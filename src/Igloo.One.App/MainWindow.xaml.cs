using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Threading;
using Igloo.Mascot.Core;

namespace Igloo.One.App;

[SuppressMessage("Maintainability", "CA1515:Consider making public types internal",
    Justification = "WPF XAML-generated partial class is public.")]
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable",
    Justification = "WPF owns Window lifetime; Closed cancels and disposes the speech session and token source.")]
public partial class MainWindow : Window
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly OneChatViewModel _chat;
    private readonly OneVoiceSession _voice;
    private OneBackendClient? _backend;
    private bool _started;
    private bool _voiceEnabled = true;

    internal bool AutoVoice { get; set; } = true;
    internal void ShowSpeechCapturePose()
    {
        MainAvatar.Talk(3);
        MainAvatar.WordBoundary();
        TalkingStatus.Visibility = Visibility.Visible;
    }

    internal bool IsSystemCheckRunning => _chat.IsChecking;
    internal string SystemCheckStatus => _chat.Status;
    internal IReadOnlyList<OneChatMessage> ChatMessages => _chat.Messages;

    public MainWindow()
    {
        InitializeComponent();
        _chat = new(
            ct => (_backend ??= OneBackendClient.Create()).InspectSystemAsync(ct),
            ct => (_backend ??= OneBackendClient.Create()).ListDistrosAsync(ct));
        _voice = new OneVoiceSession(Dispatcher);
        DataContext = _chat;
        _chat.CheckFinished += blockers =>
        {
            if (blockers) MainAvatar.Trigger(MascotAction.Think);
        };
        _chat.Messages.CollectionChanged += (_, args) =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => TranscriptScroll.ScrollToEnd());
            if (args.NewItems is null) return;
            foreach (object item in args.NewItems)
                if (item is OneChatMessage { IsTux: true } message)
                    Dispatcher.BeginInvoke(DispatcherPriority.Background, () => SpeakAssistant(message.Text));
        };
        _voice.StatusChanged += status => VoiceStatus.Text = status;
        _voice.Transcribed += text =>
        {
            MessageBox.Text = string.IsNullOrWhiteSpace(MessageBox.Text)
                ? text : MessageBox.Text.TrimEnd() + " " + text;
            MessageBox.Focus();
            MessageBox.CaretIndex = MessageBox.Text.Length;
        };
        _voice.WordSpoken += () => MainAvatar.WordBoundary();
        _voice.DictationStateChanged += UpdateMicrophoneState;
        _voice.MicrophoneLevelChanged += level =>
            MicRing.Opacity = _voice.IsListening ? .25 + .7 * level : 0;
        _voice.MaximumDurationReached += async () => await StopDictationAsync().ConfigureAwait(true);
        _voice.SpeechStopped += () =>
        {
            MainAvatar.Talk(0);
            ReadReplyButton.Content = "Replay voice";
            TalkingStatus.Visibility = Visibility.Collapsed;
        };
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        _chat.Welcome();
        await _chat.RunSystemCheckAsync(_lifetime.Token).ConfigureAwait(true);
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        await _lifetime.CancelAsync().ConfigureAwait(true);
        await _voice.DisposeAsync().ConfigureAwait(true);
        if (_backend is not null)
            await _backend.DisposeAsync().ConfigureAwait(true);
        _lifetime.Dispose();
    }

    private async Task SendMessageAsync()
    {
        string message = MessageBox.Text.Trim();
        if (message.Length == 0) return;
        MessageBox.Clear();
        await _chat.SubmitAsync(message, _lifetime.Token).ConfigureAwait(true);
    }

    private async void OnSendClick(object sender, RoutedEventArgs e) => await SendMessageAsync().ConfigureAwait(true);

    private async void OnMessageKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || Keyboard.Modifiers.HasFlag(ModifierKeys.Shift)) return;
        e.Handled = true;
        await SendMessageAsync().ConfigureAwait(true);
    }

    private async void OnCheckAgainClick(object sender, RoutedEventArgs e)
    {
        await _chat.RunSystemCheckAsync(_lifetime.Token).ConfigureAwait(true);
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Microphone and model failures must be reported in the chat window without crashing it.")]
    private async void OnDictateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_voice.IsPreparing)
            {
                _voice.CancelDictationPreparation();
                VoiceStatus.Text = "Microphone setup cancelled.";
                return;
            }
            if (_voice.IsTranscribing) return;
            if (_voice.IsListening)
            {
                await StopDictationAsync().ConfigureAwait(true);
            }
            else
            {
                _voice.StopSpeaking();
                await _voice.StartDictationAsync(_lifetime.Token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            // The window is closing.
        }
        catch (OperationCanceledException)
        {
            VoiceStatus.Text = "Microphone setup cancelled.";
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            VoiceStatus.Text = error is InvalidOperationException ? error.Message :
                "The local microphone could not start. Check microphone access and try again.";
        }
        UpdateMicrophoneState();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Audio capture or inference failures must leave the composer usable.")]
    private async Task StopDictationAsync()
    {
        try { await _voice.StopDictationAsync(_lifetime.Token).ConfigureAwait(true); }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            VoiceStatus.Text = "I could not transcribe that recording. Please try again.";
        }
        UpdateMicrophoneState();
    }

    private void UpdateMicrophoneState()
    {
        DictateButton.IsEnabled = !_voice.IsTranscribing;
        DictateButton.Style = (Style)FindResource(_voice.IsListening
            ? "Button.One.Microphone.Active" : "Button.One.Microphone");
        DictateButton.ToolTip = _voice.IsPreparing ? "Cancel voice model setup" :
            _voice.IsListening ? "Stop and transcribe recording" : "Record a message on this device";
        AutomationProperties.SetName(DictateButton, _voice.IsListening
            ? "Stop microphone recording" : "Start microphone recording");
        if (!_voice.IsListening) MicRing.Opacity = 0;
    }

    private void OnVoiceToggleClick(object sender, RoutedEventArgs e)
    {
        _voiceEnabled = !_voiceEnabled;
        VoiceToggleButton.Content = _voiceEnabled ? "Sound on" : "Sound off";
        AutomationProperties.SetName(VoiceToggleButton, _voiceEnabled ? "Mute Tux voice" : "Enable Tux voice");
        if (!_voiceEnabled) _voice.StopSpeaking();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Speech synthesis may be unavailable; the chat must remain usable.")]
    private void SpeakAssistant(string text)
    {
        if (!_voiceEnabled || !AutoVoice) return;
        try
        {
            _voice.Speak(text);
            MainAvatar.Talk(60);
            TalkingStatus.Visibility = Visibility.Visible;
            ReadReplyButton.Content = "Stop voice";
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            MainAvatar.Talk(0);
            TalkingStatus.Visibility = Visibility.Collapsed;
            VoiceStatus.Text = "Tux's voice is unavailable on this device.";
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Optional speech must report device failures without crashing the chat window.")]
    private void OnReadReplyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_voice.IsSpeaking)
            {
                _voice.StopSpeaking();
                return;
            }
            if (_chat.LastAssistantText is not string text) return;
            _voiceEnabled = true;
            VoiceToggleButton.Content = "Sound on";
            SpeakAssistant(text);
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            MainAvatar.Talk(0);
            VoiceStatus.Text = "Voice playback is unavailable on this device.";
            ReadReplyButton.Content = "Replay voice";
        }
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnMascotDemoClick(object sender, RoutedEventArgs e) =>
        new MascotDemoWindow { Owner = this }.Show();

    private void OnWaveClick(object sender, RoutedEventArgs e) => MainAvatar.Trigger(MascotAction.Wave);
}
