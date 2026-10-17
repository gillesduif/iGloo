using System.Diagnostics.CodeAnalysis;
using System.Speech.Synthesis;
using System.Windows.Threading;

namespace Igloo.One.App;

/// <summary>Local opt-in microphone dictation and Windows speech playback for Tux.</summary>
internal sealed class OneVoiceSession(Dispatcher dispatcher) : IAsyncDisposable
{
    private readonly OneOfflineDictation _dictation = new(dispatcher);
    private SpeechSynthesizer? _synthesizer;
    private Prompt? _currentPrompt;
    private bool _speaking;
    private bool _eventsConnected;

    public event Action<string>? StatusChanged;
    public event Action<string>? Transcribed;
    public event Action? DictationStateChanged;
    public event Action<double>? MicrophoneLevelChanged;
    public event Action? MaximumDurationReached;
    public event Action? WordSpoken;
    public event Action? SpeechStopped;
    public bool IsPreparing => _dictation.IsPreparing;
    public bool IsListening => _dictation.IsListening;
    public bool IsTranscribing => _dictation.IsTranscribing;
    public bool IsSpeaking => _speaking;

    [SuppressMessage("Globalization", "CA1304:Specify CultureInfo",
        Justification = "The diagnostic counts installed voices across all languages.")]
    public static (int Microphones, int Voices) InstalledSupport()
    {
        using var synthesizer = new SpeechSynthesizer();
        return (OneOfflineDictation.MicrophoneCount, synthesizer.GetInstalledVoices().Count);
    }

    public async Task StartDictationAsync(CancellationToken cancellationToken)
    {
        ConnectDictationEvents();
        await _dictation.StartAsync(cancellationToken).ConfigureAwait(true);
    }

    public void CancelDictationPreparation() => _dictation.CancelPreparation();

    public async Task StopDictationAsync(CancellationToken cancellationToken)
    {
        string? text = await _dictation.StopAsync(cancellationToken).ConfigureAwait(true);
        if (!string.IsNullOrWhiteSpace(text)) Transcribed?.Invoke(text);
    }

    private void ConnectDictationEvents()
    {
        if (_eventsConnected) return;
        _eventsConnected = true;
        _dictation.StatusChanged += status => StatusChanged?.Invoke(status);
        _dictation.StateChanged += () => DictationStateChanged?.Invoke();
        _dictation.MicrophoneLevelChanged += level => MicrophoneLevelChanged?.Invoke(level);
        _dictation.MaximumDurationReached += () => MaximumDurationReached?.Invoke();
    }

    public void Speak(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        if (_synthesizer is null)
        {
            _synthesizer = new SpeechSynthesizer();
            _synthesizer.SetOutputToDefaultAudioDevice();
            _synthesizer.SpeakProgress += (_, args) => dispatcher.BeginInvoke(() =>
            {
                if (ReferenceEquals(args.Prompt, _currentPrompt)) WordSpoken?.Invoke();
            });
            _synthesizer.SpeakCompleted += (_, args) => dispatcher.BeginInvoke(() =>
            {
                if (!ReferenceEquals(args.Prompt, _currentPrompt)) return;
                _currentPrompt = null;
                _speaking = false;
                SpeechStopped?.Invoke();
                StatusChanged?.Invoke(args.Error is null ? "Tux finished speaking." :
                    "Voice playback is unavailable on this device.");
            });
        }
        _synthesizer.SpeakAsyncCancelAll();
        _currentPrompt = _synthesizer.SpeakAsync(text);
        _speaking = true;
        StatusChanged?.Invoke("Tux is speaking…");
    }

    public void StopSpeaking()
    {
        if (!_speaking || _synthesizer is null) return;
        _currentPrompt = null;
        _synthesizer.SpeakAsyncCancelAll();
        _speaking = false;
        SpeechStopped?.Invoke();
        StatusChanged?.Invoke("Tux is quiet.");
    }

    public async ValueTask DisposeAsync()
    {
        await _dictation.DisposeAsync().ConfigureAwait(true);
        _synthesizer?.Dispose();
        _synthesizer = null;
    }
}
