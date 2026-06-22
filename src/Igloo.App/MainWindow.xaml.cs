using System.Windows;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Igloo.App.ViewModels;

namespace Igloo.App;

public sealed partial class MainWindow : Window
{
    private static readonly Brush CaptionHoverBrush = Frozen(Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
    private bool _maxButtonHovered;

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
        SourceInitialized += OnSourceInitialized;
    }

    // ── Custom chrome plumbing ────────────────────────────────────────────────

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        // Belt-and-suspenders: dark native chrome for the moments WPF's template isn't
        // painting yet (e.g. the first frame of startup), so nothing flashes white.
        ChromeInterop.EnableDarkTitleBar(hwnd);

        // Hook the window proc for the messages WindowChrome leaves to us.
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            // Keep a borderless maximized window inside the taskbar-excluded work area.
            case ChromeInterop.WM_GETMINMAXINFO:
                ChromeInterop.ConstrainMaximizedBounds(hwnd, lParam, MinWidth, MinHeight);
                handled = true;
                break;

            // Report the maximize button as the real HTMAXBUTTON so Win11 shows its snap-
            // layout flyout on hover. On Win10 this just behaves as a maximize click.
            case ChromeInterop.WM_NCHITTEST when IsOverMaxRestoreButton(lParam):
                handled = true;
                return (IntPtr)ChromeInterop.HTMAXBUTTON;

            // Drive the button's hover visual ourselves while it lives in the non-client
            // area (WPF's IsMouseOver trigger can't fire there).
            case ChromeInterop.WM_NCMOUSEMOVE:
                SetMaxButtonHover(wParam.ToInt32() == ChromeInterop.HTMAXBUTTON);
                break;

            case ChromeInterop.WM_NCMOUSELEAVE:
                SetMaxButtonHover(false);
                break;

            // Swallow the press, act on release — and toggle maximize/restore ourselves.
            case ChromeInterop.WM_NCLBUTTONDOWN when wParam.ToInt32() == ChromeInterop.HTMAXBUTTON:
                handled = true;
                break;

            case ChromeInterop.WM_NCLBUTTONUP when wParam.ToInt32() == ChromeInterop.HTMAXBUTTON:
                ToggleMaximizeRestore();
                SetMaxButtonHover(false);
                handled = true;
                break;

            // Right-click on the caption → native system menu at the cursor.
            case ChromeInterop.WM_NCRBUTTONUP when wParam.ToInt32() == ChromeInterop.HTCAPTION:
                var (x, y) = ChromeInterop.GetScreenPoint(lParam);
                ChromeInterop.ShowSystemMenu(this, x, y);
                handled = true;
                break;

            // Alt+Space → native system menu at the window's top-left.
            case ChromeInterop.WM_SYSKEYDOWN when wParam.ToInt32() == ChromeInterop.VK_SPACE:
                var origin = PointToScreen(new Point(0, 0));
                ChromeInterop.ShowSystemMenu(this, (int)origin.X, (int)origin.Y);
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    /// <summary>True when the screen point packed in an NC-message lParam is inside the maximize button.</summary>
    private bool IsOverMaxRestoreButton(IntPtr lParam)
    {
        if (MaxRestoreButton.ActualWidth == 0) return false;

        var (sx, sy) = ChromeInterop.GetScreenPoint(lParam);
        var topLeft = MaxRestoreButton.PointToScreen(new Point(0, 0));
        var bottomRight = MaxRestoreButton.PointToScreen(
            new Point(MaxRestoreButton.ActualWidth, MaxRestoreButton.ActualHeight));

        return sx >= topLeft.X && sx < bottomRight.X
            && sy >= topLeft.Y && sy < bottomRight.Y;
    }

    private void SetMaxButtonHover(bool hovered)
    {
        if (hovered == _maxButtonHovered) return;
        _maxButtonHovered = hovered;

        if (hovered)
        {
            MaxRestoreButton.Background = CaptionHoverBrush;
            MaxRestoreButton.Foreground = (Brush)FindResource("Brush.Text.Primary");
        }
        else
        {
            // Clear local values so the template's focus/pressed triggers resume control.
            MaxRestoreButton.ClearValue(BackgroundProperty);
            MaxRestoreButton.ClearValue(ForegroundProperty);
        }
    }

    private void ToggleMaximizeRestore() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    // ── Caption buttons ───────────────────────────────────────────────────────

    private void OnMinimizeClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void OnMaxRestoreClick(object sender, RoutedEventArgs e) => ToggleMaximizeRestore();

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    // ── Step transition ───────────────────────────────────────────────────────

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
