namespace CLIo.Services;

/// <summary>Locates optional local voice models under %LOCALAPPDATA%/CLIo/voice.</summary>
public static class VoiceAssets
{
    public static string Directory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CLIo", "voice");

    /// <summary>Speaking speed multiplier for Piper (1.0 = normal). Set from the ViewModel.</summary>
    public static double SpeechSpeed { get; set; } = 1.0;

    /// <summary>Piper expressiveness (variation in pitch/rhythm), default 0.667.</summary>
    public static double NoiseScale { get; set; } = 0.667;

    /// <summary>Piper phoneme-width variation (cadence looseness), default 0.8.</summary>
    public static double NoiseW { get; set; } = 0.8;

    /// <summary>Seconds of pause Piper adds after each sentence.</summary>
    public static double SentenceSilence { get; set; } = 0.2;

    /// <summary>Biases Whisper toward the user's names and terms. Rebuilt from memory when the mic starts.</summary>
    public static string TranscriptionPrompt { get; set; } = string.Empty;

    /// <summary>Optional remote Whisper server (OpenAI-compatible /v1/audio/transcriptions or whisper.cpp /inference).</summary>
    public static string RemoteUrl { get; set; } = string.Empty;
    public static string RemoteModel { get; set; } = string.Empty;
    public static string RemoteKey { get; set; } = string.Empty;
    /// <summary>Random per-install id sent as X-Clio-Install so a Worker can rate-limit. Not a credential.</summary>
    public static string InstallId { get; set; } = string.Empty;

    /// <summary>Privacy switch: never send audio off this machine.</summary>
    public static bool ForceLocalOnly { get; set; }
    public static bool RemoteConfigured => !ForceLocalOnly && Uri.TryCreate(RemoteUrl, UriKind.Absolute, out _);

    public static string? FindWhisperModel() =>
        Find("ggml-*.bin", name => name.Contains("base.en") ? 0 : name.Contains(".en") ? 1 : 2);

    public static string? FindPiperExe()
    {
        if (!System.IO.Directory.Exists(Directory)) return null;
        return System.IO.Directory.EnumerateFiles(Directory, "piper.exe", SearchOption.AllDirectories).FirstOrDefault();
    }

    public static string? FindPiperVoice() =>
        Find("*.onnx", name => name.Contains("cori") ? 0 : 1);

    /// <summary>Returns a user-facing reason if Windows privacy settings or hardware block capture, else null.</summary>
    public static string? MicrophoneBlockedReason()
    {
        try
        {
            const string root = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";
            foreach (var hive in new[] { Microsoft.Win32.Registry.LocalMachine, Microsoft.Win32.Registry.CurrentUser })
            {
                using var key = hive.OpenSubKey(root);
                if (key?.GetValue("Value") as string == "Deny")
                {
                    return "Windows has microphone access turned off. Open Settings > Privacy > Microphone and turn on \"Microphone access\" and \"Let desktop apps access your microphone\".";
                }
            }

            using var desktop = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(root + @"\NonPackaged");
            if (desktop?.GetValue("Value") as string == "Deny")
            {
                return "Windows blocks desktop apps from the microphone. Open Settings > Privacy > Microphone and turn on \"Let desktop apps access your microphone\".";
            }
        }
        catch
        {
            // Registry unreadable: don't claim a block we can't confirm.
        }

        return NAudio.Wave.WaveInEvent.DeviceCount == 0
            ? "No microphone input device was found. Plug one in or enable it in Settings > System > Sound."
            : null;
    }

    private static string? Find(string pattern, Func<string, int> rank)
    {
        if (!System.IO.Directory.Exists(Directory)) return null;
        return System.IO.Directory.EnumerateFiles(Directory, pattern, SearchOption.AllDirectories)
            .OrderBy(path => rank(Path.GetFileName(path).ToLowerInvariant()))
            .FirstOrDefault();
    }
}
