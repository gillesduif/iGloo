using System.Configuration;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Speech.AudioFormat;
using System.Speech.Synthesis;
using NAudio.Wave;

namespace Igloo.One.App;
/// <summary>
/// Interaction logic for App.xaml
/// </summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Maintainability", "CA1515:Consider making public types internal",
    Justification = "WPF XAML-generated partial class is public.")]
public partial class OneApplication : Application
{
    [SuppressMessage("Design", "CA1031:Do not catch general exception types", Justification = "Automated visual capture records any render failure and exits nonzero.")]
    protected override void OnStartup(StartupEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnStartup(e);
        if (e.Args.Length >= 2 && e.Args[0] == "--voice-probe")
        {
            string output = Path.GetFullPath(e.Args[1]);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            try
            {
                var support = OneVoiceSession.InstalledSupport();
                File.WriteAllText(output, $"microphones={support.Microphones}, voices={support.Voices}{Environment.NewLine}");
                Shutdown(0);
            }
            catch (Exception error)
            {
                File.WriteAllText(output, $"unavailable={error.GetType().Name}{Environment.NewLine}");
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Length >= 2 && e.Args[0] == "--voice-selftest")
        {
            string output = Path.GetFullPath(e.Args[1]);
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => BeginVoiceSelfTest(output));
            return;
        }
        if (e.Args.Length >= 2 && (e.Args[0] is "--main-capture" or "--main-capture-complete" or
            "--main-speaking-capture" or "--main-live-voice-capture" or "--demo-window-capture"))
        {
            string output = Path.GetFullPath(e.Args[1]);
            Window main = e.Args[0] == "--demo-window-capture" ? new MascotDemoWindow() : new MainWindow();
            if (main is MainWindow capture && e.Args[0] != "--main-live-voice-capture")
                capture.AutoVoice = false;
            if (e.Args.Length >= 3 && double.TryParse(e.Args[2], out double width))
                main.Width = Math.Max(main.MinWidth, width);
            MainWindow = main;
            main.Loaded += async (_, _) =>
            {
                int delay = e.Args.Length >= 4 && int.TryParse(e.Args[3], out int requestedDelay)
                    ? Math.Clamp(requestedDelay, 100, 30000) : 1000;
                await Task.Delay(delay).ConfigureAwait(true);
                if (e.Args[0] == "--main-capture-complete" && main is MainWindow one)
                    for (int i = 0; one.IsSystemCheckRunning && i < 150; i++)
                        await Task.Delay(200).ConfigureAwait(true);
                if (e.Args[0] == "--main-speaking-capture" && main is MainWindow speaking)
                {
                    speaking.ShowSpeechCapturePose();
                    await Task.Delay(120).ConfigureAwait(true);
                }
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(output)!);
                    DpiScale dpi = VisualTreeHelper.GetDpi(main);
                    var bitmap = new RenderTargetBitmap(
                        (int)Math.Ceiling(main.ActualWidth * dpi.DpiScaleX),
                        (int)Math.Ceiling(main.ActualHeight * dpi.DpiScaleY),
                        dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
                    bitmap.Render(main);
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using (FileStream stream = File.Create(output)) encoder.Save(stream);
                    Shutdown(0);
                }
                catch (Exception error)
                {
                    File.WriteAllText(Path.ChangeExtension(output, ".error.txt"), error.ToString());
                    Shutdown(1);
                }
            };
            main.Show();
        }
        else if (e.Args.Length >= 2 && e.Args[0] == "--mascot-capture")
        {
            string directory = Path.GetFullPath(e.Args[1]);
            var demo = new MascotDemoWindow();
            MainWindow = demo;
            demo.Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                try
                {
                    demo.CaptureAll(directory);
                    Shutdown(0);
                }
                catch (Exception error)
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "capture-error.txt"), error.ToString());
                    Shutdown(1);
                }
            });
            demo.Show();
        }
        else if (e.Args.Length >= 2 && e.Args[0] == "--mascot-benchmark")
        {
            string directory = Path.GetFullPath(e.Args[1]);
            double seconds = e.Args.Length >= 3 && double.TryParse(e.Args[2], out double requested) ? requested : 600;
            var demo = new MascotDemoWindow();
            MainWindow = demo;
            demo.Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                BeginBenchmark(demo, directory, seconds));
            demo.Show();
        }
        else if (e.Args.Length >= 2 && e.Args[0] == "--mascot-lifecycle")
        {
            string directory = Path.GetFullPath(e.Args[1]);
            var demo = new MascotDemoWindow();
            MainWindow = demo;
            demo.Loaded += async (_, _) =>
            {
                await Task.Delay(700).ConfigureAwait(true);
                long visibleBefore = demo.RenderedFrames;
                demo.WindowState = WindowState.Minimized;
                await Task.Delay(700).ConfigureAwait(true);
                long minimized = demo.RenderedFrames;
                demo.WindowState = WindowState.Normal;
                await Task.Delay(700).ConfigureAwait(true);
                long restored = demo.RenderedFrames;
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "lifecycle.txt"),
                    $"visible={visibleBefore}, minimized={minimized}, restored={restored}{Environment.NewLine}");
                Shutdown(visibleBefore > 0 && minimized == visibleBefore && restored > minimized ? 0 : 1);
            };
            demo.Show();
        }
        else if (e.Args.Length >= 2 && e.Args[0] == "--mascot-flow")
        {
            string directory = Path.GetFullPath(e.Args[1]);
            var demo = new MascotDemoWindow();
            MainWindow = demo;
            demo.Loaded += async (_, _) =>
            {
                try
                {
                    await demo.RunDemoAsync().ConfigureAwait(true);
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "flow.txt"),
                        $"completed={demo.CompletedTasks}/4, frames={demo.RenderedFrames}{Environment.NewLine}");
                    Shutdown(demo.CompletedTasks == 4 ? 0 : 1);
                }
                catch (Exception error)
                {
                    Directory.CreateDirectory(directory);
                    File.WriteAllText(Path.Combine(directory, "flow-error.txt"), error.ToString());
                    Shutdown(1);
                }
            };
            demo.Show();
        }
        else
        {
            var main = new MainWindow();
            MainWindow = main;
            main.Show();
        }
    }

    private void BeginBenchmark(MascotDemoWindow demo, string directory, double seconds)
    {
        Directory.CreateDirectory(directory);
        string log = Path.Combine(directory, "performance.csv");
        File.WriteAllText(log, "elapsed_s,frames,private_bytes,managed_bytes,cpu_s,working_set_bytes,render_tier,glare_mode" + Environment.NewLine);
        var watch = Stopwatch.StartNew();
        Process process = Process.GetCurrentProcess();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        timer.Tick += (_, _) =>
        {
            process.Refresh();
            File.AppendAllText(log, $"{watch.Elapsed.TotalSeconds:F1},{demo.RenderedFrames},{process.PrivateMemorySize64}," +
                $"{GC.GetTotalMemory(false)},{process.TotalProcessorTime.TotalSeconds:F2},{process.WorkingSet64}," +
                $"{System.Windows.Media.RenderCapability.Tier >> 16},geometry_halo{Environment.NewLine}");
            if (watch.Elapsed.TotalSeconds >= seconds) { timer.Stop(); process.Dispose(); Shutdown(0); }
        };
        timer.Start();
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The diagnostic writes a failure report and exits nonzero rather than crashing the process.")]
    private async void BeginVoiceSelfTest(string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        try
        {
            string wavePath = Path.ChangeExtension(output, ".wav");
            using (var synthesizer = new SpeechSynthesizer())
            {
                synthesizer.SetOutputToWaveFile(wavePath,
                    new SpeechAudioFormatInfo(16000, AudioBitsPerSample.Sixteen, AudioChannel.Mono));
                synthesizer.Speak("Welcome to iGloo One. System check complete.");
                synthesizer.SetOutputToNull();
            }
            using var reader = new WaveFileReader(wavePath);
            if (reader.WaveFormat.SampleRate != 16000 || reader.WaveFormat.BitsPerSample != 16 ||
                reader.WaveFormat.Channels != 1)
                throw new InvalidDataException("The speech fixture was not 16 kHz mono PCM.");
            byte[] samples = new byte[checked((int)reader.Length)];
            int count = reader.Read(samples, 0, samples.Length);
            byte[] stereo48 = new byte[checked(count / 2 * 3 * 8)];
            int destination = 0;
            for (int offset = 0; offset + 1 < count; offset += 2)
            {
                float sample = BitConverter.ToInt16(samples, offset) / 32768f;
                for (int repeat = 0; repeat < 3; repeat++)
                    for (int channel = 0; channel < 2; channel++)
                {
                    BitConverter.TryWriteBytes(stereo48.AsSpan(destination, 4), sample);
                    destination += 4;
                }
            }
            byte[] converted = await Task.Run(() => OneOfflineDictation.ConvertToMonoPcm(stereo48,
                WaveFormat.CreateIeeeFloatWaveFormat(48000, 2))).ConfigureAwait(true);
            string transcript = await OneOfflineDictation.TranscribeSampleAsync(
                converted, CancellationToken.None).ConfigureAwait(true);
            File.WriteAllText(output, $"capture_format=48000 Hz stereo float; pcm_bytes={converted.Length}{Environment.NewLine}" +
                $"transcript={transcript}{Environment.NewLine}");
            Shutdown(string.IsNullOrWhiteSpace(transcript) ? 1 : 0);
        }
        catch (Exception error)
        {
            File.WriteAllText(output, $"error={error}{Environment.NewLine}");
            Shutdown(1);
        }
    }
}
