using System.Diagnostics;
using System.Text.Json;
using NAudio.Wave;

namespace CLIo.Services;

/// <summary>
/// Local neural TTS via the Piper executable. Falls back to Windows voices when
/// piper.exe or a voice (.onnx) is not present in the voice folder.
/// </summary>
public sealed class PiperTextToSpeechService : ITextToSpeechService
{
    private readonly SystemTextToSpeechService fallback;
    private readonly object gate = new();
    private Process? process;
    private WaveOutEvent? output;
    private int generation;

    public PiperTextToSpeechService(SystemTextToSpeechService fallback)
    {
        this.fallback = fallback;
        fallback.SpeakingChanged += speaking => SpeakingChanged?.Invoke(speaking);
    }

    public bool IsAvailable => PiperReady() || fallback.IsAvailable;
    public bool IsSpeaking { get; private set; }
    public event Action<bool>? SpeakingChanged;

    private static bool PiperReady() => VoiceAssets.FindPiperExe() is not null && VoiceAssets.FindPiperVoice() is not null;

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        Stop();
        if (!PiperReady())
        {
            fallback.Speak(text);
            return;
        }

        int myGeneration;
        lock (gate) { myGeneration = ++generation; }
        _ = Task.Run(() => RunAsync(text, myGeneration));
    }

    private async Task RunAsync(string text, int myGeneration)
    {
        Process? proc = null;
        WaveOutEvent? waveOut = null;
        try
        {
            var exe = VoiceAssets.FindPiperExe()!;
            var voice = VoiceAssets.FindPiperVoice()!;
            var sampleRate = ReadSampleRate(voice);

            proc = new Process
            {
                StartInfo = new ProcessStartInfo(exe)
                {
                    WorkingDirectory = Path.GetDirectoryName(exe)!,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.StartInfo.ArgumentList.Add("--model");
            proc.StartInfo.ArgumentList.Add(voice);
            proc.StartInfo.ArgumentList.Add("--output_raw");
            // Piper's length_scale is the inverse of speed: >1 is slower.
            proc.StartInfo.ArgumentList.Add("--length_scale");
            proc.StartInfo.ArgumentList.Add((1.0 / Math.Clamp(VoiceAssets.SpeechSpeed, 0.5, 2.0)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            foreach (var (flag, value) in new[]
            {
                ("--noise_scale", VoiceAssets.NoiseScale),
                ("--noise_w", VoiceAssets.NoiseW),
                ("--sentence_silence", VoiceAssets.SentenceSilence)
            })
            {
                proc.StartInfo.ArgumentList.Add(flag);
                proc.StartInfo.ArgumentList.Add(value.ToString("0.000", System.Globalization.CultureInfo.InvariantCulture));
            }

            var buffer = new BufferedWaveProvider(new WaveFormat(sampleRate, 16, 1))
            {
                BufferDuration = TimeSpan.FromMinutes(10),
                ReadFully = false
            };
            waveOut = new WaveOutEvent();
            waveOut.Init(buffer);

            // Lead-in silence: the audio device takes a moment to wake up (worse on Bluetooth)
            // and would otherwise swallow the first word or two.
            var leadInBytes = sampleRate * 450 / 1000 * 2; // whole 16-bit samples only, or everything after it is static
            buffer.AddSamples(new byte[leadInBytes], 0, leadInBytes);

            lock (gate)
            {
                if (myGeneration != generation) return;
                process = proc;
                output = waveOut;
            }

            proc.Start();
            _ = proc.StandardError.ReadToEndAsync(); // drain so Piper never blocks on stderr
            SetSpeaking(true);

            await proc.StandardInput.WriteAsync(text.Replace('\n', ' '));
            proc.StandardInput.Close();

            var started = false;
            var chunk = new byte[8192];
            var carry = 0; // leftover odd byte from the previous read
            int read;
            while ((read = await proc.StandardOutput.BaseStream.ReadAsync(chunk.AsMemory(carry))) > 0)
            {
                if (myGeneration != generation) return;
                var total = carry + read;
                var usable = total & ~1;
                buffer.AddSamples(chunk, 0, usable);
                carry = total - usable;
                if (carry == 1) chunk[0] = chunk[usable];
                if (!started && buffer.BufferedBytes > leadInBytes + sampleRate) // silence + ~0.5s of speech before playback
                {
                    waveOut.Play();
                    started = true;
                }
            }

            if (myGeneration != generation) return;
            if (!started) waveOut.Play();
            while (buffer.BufferedBytes > 0 && myGeneration == generation)
            {
                await Task.Delay(100);
            }

            await Task.Delay(250);
        }
        catch
        {
            // Playback problems are non-fatal; speech just ends.
        }
        finally
        {
            try { if (proc is { HasExited: false }) proc.Kill(true); } catch { }
            proc?.Dispose();
            waveOut?.Dispose();
            var current = false;
            lock (gate)
            {
                current = myGeneration == generation;
                if (current)
                {
                    process = null;
                    output = null;
                }
            }

            if (current) SetSpeaking(false);
        }
    }

    private static int ReadSampleRate(string voicePath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(voicePath + ".json"));
            return doc.RootElement.GetProperty("audio").GetProperty("sample_rate").GetInt32();
        }
        catch
        {
            return 22050;
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            generation++;
            try { process?.Kill(true); } catch { }
            try { output?.Stop(); } catch { }
        }

        fallback.Stop();
        SetSpeaking(false);
    }

    private void SetSpeaking(bool value)
    {
        if (IsSpeaking == value) return;
        IsSpeaking = value;
        SpeakingChanged?.Invoke(value);
    }

    public void Dispose()
    {
        Stop();
        fallback.Dispose();
    }
}
