using System.Diagnostics;
using NAudio.Wave;
using Whisper.net;

namespace CLIo.Services;

/// <summary>
/// Whisper dictation. While the mic is on, speech is cut into utterances at natural pauses.
/// Each finished utterance is transcribed once in the background (by the Whisper server when
/// configured, otherwise the local model), and the utterance still being spoken is shown live
/// by the fast local model. Stopping only has to wait for the last short utterance.
/// Falls back to the Windows recognizer when no Whisper model or server is available.
/// </summary>
public sealed class WhisperSpeechToTextService : ISpeechToTextService
{
    private const int SampleRate = 16000;
    private const int BytesPerSecond = SampleRate * 2;
    private const int PauseMs = 1300;           // silence that ends an utterance (a thinking pause must not end it)
    private const int MinSpeechMs = 300;        // a chunk needs this much real speech energy...
    private const double MinPeakRms = 600;      // ...reaching this level, or it is treated as noise
    private const double MinChunkSeconds = 1.2; // don't cut tiny fragments
    private const double MaxChunkSeconds = 22;  // force a cut so one chunk never runs long
    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private static readonly TimeSpan RemoteTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan RemoteCooldown = TimeSpan.FromSeconds(60);
    private static DateTime remoteDownUntil = DateTime.MinValue;

    private readonly SystemSpeechToTextService fallback;
    private readonly object gate = new();
    private readonly SemaphoreSlim inference = new(1, 1);
    private WhisperFactory? factory;
    private string? factoryModel;
    private WaveInEvent? waveIn;
    private Session? session;
    private bool usingFallback;
    private Task transcription = Task.CompletedTask;

    private sealed class Chunk
    {
        public string Text = string.Empty;
        public bool Final;
    }

    private sealed class Session
    {
        public readonly MemoryStream Pcm = new();
        public readonly List<Chunk> Chunks = new();
        public readonly List<Task> Commits = new();
        public readonly CancellationTokenSource Cts = new();
        public int CommittedBytes;
        public int SilentMs;
        public int SpeechMs;
        public double PeakRms;
        public bool ForceSpeech;
        public bool HeardSpeech => ForceSpeech || (SpeechMs >= 300 && PeakRms >= 600);
        public double NoiseFloor = 200;
        public string Live = string.Empty;
        public string Engine = "local Whisper";
        public long LiveVersion;
    }

    public WhisperSpeechToTextService(SystemSpeechToTextService fallback)
    {
        this.fallback = fallback;
        fallback.PhraseRecognized += text => PhraseRecognized?.Invoke(text);
        fallback.Failed += message => Failed?.Invoke(message);
        fallback.Diagnostic += message => Diagnostic?.Invoke(message);
    }

    public bool IsAvailable => VoiceAssets.FindWhisperModel() is not null || VoiceAssets.RemoteConfigured || fallback.IsAvailable;
    public bool IsListening => usingFallback ? fallback.IsListening : waveIn is not null;

    public event Action<string>? PhraseRecognized;
    public event Action<string>? Failed;
    public event Action<string>? PartialRecognized;
    public event Action<string>? StatusChanged;
    public event Action<string>? Diagnostic;

