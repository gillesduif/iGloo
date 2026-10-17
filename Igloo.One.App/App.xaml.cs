using System.Configuration;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

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
                File.WriteAllText(output, $"recognizers={support.Recognizers}, voices={support.Voices}{Environment.NewLine}");
                Shutdown(0);
            }
            catch (Exception error)
            {
                File.WriteAllText(output, $"unavailable={error.GetType().Name}{Environment.NewLine}");
                Shutdown(1);
            }
            return;
        }
        if (e.Args.Length >= 2 && (e.Args[0] is "--main-capture" or "--main-capture-complete" or "--demo-window-capture"))
        {
            string output = Path.GetFullPath(e.Args[1]);
            Window main = e.Args[0] == "--demo-window-capture" ? new MascotDemoWindow() : new MainWindow();
            if (e.Args.Length >= 3 && double.TryParse(e.Args[2], out double width))
                main.Width = Math.Max(main.MinWidth, width);
            MainWindow = main;
            main.Loaded += async (_, _) =>
            {
                await Task.Delay(1000).ConfigureAwait(true);
                if (e.Args[0] == "--main-capture-complete" && main is MainWindow one)
                    for (int i = 0; one.IsSystemCheckRunning && i < 150; i++)
                        await Task.Delay(200).ConfigureAwait(true);
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
}
