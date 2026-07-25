using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

namespace Igloo.App.Behaviors;

/// <summary>
/// Attached behavior that turns a <see cref="ScrollViewer"/>'s chunky line-by-line mouse
/// wheel into a smooth, eased glide. Wheel ticks accumulate into a target offset that is
/// animated via an attached proxy property (ScrollViewer.VerticalOffset is read-only, so
/// we animate the proxy and forward it through <see cref="ScrollViewer.ScrollToVerticalOffset"/>).
///
/// Honors the system reduced-motion preference: when animations are off, the default
/// (instant) scrolling is left untouched.
/// </summary>
public static class SmoothScroll
{
    private const double WheelStep = 1.4;     // multiplier on the raw wheel delta (~120/tick)
    private const int DurationMs = 260;

    public static readonly DependencyProperty IsEnabledProperty =
        DependencyProperty.RegisterAttached(
            "IsEnabled", typeof(bool), typeof(SmoothScroll),
            new PropertyMetadata(false, OnIsEnabledChanged));

    public static bool GetIsEnabled(DependencyObject o)
    {
        ArgumentNullException.ThrowIfNull(o);
        return (bool)o.GetValue(IsEnabledProperty);
    }

    public static void SetIsEnabled(DependencyObject o, bool value)
    {
        ArgumentNullException.ThrowIfNull(o);
        o.SetValue(IsEnabledProperty, value);
    }

    // Animated proxy: writing it scrolls the viewer.
    private static readonly DependencyProperty OffsetProperty =
        DependencyProperty.RegisterAttached(
            "Offset", typeof(double), typeof(SmoothScroll),
            new PropertyMetadata(0.0, OnOffsetChanged));

    // Where the in-flight animation is heading (so rapid ticks add up instead of resetting).
    private static readonly DependencyProperty TargetOffsetProperty =
        DependencyProperty.RegisterAttached(
            "TargetOffset", typeof(double), typeof(SmoothScroll), new PropertyMetadata(double.NaN));

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv)
            return;

        if ((bool)e.NewValue)
            sv.PreviewMouseWheel += OnPreviewMouseWheel;
        else
            sv.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnOffsetChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is ScrollViewer sv)
            sv.ScrollToVerticalOffset((double)e.NewValue);
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        var sv = (ScrollViewer)sender;

        // Nothing to scroll, or a nested inner scroller should handle it — let it through.
        if (sv.ScrollableHeight <= 0)
            return;

        // Reduced motion: don't hijack; default instant scrolling stays.
        if (!SystemParameters.ClientAreaAnimation)
            return;

        var pending = (double)sv.GetValue(TargetOffsetProperty);
        var from = double.IsNaN(pending) ? sv.VerticalOffset : pending;

        var target = Math.Clamp(from - e.Delta * WheelStep, 0, sv.ScrollableHeight);
        sv.SetValue(TargetOffsetProperty, target);

        // Seed the proxy so the animation starts from where we actually are.
        sv.SetValue(OffsetProperty, sv.VerticalOffset);

        var anim = new DoubleAnimation
        {
            From = sv.VerticalOffset,
            To = target,
            Duration = TimeSpan.FromMilliseconds(DurationMs),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop,
        };
        anim.Completed += (_, _) =>
        {
            sv.SetValue(OffsetProperty, target);
            sv.SetValue(TargetOffsetProperty, double.NaN);
        };

        sv.BeginAnimation(OffsetProperty, anim);
        e.Handled = true;
    }
}