    public string? Start()
    {
        lock (gate)
        {
            if (IsListening) return null;

            var blocked = VoiceAssets.MicrophoneBlockedReason();
            if (blocked is not null)
            {
                Diagnostic?.Invoke($"Mic blocked: {blocked}");
                return blocked;
            }

            var model = VoiceAssets.FindWhisperModel();
            if (model is null && !VoiceAssets.RemoteConfigured)
            {
                Diagnostic?.Invoke($"No Whisper model (ggml-*.bin) in {VoiceAssets.Directory}; using Windows recognizer.");
                usingFallback = true;
                return fallback.Start();
            }

            usingFallback = false;
            Diagnostic?.Invoke($"Whisper recording, local model {(model is null ? "none" : Path.GetFileName(model))}, remote {(VoiceAssets.RemoteConfigured ? VoiceAssets.RemoteUrl : "off")}, prompt '{VoiceAssets.TranscriptionPrompt}'");
            try
            {
                var s = new Session();
                session = s;
                waveIn = new WaveInEvent { WaveFormat = new WaveFormat(SampleRate, 16, 1), BufferMilliseconds = 100 };
                waveIn.DataAvailable += (_, e) => OnAudio(s, e.Buffer, e.BytesRecorded);
                waveIn.RecordingStopped += (_, e) =>
                {
                    if (e.Exception is not null)
                    {
                        Diagnostic?.Invoke($"Audio capture error: {e.Exception}");
                        Failed?.Invoke($"Audio capture error: {e.Exception.Message}");
                    }
                };
                waveIn.StartRecording();
                _ = WarmUpAsync();
                _ = LiveLoopAsync(s);
                return null;
            }
            catch (Exception ex)
            {
                waveIn?.Dispose();
                waveIn = null;
                session = null;
                return $"Could not start microphone: {ex.Message}";
            }
        }
    }

    public void Stop() => _ = StopAndFlushAsync();

    public Task StopAndFlushAsync()
    {
        lock (gate)
        {
            if (usingFallback)
            {
                return fallback.StopAndFlushAsync();
            }

            if (waveIn is null || session is null)
            {
                return transcription;
            }

            var s = session;
            session = null;
            s.Cts.Cancel();
            try { waveIn.StopRecording(); } catch { }
            waveIn.Dispose();
            waveIn = null;
            transcription = FinishAsync(s);
            return transcription;
        }
    }

    // ---- recording: cut utterances at pauses -------------------------------------------------

    private void OnAudio(Session s, byte[] buffer, int count)
    {
        lock (s)
        {
            s.Pcm.Write(buffer, 0, count);

            double sum = 0;
            var samples = count / 2;
            for (var i = 0; i + 1 < count; i += 2)
            {
                double v = BitConverter.ToInt16(buffer, i);
                sum += v * v;
            }

            var rms = samples > 0 ? Math.Sqrt(sum / samples) : 0;
            var threshold = Math.Max(450, s.NoiseFloor * 3);
            if (rms > threshold)
            {
                s.SpeechMs += count * 1000 / BytesPerSecond;
                s.PeakRms = Math.Max(s.PeakRms, rms);
                s.SilentMs = 0;
            }
            else
            {
                s.SilentMs += count * 1000 / BytesPerSecond;
                s.NoiseFloor = s.NoiseFloor * 0.95 + rms * 0.05;
            }

            var chunkBytes = (int)s.Pcm.Length - s.CommittedBytes;
            var longEnough = chunkBytes >= MinChunkSeconds * BytesPerSecond;
            if (s.HeardSpeech && longEnough && (s.SilentMs >= PauseMs || chunkBytes >= MaxChunkSeconds * BytesPerSecond))
            {
                CutChunk(s, endOfRecording: false);
            }
        }
    }

    // Caller holds lock(s). Starts a background transcription of everything since the last cut.
    private void CutChunk(Session s, bool endOfRecording)
    {
        var all = s.Pcm.GetBuffer();
        var length = (int)s.Pcm.Length;
        var slice = new byte[length - s.CommittedBytes];
        Buffer.BlockCopy(all, s.CommittedBytes, slice, 0, slice.Length);
        s.CommittedBytes = length;
        var hadSpeech = s.HeardSpeech;
        s.SpeechMs = 0;
        s.PeakRms = 0;
        s.ForceSpeech = false;
        s.SilentMs = 0;

        if (!hadSpeech || slice.Length < 0.3 * BytesPerSecond)
        {
            s.Live = string.Empty;
            return; // silence only: Whisper would hallucinate on it
        }

        // Show the live text for this utterance until the better transcript arrives.
        var prompt = BuildPrompt(s);
        var chunk = new Chunk { Text = s.Live, Final = false };
        s.Live = string.Empty;
        s.LiveVersion++;
        s.Chunks.Add(chunk);
        s.Commits.Add(CommitAsync(s, chunk, slice, prompt, endOfRecording));
    }

