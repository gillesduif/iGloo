using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Speech.Recognition;
using System.Speech.Synthesis;
using System.Windows.Threading;

namespace Igloo.One.App;

/// <summary>Opt-in Windows desktop speech. Dictation fills the composer for review; it never sends on its own.</summary>
internal sealed class OneVoiceSession(Dispatcher dispatcher) : IDisposable
{
    private SpeechRecognitionEngine? _recognizer;
    private SpeechSynthesizer? _synthesizer;
    private bool _speaking;

    public event Action<string>? StatusChanged;
    public event Action<string>? Transcribed;
    public event Action? ListeningStopped;
    public event Action? WordSpoken;
    public event Action? SpeechStopped;
    public bool IsListening => _recognizer is not null;
    public bool IsSpeaking => _speaking;
    public static bool HasDesktopRecognizer => SpeechRecognitionEngine.InstalledRecognizers().Count > 0;

    [SuppressMessage("Globalization", "CA1304:Specify CultureInfo",
        Justification = "The diagnostic intentionally counts installed voices across all languages.")]
    public static (int Recognizers, int Voices) InstalledSupport()
    {
        int recognizers = SpeechRecognitionEngine.InstalledRecognizers().Count;
        using var synthesizer = new SpeechSynthesizer();
        return (recognizers, synthesizer.GetInstalledVoices().Count);
    }

    public void StartDictation()
    {
        if (_recognizer is not null) return;
        var installed = SpeechRecognitionEngine.InstalledRecognizers();
        RecognizerInfo? chosen = installed.FirstOrDefault(r => r.Culture.Equals(CultureInfo.CurrentUICulture))
            ?? installed.FirstOrDefault(r => r.Culture.TwoLetterISOLanguageName ==
                CultureInfo.CurrentUICulture.TwoLetterISOLanguageName)
            ?? installed.FirstOrDefault();
        if (chosen is null)
            throw new InvalidOperationException("No Windows desktop speech recognizer is installed.");

        var engine = new SpeechRecognitionEngine(chosen);
        try
        {
            engine.LoadGrammar(new DictationGrammar());
            engine.SetInputToDefaultAudioDevice();
            engine.SpeechRecognized += OnRecognized;
            engine.SpeechRecognitionRejected += OnRejected;
            engine.RecognizeCompleted += OnRecognitionCompleted;
            _recognizer = engine;
            engine.RecognizeAsync(RecognizeMode.Single);
            StatusChanged?.Invoke($"Listening in {chosen.Culture.DisplayName}…");
        }
        catch
        {
            _recognizer = null;
            engine.Dispose();
            throw;
        }
    }

    public void StopDictation()
    {
        if (_recognizer is null) return;
        _recognizer.RecognizeAsyncCancel();
        StatusChanged?.Invoke("Stopping dictation…");
    }

    private void OnRecognized(object? sender, SpeechRecognizedEventArgs args)
    {
        if (args.Result.Confidence < .45 || string.IsNullOrWhiteSpace(args.Result.Text))
        {
            dispatcher.BeginInvoke(() => StatusChanged?.Invoke("Speech was unclear. Please try again."));
            return;
        }
        string text = args.Result.Text;
        dispatcher.BeginInvoke(() =>
        {
            Transcribed?.Invoke(text);
            StatusChanged?.Invoke("Dictation added to the message box. Review it before sending.");
        });
    }

    private void OnRejected(object? sender, SpeechRecognitionRejectedEventArgs args) =>
        dispatcher.BeginInvoke(() => StatusChanged?.Invoke("I did not catch that. Please try again."));

    private void OnRecognitionCompleted(object? sender, RecognizeCompletedEventArgs args) =>
        dispatcher.BeginInvoke(() =>
        {
            if (_recognizer is not SpeechRecognitionEngine recognizer || !ReferenceEquals(recognizer, sender)) return;
            recognizer.Dispose();
            _recognizer = null;
            ListeningStopped?.Invoke();
            if (args.Error is not null) StatusChanged?.Invoke("Dictation is unavailable on this device.");
            else if (args.Cancelled) StatusChanged?.Invoke("Dictation stopped.");
        });

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_synthesizer is null)
        {
            _synthesizer = new SpeechSynthesizer();
            _synthesizer.SetOutputToDefaultAudioDevice();
            _synthesizer.SpeakProgress += (_, _) => dispatcher.BeginInvoke(() => WordSpoken?.Invoke());
            _synthesizer.SpeakCompleted += (_, args) => dispatcher.BeginInvoke(() =>
            {
                _speaking = false;
                SpeechStopped?.Invoke();
                StatusChanged?.Invoke(args.Error is null ? "Voice playback finished." :
                    "Voice playback is unavailable on this device.");
            });
        }
        _speaking = true;
        try { _synthesizer.SpeakAsync(text); }
        catch { _speaking = false; throw; }
        StatusChanged?.Invoke("Reading Tux's reply aloud…");
    }

    public void StopSpeaking()
    {
        if (!_speaking || _synthesizer is null) return;
        _synthesizer.SpeakAsyncCancelAll();
        _speaking = false;
        SpeechStopped?.Invoke();
        StatusChanged?.Invoke("Voice playback stopped.");
    }

    public void Dispose()
    {
        if (_recognizer is not null)
        {
            _recognizer.SpeechRecognized -= OnRecognized;
            _recognizer.SpeechRecognitionRejected -= OnRejected;
            _recognizer.RecognizeCompleted -= OnRecognitionCompleted;
            _recognizer.Dispose();
            _recognizer = null;
        }
        _synthesizer?.Dispose();
        _synthesizer = null;
    }
}
