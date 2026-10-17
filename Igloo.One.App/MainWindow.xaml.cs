using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
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
    private bool _hasDesktopRecognizer;

    internal bool IsSystemCheckRunning => _chat.IsChecking;
    internal string SystemCheckStatus => _chat.Status;
    internal IReadOnlyList<OneChatMessage> ChatMessages => _chat.Messages;

    public MainWindow()
    {
        InitializeComponent();
        _chat = new(ct => (_backend ??= OneBackendClient.Create()).InspectSystemAsync(ct));
        _voice = new OneVoiceSession(Dispatcher);
        DataContext = _chat;
        _chat.CheckFinished += blockers =>
        {
            if (blockers) MainAvatar.Trigger(MascotAction.Think);
        };
        _chat.Messages.CollectionChanged += (_, _) =>
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () => TranscriptScroll.ScrollToEnd());
        _voice.StatusChanged += status => VoiceStatus.Text = status;
        _voice.Transcribed += text =>
        {
            MessageBox.Text = string.IsNullOrWhiteSpace(MessageBox.Text)
                ? text : MessageBox.Text.TrimEnd() + " " + text;
            MessageBox.Focus();
            MessageBox.CaretIndex = MessageBox.Text.Length;
        };
        _voice.WordSpoken += () => MainAvatar.WordBoundary();
        _voice.ListeningStopped += () => DictateButton.Content = "Dictate";
        _voice.SpeechStopped += () =>
        {
            MainAvatar.Talk(0);
            ReadReplyButton.Content = "Read reply";
        };
        Loaded += OnLoaded;
        Closed += OnClosed;
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Optional speech availability must not prevent the read-only system check from starting.")]
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_started) return;
        _started = true;
        try
        {
            _hasDesktopRecognizer = OneVoiceSession.HasDesktopRecognizer;
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
        }
        if (!_hasDesktopRecognizer)
        {
            DictateButton.Content = "Voice typing";
            VoiceStatus.Text = "Use Windows voice typing for input; it uses online speech recognition.";
        }
        _chat.Welcome();
        await _chat.RunSystemCheckAsync(_lifetime.Token).ConfigureAwait(true);
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        await _lifetime.CancelAsync().ConfigureAwait(true);
        _voice.Dispose();
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
        Justification = "Optional speech must report device failures without crashing the chat window.")]
    private void OnDictateClick(object sender, RoutedEventArgs e)
    {
        if (!_hasDesktopRecognizer)
        {
            MessageBox.Focus();
            VoiceStatus.Text = "Press Win+H to dictate into this box with Windows voice typing (online). Review before sending.";
            return;
        }
        try
        {
            if (_voice.IsListening)
            {
                _voice.StopDictation();
                DictateButton.Content = "Dictate";
            }
            else
            {
                _voice.StartDictation();
                DictateButton.Content = "Stop";
            }
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            VoiceStatus.Text = "Dictation is unavailable. Check the microphone and Windows speech language.";
            DictateButton.Content = "Dictate";
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
            _voice.Speak(text);
            MainAvatar.Talk(60);
            ReadReplyButton.Content = "Stop reading";
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            MainAvatar.Talk(0);
            VoiceStatus.Text = "Voice playback is unavailable on this device.";
            ReadReplyButton.Content = "Read reply";
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
