using System.Configuration;
using System.Data;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;
using System.IO;
using System.Windows;
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
        if (e.Args.Length >= 2 && e.Args[0] == "--mascot-capture")
        {
            string directory = Path.GetFullPath(e.Args[1]);
            bool noBlur = e.Args.Length >= 3 && e.Args[2] == "--no-blur";
            var demo = new MascotDemoWindow();
            MainWindow = demo;
            demo.Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
            {
                try
                {
                    if (noBlur) demo.DisableBlurEffectsForDiagnostics();
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
            bool noBlur = e.Args.Length >= 4 && e.Args[3] == "--no-blur";
            var demo = new MascotDemoWindow();
            MainWindow = demo;
            demo.Loaded += (_, _) => Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () =>
                BeginBenchmark(demo, directory, seconds, noBlur));
            demo.Show();
        }
        else
        {
            var main = new MainWindow();
            MainWindow = main;
            main.Show();
        }
    }

    private void BeginBenchmark(MascotDemoWindow demo, string directory, double seconds, bool noBlur)
    {
        if (noBlur) demo.DisableBlurEffectsForDiagnostics();
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
