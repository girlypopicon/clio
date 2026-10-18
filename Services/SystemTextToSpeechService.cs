using System.Speech.Synthesis;

namespace CLIo.Services;

/// <summary>Local text-to-speech using the Windows built-in voices (System.Speech).</summary>
public sealed class SystemTextToSpeechService : ITextToSpeechService
{
    private SpeechSynthesizer? synth;
    private bool initFailed;

    public bool IsAvailable => GetSynth() is not null;
    public bool IsSpeaking { get; private set; }
    public event Action<bool>? SpeakingChanged;

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var s = GetSynth();
        if (s is null)
        {
            return;
        }

        try
        {
            s.SpeakAsyncCancelAll();
            SetSpeaking(true);
            s.SpeakAsync(text);
        }
        catch
        {
            SetSpeaking(false);
        }
    }

    public void Stop()
    {
        try { synth?.SpeakAsyncCancelAll(); } catch { }
        SetSpeaking(false);
    }

    private SpeechSynthesizer? GetSynth()
    {
        if (synth is not null || initFailed)
        {
            return synth;
        }

        try
        {
            synth = new SpeechSynthesizer();
            synth.SetOutputToDefaultAudioDevice();
            synth.SpeakCompleted += (_, _) => SetSpeaking(false);
        }
        catch
        {
            initFailed = true;
            synth = null;
        }

        return synth;
    }

    private void SetSpeaking(bool value)
    {
        if (IsSpeaking == value)
        {
            return;
        }

        IsSpeaking = value;
        SpeakingChanged?.Invoke(value);
    }

    public void Dispose()
    {
        Stop();
        synth?.Dispose();
        synth = null;
    }
}
