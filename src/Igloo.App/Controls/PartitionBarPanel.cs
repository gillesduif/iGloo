using System.Windows;
using System.Windows.Controls;

namespace Igloo.App.Controls;

/// <summary>
/// Lays out its children as one horizontal disk bar: each child's width is
/// proportional to its attached <see cref="WeightProperty"/> (partition size in
/// bytes), separated by <see cref="Gap"/>, with a readability floor so a tiny
/// partition (a 99 MB ESP on a 1 TB disk) never collapses below
/// <see cref="MinSegmentWidth"/> — the same compromise Disk Management makes.
/// </summary>
public sealed class PartitionBarPanel : Panel
{
    public static readonly DependencyProperty WeightProperty = DependencyProperty.RegisterAttached(
        "Weight", typeof(double), typeof(PartitionBarPanel),
        new FrameworkPropertyMetadata(0.0,
            FrameworkPropertyMetadataOptions.AffectsParentMeasure | FrameworkPropertyMetadataOptions.AffectsParentArrange));

    public static double GetWeight(DependencyObject obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return (double)obj.GetValue(WeightProperty);
    }

    public static void SetWeight(DependencyObject obj, double value)
    {
        ArgumentNullException.ThrowIfNull(obj);
        obj.SetValue(WeightProperty, value);
    }

    public static readonly DependencyProperty GapProperty = DependencyProperty.Register(
        nameof(Gap), typeof(double), typeof(PartitionBarPanel),
        new FrameworkPropertyMetadata(3.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>Pixels between segments.</summary>
    public double Gap
    {
        get => (double)GetValue(GapProperty);
        set => SetValue(GapProperty, value);
    }

    public static readonly DependencyProperty MinSegmentWidthProperty = DependencyProperty.Register(
        nameof(MinSegmentWidth), typeof(double), typeof(PartitionBarPanel),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsArrange));

    /// <summary>Readability floor for a segment's width.</summary>
    public double MinSegmentWidth
    {
        get => (double)GetValue(MinSegmentWidthProperty);
        set => SetValue(MinSegmentWidthProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double maxChildHeight = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Measure(availableSize);
            maxChildHeight = Math.Max(maxChildHeight, child.DesiredSize.Height);
        }

        return new Size(
            double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? maxChildHeight : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var visible = new List<UIElement>();
        foreach (UIElement child in InternalChildren)
            if (child.Visibility != Visibility.Collapsed)
                visible.Add(child);
        if (visible.Count == 0)
            return finalSize;

        var widths = ComputeWidths(visible, finalSize.Width);
        double x = 0;
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].Arrange(new Rect(x, 0, widths[i], finalSize.Height));
            x += widths[i] + Gap;
        }
        return finalSize;
    }

    private double[] ComputeWidths(List<UIElement> children, double totalWidth)
    {
        var n = children.Count;
        var available = Math.Max(0, totalWidth - Gap * (n - 1));
        var min = Math.Min(MinSegmentWidth, available / n);   // degrade gracefully when crowded

        var weights = new double[n];
        for (var i = 0; i < n; i++)
            weights[i] = Math.Max(GetWeight(children[i]), 0.0);

        var widths = new double[n];
        var pinned = new bool[n];

        // Iteratively pin below-floor segments at the floor and redistribute the
        // remaining width among the rest by weight. Terminates: every pass pins
        // at least one more segment or changes nothing.
        while (true)
        {
            double freeWidth = available, weightSum = 0;
            var unpinned = 0;
            for (var i = 0; i < n; i++)
            {
                if (pinned[i])
                    freeWidth -= min;
                else
                { weightSum += weights[i]; unpinned++; }
            }

            var pinnedThisPass = false;
            for (var i = 0; i < n; i++)
            {
                if (pinned[i])
                { widths[i] = min; continue; }

                widths[i] = weightSum > 0
                    ? freeWidth * weights[i] / weightSum
                    : freeWidth / Math.Max(unpinned, 1);

                if (widths[i] < min)
                {
                    pinned[i] = true;
                    pinnedThisPass = true;
                }
            }

            if (!pinnedThisPass)
                return widths;
        }
    }
}
