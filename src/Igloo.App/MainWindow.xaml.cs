using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Animation;
using Igloo.App.ViewModels;

namespace Igloo.App;

public sealed partial class MainWindow : Window
{
    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    /// <summary>
    /// Light fade/slide when the wizard step changes (200 ms, ease-out). Purely
    /// decorative: it never blocks input, and it is skipped entirely when the
    /// user has animations disabled in Windows accessibility settings.
    /// </summary>
    private void OnStepChanged(object sender, DataTransferEventArgs e)
    {
        if (!SystemParameters.ClientAreaAnimation) return;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var duration = new Duration(TimeSpan.FromMilliseconds(200));

        StepHost.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, duration));
        StepTransform.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, duration) { EasingFunction = ease });
    }
}
