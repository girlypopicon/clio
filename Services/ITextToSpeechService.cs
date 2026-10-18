namespace CLIo.Services;

public interface ITextToSpeechService : IDisposable
{
    bool IsAvailable { get; }
    bool IsSpeaking { get; }

    /// <summary>Raised when speech starts or ends (any thread).</summary>
    event Action<bool>? SpeakingChanged;

    /// <summary>Speaks the text, replacing anything currently playing.</summary>
    void Speak(string text);

    void Stop();
}