    private async Task CommitAsync(Session s, Chunk chunk, byte[] audio, string prompt, bool endOfRecording)
    {
        var watch = Stopwatch.StartNew();
        try
        {
            var text = TranscriptFilter.Apply(await InferAsync(audio, s, prompt));
            lock (s)
            {
                chunk.Text = text;
                chunk.Final = true;
            }

            Diagnostic?.Invoke($"Utterance {audio.Length / (double)BytesPerSecond:0.0}s transcribed by {s.Engine} in {watch.ElapsedMilliseconds} ms: '{text}'");
            if (!endOfRecording && !s.Cts.IsCancellationRequested)
            {
                PartialRecognized?.Invoke(Compose(s));
            }
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"Utterance transcription failed after {watch.ElapsedMilliseconds} ms: {ex.Message}");
            lock (s) { chunk.Final = true; } // keep whatever live text we had
            Failed?.Invoke($"Whisper failed: {ex.Message}");
        }
    }

    // Vocabulary hint plus the tail of what was already said, so Whisper keeps names and the thread of the sentence.
    private static string BuildPrompt(Session s)
    {
        var vocab = VoiceAssets.TranscriptionPrompt;
        if (vocab.Length > 350) vocab = vocab[..350];
        var said = Compose(s, includeLive: false);
        var tail = said.Length > 120 ? said[^120..] : said;
        return $"{vocab} {tail}".Trim();
    }

    private static string Compose(Session s, bool includeLive = true)
    {
        lock (s)
        {
            var parts = s.Chunks.Select(chunk => chunk.Text).ToList();
            if (includeLive) parts.Add(s.Live);
            return TranscriptFilter.JoinUtterances(parts);
        }
    }

    // ---- live text: the local model transcribes only the utterance being spoken ---------------

    private async Task LiveLoopAsync(Session s)
    {
        try
        {
            while (!s.Cts.IsCancellationRequested)
            {
                await Task.Delay(900, s.Cts.Token);
                var model = VoiceAssets.FindWhisperModel();
                if (model is null) continue; // remote-only: no live words, avoid queueing slow requests

                byte[] audio;
                long version;
                string livePrompt;
                lock (s)
                {
                    if (!s.HeardSpeech) continue;
                    var length = (int)s.Pcm.Length - s.CommittedBytes;
                    if (length < 0.8 * BytesPerSecond) continue;
                    audio = new byte[length];
                    Buffer.BlockCopy(s.Pcm.GetBuffer(), s.CommittedBytes, audio, 0, length);
                    version = s.LiveVersion;
                    livePrompt = BuildPrompt(s);
                }

                if (!await inference.WaitAsync(0, s.Cts.Token)) continue;
                try
                {
                    var text = TranscriptFilter.Apply(await InferLocalCoreAsync(audio, model, livePrompt));
                    lock (s)
                    {
                        // Drop the result if the utterance was cut while we were transcribing.
                        if (version != s.LiveVersion || s.Cts.IsCancellationRequested) continue;
                        s.Live = text;
                    }

                    PartialRecognized?.Invoke(Compose(s));
                }
                finally
                {
                    inference.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal: mic was stopped.
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"Live transcription stopped: {ex.Message}");
        }
    }

    // ---- stopping ----------------------------------------------------------------------------

    private async Task FinishAsync(Session s)
    {
        var total = Stopwatch.StartNew();
        try
        {
            byte[] all;
            lock (s)
            {
                all = s.Pcm.ToArray();
                if (all.Length < BytesPerSecond * 0.4)
                {
                    Diagnostic?.Invoke($"Recording too short ({all.Length} bytes).");
                    PartialRecognized?.Invoke(string.Empty);
                    StatusChanged?.Invoke("Recording was too short - keep the mic on a bit longer.");
                    return;
                }
            }

            var peak = 0;
            for (var i = 0; i + 1 < all.Length; i += 2)
            {
                peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(all, i)));
            }

            if (peak == 0)
            {
                Diagnostic?.Invoke("Recorded audio is pure silence (all zero samples).");
                Failed?.Invoke("The microphone returned only silence. Check Settings > Privacy > Microphone (allow desktop apps) and that the right input device is the default.");
                return;
            }

            StatusChanged?.Invoke("Transcribing...");
            lock (s)
            {
                // Whatever was said since the last pause becomes the final short utterance.
                if (!s.HeardSpeech && s.SpeechMs >= 150 && s.PeakRms >= 500) s.ForceSpeech = true; // a short last word still counts
                CutChunk(s, endOfRecording: true);
            }

            Task[] pending;
            lock (s) { pending = s.Commits.ToArray(); }
            await Task.WhenAll(pending);

            var result = Compose(s, includeLive: false);
            Diagnostic?.Invoke($"Final text ready {total.ElapsedMilliseconds} ms after stop: '{result}'");
            PartialRecognized?.Invoke(result);
            StatusChanged?.Invoke(result.Length == 0 ? "Didn't catch any speech." : $"Transcribed by {s.Engine}.");
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"Whisper failed: {ex}");
            Failed?.Invoke($"Whisper failed: {ex.Message}");
        }
        finally
        {
            StatusChanged?.Invoke("​");
        }
    }

    // ---- engines -----------------------------------------------------------------------------

    // Loads the local model while the user is still talking, so nothing waits on it later.
    private async Task WarmUpAsync()
    {
        var model = VoiceAssets.FindWhisperModel();
        if (model is null || (factory is not null && factoryModel == model)) return;
        await inference.WaitAsync();
        try
        {
            EnsureFactory(model);
        }
        catch (Exception ex)
        {
            Diagnostic?.Invoke($"Whisper warm-up failed: {ex}");
            StatusChanged?.Invoke("​");
        }
        finally
        {
            inference.Release();
        }
    }

    private void EnsureFactory(string model)
    {
        if (factory is not null && factoryModel == model) return;
        StatusChanged?.Invoke("Loading Whisper model...");
        var started = Environment.TickCount64;
        factory?.Dispose();
        factory = WhisperFactory.FromPath(model);
        factoryModel = model;
        Diagnostic?.Invoke($"Whisper model loaded in {Environment.TickCount64 - started} ms.");
        StatusChanged?.Invoke("​");
    }

    // Best available engine for a finished utterance: the server if configured, else the local model.
    private async Task<string> InferAsync(byte[] audio, Session s, string prompt)
    {
        var model = VoiceAssets.FindWhisperModel();
        if (VoiceAssets.RemoteConfigured && (model is null || DateTime.UtcNow >= remoteDownUntil))
        {
            try
            {
                var text = await InferRemoteAsync(audio, prompt);
                s.Engine = "Whisper server";
                return text;
            }
            catch (Exception ex)
            {
                Diagnostic?.Invoke($"Remote Whisper failed: {ex.Message}");
                if (model is null) throw;

                // Don't make every following clip wait for a server that just failed.
                remoteDownUntil = DateTime.UtcNow + RemoteCooldown;
                var reason = ex is HttpRequestException { StatusCode: null } ? "can't connect"
                    : ex is TaskCanceledException ? "timed out"
                    : ex.Message.Contains("HTTP 401") || ex.Message.Contains("HTTP 403") ? "API key rejected"
                    : ex.Message;
                StatusChanged?.Invoke($"Server failed: {reason}. Using local Whisper...");
            }
        }

        s.Engine = "local Whisper";
        await inference.WaitAsync();
        try
        {
            return await InferLocalCoreAsync(audio, model!, prompt);
        }
        finally
        {
            inference.Release();
        }
    }

    private async Task<string> InferRemoteAsync(byte[] audio, string prompt)
    {
        // The Worker rejects clips under ~1 KB with a 400, and a quarter-second of audio is
        // too short to hold a word anyway (16 kHz mono 16-bit = 32,000 bytes per second).
        if (audio.Length < 8000)
        {
            return string.Empty;
        }

        // The Worker requires a UUID install id for rate limiting; never send without one.
        if (!Guid.TryParse(VoiceAssets.InstallId, out _))
        {
            VoiceAssets.InstallId = Guid.NewGuid().ToString();
        }

        var wav = new MemoryStream();
        using (var writer = new WaveFileWriter(wav, new WaveFormat(SampleRate, 16, 1)))
        {
            writer.Write(audio, 0, audio.Length);
        }

        var uri = new Uri(VoiceAssets.RemoteUrl);
        if (uri.AbsolutePath is "" or "/")
        {
            uri = new Uri(uri, "/v1/audio/transcriptions");
        }

        // Hand-built multipart body: .NET's MultipartFormDataContent adds per-part headers
        // (Content-Length, filename*) that the Cloudflare Worker's parser rejects with a 400.
        var boundary = "clio" + Guid.NewGuid().ToString("N");
        using var multipart = new MemoryStream();

        void WriteText(string text) => multipart.Write(System.Text.Encoding.UTF8.GetBytes(text));
        void WriteField(string name, string value) =>
            WriteText($"--{boundary}\r\nContent-Disposition: form-data; name=\"{name}\"\r\n\r\n{value}\r\n");

        WriteField("response_format", "json");
        WriteField("language", "en");
        if (!string.IsNullOrWhiteSpace(VoiceAssets.RemoteModel)) WriteField("model", VoiceAssets.RemoteModel);
        if (!string.IsNullOrWhiteSpace(prompt)) WriteField("prompt", prompt.Replace('\r', ' ').Replace('\n', ' '));
        WriteText($"--{boundary}\r\nContent-Disposition: form-data; name=\"file\"; filename=\"audio.wav\"\r\nContent-Type: audio/wav\r\n\r\n");
        multipart.Write(wav.ToArray());
        WriteText($"\r\n--{boundary}--\r\n");

        var content = new ByteArrayContent(multipart.ToArray());
        content.Headers.TryAddWithoutValidation("Content-Type", $"multipart/form-data; boundary={boundary}");

        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = content };
        if (!string.IsNullOrEmpty(VoiceAssets.InstallId)) request.Headers.Add("X-Clio-Install", VoiceAssets.InstallId);
        if (!string.IsNullOrWhiteSpace(VoiceAssets.RemoteKey))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", VoiceAssets.RemoteKey);
        }

        // A healthy Worker answers a short clip in 1-4 s; anything slower means fall back to local.
        using var timeout = new CancellationTokenSource(RemoteTimeout);
        using var response = await http.SendAsync(request, timeout.Token);
        var body = await response.Content.ReadAsStringAsync(timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 160)]}");
        }

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        return Clean(doc.RootElement.GetProperty("text").GetString() ?? string.Empty);
    }

    // Caller must hold the inference semaphore.
    private async Task<string> InferLocalCoreAsync(byte[] audio, string model, string prompt)
    {
        EnsureFactory(model);

        var wav = new MemoryStream();
        using (var writer = new WaveFileWriter(wav, new WaveFormat(SampleRate, 16, 1)))
        {
            writer.Write(audio, 0, audio.Length);
        }

        var english = Path.GetFileName(model).Contains(".en", StringComparison.OrdinalIgnoreCase);
        var builder = factory!.CreateBuilder().WithLanguage(english ? "en" : "auto");
        if (!string.IsNullOrWhiteSpace(prompt))
        {
            builder = builder.WithPrompt(prompt);
        }

        await using var processor = builder.Build();
        var text = new System.Text.StringBuilder();
        await foreach (var segment in processor.ProcessAsync(new MemoryStream(wav.ToArray())))
        {
            text.Append(segment.Text);
        }

        return Clean(text.ToString());
    }

    // Drop non-speech annotations like [BLANK_AUDIO], (music), [typing].
    private static string Clean(string raw)
    {
        var cleaned = System.Text.RegularExpressions.Regex.Replace(raw, @"\[[^\]]*\]|\([^)]*\)", " ");
        return System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();
    }

    public void Dispose()
    {
        session?.Cts.Cancel();
        waveIn?.Dispose();
        factory?.Dispose();
        fallback.Dispose();
    }
}
