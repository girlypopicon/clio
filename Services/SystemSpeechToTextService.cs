using System.Globalization;
using System.Speech.Recognition;

namespace CLIo.Services;

/// <summary>Local dictation using the Windows desktop speech recognizer (System.Speech).</summary>
public sealed class SystemSpeechToTextService : ISpeechToTextService
{
    private readonly object gate = new();
    private SpeechRecognitionEngine? engine;

    public bool IsAvailable
    {
        get
        {
            try { return SpeechRecognitionEngine.InstalledRecognizers().Count > 0; }
            catch { return false; }
        }
    }

    public bool IsListening { get; private set; }

    public event Action<string>? PhraseRecognized;
    public event Action<string>? PartialRecognized { add { } remove { } }
    public event Action<string>? Failed;
    public event Action<string>? StatusChanged;
    public event Action<string>? Diagnostic;

    public async Task StopAndFlushAsync()
    {
        Stop();
        await Task.Delay(700);
    }

    public string? Start()
    {
        lock (gate)
        {
            if (IsListening)
            {
                return null;
            }

            try
            {
                var recognizers = SpeechRecognitionEngine.InstalledRecognizers();
                if (recognizers.Count == 0)
                {
                    return "No Windows speech recognizer is installed (Settings > Time & Language > Speech).";
                }

                var info = recognizers.FirstOrDefault(r => r.Culture.Name.Equals(CultureInfo.CurrentUICulture.Name, StringComparison.OrdinalIgnoreCase))
                           ?? recognizers.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName == CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
                           ?? recognizers[0];

                Diagnostic?.Invoke($"Windows recognizer: {info.Culture.Name}");
                engine = new SpeechRecognitionEngine(info);
                engine.LoadGrammar(new DictationGrammar());
                engine.SetInputToDefaultAudioDevice();
                engine.SpeechRecognized += OnSpeechRecognized;
                engine.RecognizeCompleted += OnRecognizeCompleted;
                engine.RecognizeAsync(RecognizeMode.Multiple);
                IsListening = true;
                return null;
            }
            catch (Exception ex)
            {
                Teardown();
                Diagnostic?.Invoke($"Windows recognizer failed to start: {ex}");
                return $"Could not start microphone: {ex.Message}";
            }
        }
    }

    public void Stop()
    {
        lock (gate)
        {
            if (!IsListening || engine is null)
            {
                return;
            }

            try { engine.RecognizeAsyncStop(); }
            catch { Teardown(); }
            IsListening = false;
        }
    }

    private void OnSpeechRecognized(object? sender, SpeechRecognizedEventArgs e)
    {
        var text = e.Result.Text?.Trim();
        if (!string.IsNullOrEmpty(text) && e.Result.Confidence >= 0.3f)
        {
            PhraseRecognized?.Invoke(text);
        }
    }

    private void OnRecognizeCompleted(object? sender, RecognizeCompletedEventArgs e)
    {
        var wasListening = IsListening;
        lock (gate)
        {
            IsListening = false;
            Teardown();
        }

        if (e.Error is not null && wasListening)
        {
            Failed?.Invoke(e.Error.Message);
        }
    }

    private void Teardown()
    {
        if (engine is null)
        {
            return;
        }

        engine.SpeechRecognized -= OnSpeechRecognized;
        engine.RecognizeCompleted -= OnRecognizeCompleted;
        try { engine.Dispose(); } catch { }
        engine = null;
    }

    public void Dispose()
    {
        lock (gate)
        {
            IsListening = false;
            Teardown();
        }
    }
}
