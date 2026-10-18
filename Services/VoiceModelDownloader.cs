using System.IO.Compression;

namespace CLIo.Services;

/// <summary>
/// Downloads the optional local voice models (Whisper + Piper) into %LOCALAPPDATA%/CLIo/voice.
/// Only runs when the user explicitly asks for it from Settings.
/// </summary>
public sealed class VoiceModelDownloader
{
    private const string WhisperUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.en.bin";
    private const string PiperZipUrl = "https://github.com/rhasspy/piper/releases/download/2023.11.14-2/piper_windows_amd64.zip";
    private const string VoiceBase = "https://huggingface.co/rhasspy/piper-voices/resolve/main/en/en_GB/cori/high/en_GB-cori-high.onnx";

    // Rough sizes, used for the permission text and to reject truncated downloads.
    public const string TotalSizeText = "about 285 MB";
    private const long MinWhisperBytes = 100_000_000;
    private const long MinVoiceBytes = 80_000_000;
    private const long MinPiperZipBytes = 10_000_000;

    private static readonly HttpClient http = new() { Timeout = TimeSpan.FromMinutes(30) };

    public bool WhisperInstalled => VoiceAssets.FindWhisperModel() is not null;
    public bool PiperInstalled => VoiceAssets.FindPiperExe() is not null && VoiceAssets.FindPiperVoice() is not null;
    public bool AllInstalled => WhisperInstalled && PiperInstalled;

    public string DescribeStatus() =>
        $"Whisper (dictation): {(WhisperInstalled ? "installed" : "not installed")}  |  Piper voice (speech): {(PiperInstalled ? "installed" : "not installed")}";

    public async Task DownloadMissingAsync(IProgress<string> progress, CancellationToken token)
    {
        Directory.CreateDirectory(VoiceAssets.Directory);

        if (!WhisperInstalled)
        {
            await DownloadFileAsync(WhisperUrl, Path.Combine(VoiceAssets.Directory, "ggml-base.en.bin"),
                MinWhisperBytes, "Whisper model", progress, token);
        }

        if (VoiceAssets.FindPiperVoice() is null)
        {
            var voiceDir = Path.Combine(VoiceAssets.Directory, "piper-voice");
            Directory.CreateDirectory(voiceDir);
            await DownloadFileAsync(VoiceBase + ".json", Path.Combine(voiceDir, "en_GB-cori-high.onnx.json"),
                100, "voice settings", progress, token);
            await DownloadFileAsync(VoiceBase, Path.Combine(voiceDir, "en_GB-cori-high.onnx"),
                MinVoiceBytes, "Cori voice", progress, token);
        }

        if (VoiceAssets.FindPiperExe() is null)
        {
            var zipPath = Path.Combine(VoiceAssets.Directory, "piper.zip.download");
            await DownloadFileAsync(PiperZipUrl, zipPath, MinPiperZipBytes, "Piper engine", progress, token);
            progress.Report("Unpacking Piper engine...");
            var target = Path.Combine(VoiceAssets.Directory, "piper-bin");
            if (Directory.Exists(target)) Directory.Delete(target, true);
            ZipFile.ExtractToDirectory(zipPath, target);
            File.Delete(zipPath);
        }

        progress.Report("Voice models installed.");
    }

    private static async Task DownloadFileAsync(string url, string destination, long minBytes, string label,
        IProgress<string> progress, CancellationToken token)
    {
        var partial = destination + ".part";
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;

            await using (var source = await response.Content.ReadAsStreamAsync(token))
            await using (var file = File.Create(partial))
            {
                var buffer = new byte[81920];
                long done = 0;
                var lastReport = Environment.TickCount64;
                int read;
                while ((read = await source.ReadAsync(buffer, token)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), token);
                    done += read;
                    if (Environment.TickCount64 - lastReport > 400)
                    {
                        lastReport = Environment.TickCount64;
                        progress.Report(total > 0
                            ? $"Downloading {label}... {done * 100 / total}% ({done / 1_000_000} of {total / 1_000_000} MB)"
                            : $"Downloading {label}... {done / 1_000_000} MB");
                    }
                }
            }

            if (new FileInfo(partial).Length < minBytes)
            {
                throw new InvalidDataException($"{label} download looks incomplete.");
            }

            File.Move(partial, destination, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); } catch { }
        }
    }
}
