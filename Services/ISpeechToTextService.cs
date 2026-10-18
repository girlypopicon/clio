namespace CLIo.Services;

public interface ISpeechToTextService : IDisposable
{
    bool IsAvailable { get; }
    bool IsListening { get; }

    /// <summary>Raised (on a background thread) for each finalized phrase.</summary>
    event Action<string>? PhraseRecognized;

    /// <summary>Raised repeatedly while listening with the full transcript so far; replaces the previous partial. The last one is the final text.</summary>
    event Action<string>? PartialRecognized;

    /// <summary>Raised if listening stops on its own because of an error.</summary>
    event Action<string>? Failed;

    /// <summary>Starts listening on the default microphone. Returns an error message or null on success.</summary>
    string? Start();

    void Stop();

    /// <summary>Stops listening and completes once the final transcript has been delivered via PhraseRecognized.</summary>
    Task StopAndFlushAsync();

    /// <summary>Raised while background work runs (e.g. "Transcribing..."); empty when done.</summary>
    event Action<string>? StatusChanged;

    /// <summary>Diagnostic breadcrumbs for the dev log.</summary>
    event Action<string>? Diagnostic;
}
