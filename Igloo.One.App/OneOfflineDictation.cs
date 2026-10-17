using System.IO;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Windows.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Vosk;

namespace Igloo.One.App;

/// <summary>Opt-in microphone capture and local transcription. Audio is held in memory and discarded after each turn.</summary>
internal sealed class OneOfflineDictation(Dispatcher dispatcher) : IAsyncDisposable
{
    private const int SampleRate = 16000;
    private readonly CancellationTokenSource _disposal = new();
    private readonly object _audioLock = new();
    private CancellationTokenSource? _preparation;
    private Task? _starting;
    private Task<string?>? _stopping;
    private WasapiCapture? _microphone;
    private WaveFormat? _captureFormat;
    private int _maximumAudioBytes;
    private MemoryStream? _audio;
    private TaskCompletionSource? _recordingStopped;
    private Model? _model;
    private bool _limitNotified;

    public event Action<string>? StatusChanged;
    public event Action? StateChanged;
    public event Action<double>? MicrophoneLevelChanged;
    public event Action? MaximumDurationReached;
    public bool IsPreparing => _preparation is not null;
    public bool IsListening => _microphone is not null && _stopping is null;
    public bool IsTranscribing => _stopping is not null;
    public static int MicrophoneCount
    {
        get
        {
            using var devices = new MMDeviceEnumerator();
            return devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active).Count;
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_starting is not null || _microphone is not null || _stopping is not null) return;
        _preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _disposal.Token);
        StateChanged?.Invoke();
        _starting = StartCoreAsync(_preparation.Token);
        try { await _starting.ConfigureAwait(true); }
        finally
        {
            _starting = null;
            _preparation.Dispose();
            _preparation = null;
            StateChanged?.Invoke();
        }
    }

    public void CancelPreparation() => _preparation?.Cancel();

    private async Task StartCoreAsync(CancellationToken cancellationToken)
    {
        if (MicrophoneCount == 0) throw new InvalidOperationException("No microphone is available.");
        string path = await OneVoiceModelStore.EnsureAsync(Report, cancellationToken).ConfigureAwait(true);
        if (_model is null)
        {
            Report("Loading the offline voice model…");
            Vosk.Vosk.SetLogLevel(-1);
            Model loaded = await Task.Run(() => new Model(path), CancellationToken.None).ConfigureAwait(true);
            if (cancellationToken.IsCancellationRequested)
            {
                loaded.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            _model = loaded;
        }
        cancellationToken.ThrowIfCancellationRequested();
        _audio = new MemoryStream();
        _recordingStopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _limitNotified = false;
        WasapiCapture? microphone = null;
        try
        {
            microphone = new WasapiCapture();
            _captureFormat = microphone.WaveFormat;
            _maximumAudioBytes = checked(_captureFormat.AverageBytesPerSecond * 30);
            microphone.DataAvailable += OnDataAvailable;
            microphone.RecordingStopped += OnRecordingStopped;
            microphone.StartRecording();
            _microphone = microphone;
            Report("Listening locally… Select the microphone again to finish.");
            StateChanged?.Invoke();
        }
        catch
        {
            if (microphone is not null)
            {
                microphone.DataAvailable -= OnDataAvailable;
                microphone.RecordingStopped -= OnRecordingStopped;
                microphone.Dispose();
            }
            await _audio.DisposeAsync().ConfigureAwait(true);
            _audio = null;
            _captureFormat = null;
            _recordingStopped = null;
            throw;
        }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        int length;
        lock (_audioLock)
        {
            if (_audio is null) return;
            length = Math.Min(args.BytesRecorded, _maximumAudioBytes - (int)_audio.Length);
            if (length > 0) _audio.Write(args.Buffer, 0, length);
            if (_audio.Length >= _maximumAudioBytes && !_limitNotified)
            {
                _limitNotified = true;
                dispatcher.BeginInvoke(() => MaximumDurationReached?.Invoke());
            }
        }
        if (length == 0) return;
        WaveFormat? format = _captureFormat;
        if (format is null) return;
        double sum = 0;
        int count = 0;
        int stride = Math.Max(2, format.BlockAlign * 8);
        for (int offset = 0; offset + 3 < length; offset += stride)
        {
            double sample = format.BitsPerSample == 32
                ? BitConverter.ToSingle(args.Buffer, offset)
                : BitConverter.ToInt16(args.Buffer, offset) / 32768d;
            sum += sample * sample;
            count++;
        }
        double level = Math.Clamp(Math.Sqrt(sum / Math.Max(1, count)) * 3, 0, 1);
        dispatcher.BeginInvoke(() => MicrophoneLevelChanged?.Invoke(level));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is Exception error) _recordingStopped?.TrySetException(error);
        else _recordingStopped?.TrySetResult();
    }

    public Task<string?> StopAsync(CancellationToken cancellationToken)
    {
        if (_stopping is not null) return _stopping;
        if (_microphone is null) return Task.FromResult<string?>(null);
        _stopping = StopCoreAsync(cancellationToken);
        StateChanged?.Invoke();
        return _stopping;
    }

    private async Task<string?> StopCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            WasapiCapture microphone = _microphone!;
            microphone.StopRecording();
            await _recordingStopped!.Task.WaitAsync(TimeSpan.FromSeconds(3), cancellationToken)
                .ConfigureAwait(true);
            byte[] samples;
            lock (_audioLock) samples = _audio!.ToArray();
            WaveFormat captureFormat = _captureFormat!;
            ReleaseMicrophone();
            if (samples.Length < captureFormat.AverageBytesPerSecond / 4)
            {
                Report("I did not hear enough speech. Try again.");
                return null;
            }
            Report("Transcribing on this device…");
            using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _disposal.Token);
            string text = await Task.Run(() => Transcribe(_model!,
                ConvertToMonoPcm(samples, captureFormat), linked.Token), linked.Token)
                .ConfigureAwait(true);
            Report(text.Length == 0 ? "No clear words detected. Try again." :
                "Transcript added to the message box. Review it before sending.");
            return text.Length == 0 ? null : text;
        }
        finally
        {
            ReleaseMicrophone();
            _stopping = null;
            MicrophoneLevelChanged?.Invoke(0);
            StateChanged?.Invoke();
        }
    }

    private static string Transcribe(Model model, byte[] samples, CancellationToken cancellationToken)
    {
        using var recognizer = new VoskRecognizer(model, SampleRate);
        var text = new StringBuilder();
        byte[] chunk = new byte[4096];
        for (int offset = 0; offset < samples.Length; offset += chunk.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int count = Math.Min(chunk.Length, samples.Length - offset);
            Buffer.BlockCopy(samples, offset, chunk, 0, count);
            if (recognizer.AcceptWaveform(chunk, count)) AppendResult(text, recognizer.Result());
        }
        AppendResult(text, recognizer.FinalResult());
        return text.ToString().Trim();
    }

    internal static byte[] ConvertToMonoPcm(byte[] captured, WaveFormat inputFormat)
    {
        using var source = new RawSourceWaveStream(new MemoryStream(captured, writable: false), inputFormat);
        using var resampler = new MediaFoundationResampler(source, new WaveFormat(SampleRate, 16, 1))
        {
            ResamplerQuality = 60
        };
        using var output = new MemoryStream();
        byte[] buffer = new byte[8192];
        int read;
        while ((read = resampler.Read(buffer, 0, buffer.Length)) > 0)
            output.Write(buffer, 0, read);
        return output.ToArray();
    }

    internal static async Task<string> TranscribeSampleAsync(byte[] samples, CancellationToken cancellationToken)
    {
        string path = await OneVoiceModelStore.EnsureAsync(_ => { }, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            Vosk.Vosk.SetLogLevel(-1);
            using var model = new Model(path);
            return Transcribe(model, samples, cancellationToken);
        }, cancellationToken).ConfigureAwait(false);
    }

    private static void AppendResult(StringBuilder output, string json)
    {
        using JsonDocument result = JsonDocument.Parse(json);
        if (!result.RootElement.TryGetProperty("text", out JsonElement value)) return;
        string? words = value.GetString();
        if (string.IsNullOrWhiteSpace(words)) return;
        if (output.Length > 0) output.Append(' ');
        output.Append(words.Trim());
    }

    private void ReleaseMicrophone()
    {
        if (_microphone is WasapiCapture microphone)
        {
            microphone.DataAvailable -= OnDataAvailable;
            microphone.RecordingStopped -= OnRecordingStopped;
            microphone.Dispose();
            _microphone = null;
        }
        _captureFormat = null;
        lock (_audioLock)
        {
            _audio?.Dispose();
            _audio = null;
        }
        _recordingStopped = null;
    }

    private void Report(string status) => dispatcher.BeginInvoke(() => StatusChanged?.Invoke(status));

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "Window shutdown must release microphone and model resources even if preparation failed.")]
    public async ValueTask DisposeAsync()
    {
        await _disposal.CancelAsync().ConfigureAwait(true);
        if (_preparation is not null) await _preparation.CancelAsync().ConfigureAwait(true);
        if (_starting is not null)
        {
            try { await _starting.ConfigureAwait(true); }
            catch (Exception) { }
        }
        _preparation?.Dispose();
        if (_stopping is not null)
        {
            try { await _stopping.ConfigureAwait(true); }
            catch (Exception) { }
        }
        else if (_microphone is not null)
        {
            try
            {
                _microphone.StopRecording();
                if (_recordingStopped is not null)
                    await _recordingStopped.Task.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(true);
            }
            catch (Exception) { }
            finally { ReleaseMicrophone(); }
        }
        _model?.Dispose();
        _disposal.Dispose();
    }
}
