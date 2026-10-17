using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Igloo.Mascot.Core;

namespace Igloo.Mascot.Wpf;

/// <summary>Cards orbit behind/in front of a TuxAvatar and attach to its measured hand.</summary>
public sealed class TaskCardRing : Control
{
    private readonly Dictionary<TaskCardViewModel, CardVisual> _visuals = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Canvas? _back, _front;
    private TaskCardViewModel? _held;
    private double _lastTime, _orbit;
    private bool _waiting;

    static TaskCardRing()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(TaskCardRing), new FrameworkPropertyMetadata(typeof(TaskCardRing)));
    }

    public TaskCardRing()
    {
        Cards = new ObservableCollection<TaskCardViewModel>();
        Loaded += (_, _) => RefreshVisuals();
    }

    public static readonly DependencyProperty CardsProperty = DependencyProperty.Register(nameof(Cards),
        typeof(ObservableCollection<TaskCardViewModel>), typeof(TaskCardRing),
        new PropertyMetadata(null, OnCardsChanged));
    public static readonly DependencyProperty AvatarProperty = DependencyProperty.Register(nameof(Avatar),
        typeof(TuxAvatar), typeof(TaskCardRing), new PropertyMetadata(null, OnAvatarChanged));
    public static readonly DependencyProperty AutoAdvanceProgressProperty = DependencyProperty.Register(nameof(AutoAdvanceProgress),
        typeof(bool), typeof(TaskCardRing), new PropertyMetadata(true));
    public static readonly DependencyProperty DarkThemeProperty = DependencyProperty.Register(nameof(DarkTheme),
        typeof(bool), typeof(TaskCardRing), new PropertyMetadata(true));

    [SuppressMessage("Usage", "CA2227:Collection properties should be read only", Justification = "The dependency property must accept an MVVM collection binding.")]
    public ObservableCollection<TaskCardViewModel> Cards
    {
        get => (ObservableCollection<TaskCardViewModel>)GetValue(CardsProperty);
        set => SetValue(CardsProperty, value);
    }
    public TuxAvatar? Avatar { get => (TuxAvatar?)GetValue(AvatarProperty); set => SetValue(AvatarProperty, value); }
    public bool AutoAdvanceProgress { get => (bool)GetValue(AutoAdvanceProgressProperty); set => SetValue(AutoAdvanceProgressProperty, value); }
    public bool DarkTheme { get => (bool)GetValue(DarkThemeProperty); set => SetValue(DarkThemeProperty, value); }
    public bool WaitingForUser => _waiting;
    public Point? HeldCardCentreInRing { get; private set; }
    public event EventHandler? WaitingChanged;

    private static void OnCardsChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var ring = (TaskCardRing)sender;
        if (args.OldValue is ObservableCollection<TaskCardViewModel> old) old.CollectionChanged -= ring.OnCollectionChanged;
        if (args.NewValue is ObservableCollection<TaskCardViewModel> current) current.CollectionChanged += ring.OnCollectionChanged;
        ring.RefreshVisuals();
    }

    private static void OnAvatarChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var ring = (TaskCardRing)sender;
        if (args.OldValue is TuxAvatar old) old.FrameRendered -= ring.OnAvatarFrame;
        if (args.NewValue is TuxAvatar current) current.FrameRendered += ring.OnAvatarFrame;
    }

    private void OnCollectionChanged(object? sender, NotifyCollectionChangedEventArgs args) => RefreshVisuals();

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _back = GetTemplateChild("PART_Back") as Canvas;
        _front = GetTemplateChild("PART_Front") as Canvas;
        RefreshVisuals();
    }

    private void RefreshVisuals()
    {
        if (_back is null || _front is null || Cards is null) return;
        var present = new HashSet<TaskCardViewModel>(Cards);
        foreach ((TaskCardViewModel card, CardVisual visual) in _visuals.ToArray())
        {
            if (present.Contains(card)) continue;
            (visual.Border.Parent as Panel)?.Children.Remove(visual.Border);
            _visuals.Remove(card);
        }
        foreach (TaskCardViewModel card in Cards)
        {
            if (_visuals.ContainsKey(card)) continue;
            var visual = new CardVisual(card);
            _visuals.Add(card, visual);
            _back.Children.Add(visual.Border);
        }
    }

    public bool StartTask(int index, double durationSeconds = 6)
    {
        if (_held is not null || index < 0 || index >= Cards.Count || Cards[index].State != TaskCardState.Todo)
            return false;
        TaskCardViewModel card = Cards[index];
        card.DurationSeconds = durationSeconds;
        card.Progress = 0;
        card.State = TaskCardState.Busy;
        _held = card;
        if (Avatar is not null) Avatar.IsHoldingCard = true;
        return true;
    }

    public void CompleteTask(bool failed = false)
    {
        if (_held is null) return;
        _held.Progress = failed ? _held.Progress : 1;
        _held.State = failed ? TaskCardState.Failed : TaskCardState.Done;
        _held = null;
        if (Avatar is not null) Avatar.IsHoldingCard = false;
    }

    public void SetWaiting(bool value)
    {
        if (_waiting == value) return;
        _waiting = value;
        if (Avatar is not null) Avatar.SetWaiting(value);
        WaitingChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetGazeTarget(Point screenPoint) => Avatar?.SetGazeTarget(screenPoint);

    private void OnAvatarFrame(object? sender, EventArgs args) => LayoutCards(false);

    public void ArrangeForSnapshot() => LayoutCards(true);

    private void LayoutCards(bool snapshot)
    {
        if (Avatar is null || _back is null || _front is null || Cards.Count == 0 || ActualWidth <= 0) return;
        double now = _clock.Elapsed.TotalSeconds;
        double dt = snapshot ? 0 : Math.Clamp(now - _lastTime, 0, .05);
        _lastTime = now;
        _orbit += dt * .32 * (SystemParameters.ClientAreaAnimation ? 1 : .4);
        if (_held is null)
        {
            foreach (TaskCardViewModel card in Cards)
                if (card.State == TaskCardState.Busy) { _held = card; Avatar.IsHoldingCard = true; break; }
        }
        double hold = Avatar.CurrentFrame.Hold;
        if (!snapshot && _held is not null && _held.State == TaskCardState.Busy && !_waiting && hold > .9 &&
            AutoAdvanceProgress && _held.Progress.HasValue)
        {
            _held.Progress = _held.Progress.Value + dt / _held.DurationSeconds;
            if (_held.Progress >= 1) CompleteTask();
        }
        Point avatarTopLeft = Avatar.TranslatePoint(default, this);
        double avatarCentreX = avatarTopLeft.X + Avatar.ActualWidth / 2;
        Point holdHand = Avatar.HandTipIn(this, new WingAngles(32, 80, 18));
        Point actualHand = Avatar.HandTipIn(this);
        double handX = holdHand.X + (actualHand.X - holdHand.X) * hold;
        double handY = holdHand.Y + (actualHand.Y - holdHand.Y) * hold;
        double follow = snapshot ? 1 : 1 - Math.Exp(-dt * 7);
        int count = Cards.Count;
        HeldCardCentreInRing = null;
        for (int index = 0; index < count; index++)
        {
            TaskCardViewModel card = Cards[index];
            CardVisual visual = _visuals[card];
            OrbitTarget orbit = OrbitLayout.Place(index, count, _orbit, card.State,
                avatarCentreX, avatarTopLeft.Y, ActualWidth / 2, ActualWidth);
            bool held = ReferenceEquals(card, _held);
            double targetX = held ? Math.Clamp(handX - 142 / 2.0 + 4, 71, Math.Max(71, ActualWidth - 71)) : orbit.X;
            double targetY = held ? handY + 8 : orbit.Y;
            visual.Update(targetX, targetY, held ? 1.02 : orbit.Scale, held ? 1 : orbit.Opacity,
                held ? -4 : 0, follow, now, DarkTheme, card.State, card.Progress);
            Canvas layer = held || orbit.Z > 0 ? _front : _back;
            if (!ReferenceEquals(visual.Border.Parent, layer))
            {
                (visual.Border.Parent as Panel)?.Children.Remove(visual.Border);
                layer.Children.Add(visual.Border);
            }
            if (held)
            {
                Point centre = new(visual.X, visual.Y);
                HeldCardCentreInRing = centre;
                Avatar.SetHeldCardGaze(PointToScreen(centre));
            }
        }
    }

    private sealed class CardVisual
    {
        private const double CardWidth = 142, CardHeight = 62;
        private static readonly ResourceDictionary DarkPalette = new()
        {
            Source = new Uri("/Igloo.Mascot.Wpf;component/Themes/CardDark.xaml", UriKind.Relative)
        };
        private static readonly ResourceDictionary LightPalette = new()
        {
            Source = new Uri("/Igloo.Mascot.Wpf;component/Themes/CardLight.xaml", UriKind.Relative)
        };
        private readonly Border _icon, _barFill;
        private readonly TextBlock _iconText;
        private readonly Border _progressTrack;
        private readonly DropShadowEffect _shadow;
        private readonly ScaleTransform _scale = new();
        private readonly RotateTransform _rotation = new();
        private readonly RotateTransform _iconRotation = new();
        private double _x, _y, _s = 1, _opacity = 1, _angle;
        private bool _placed;

        public Border Border { get; }
        public double X => _x;
        public double Y => _y;

        private static Brush Palette(bool dark, string key) => (Brush)(dark ? DarkPalette[key] : LightPalette[key])!;

        public CardVisual(TaskCardViewModel card)
        {
            _shadow = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 3, Opacity = .32, Color = Colors.Black };
            var title = new TextBlock { FontSize = 10, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
            title.SetBinding(TextBlock.TextProperty, new Binding(nameof(TaskCardViewModel.Title)) { Source = card });
            var subtitle = new TextBlock { FontSize = 8, Opacity = .73, TextTrimming = TextTrimming.CharacterEllipsis };
            subtitle.SetBinding(TextBlock.TextProperty, new Binding(nameof(TaskCardViewModel.Subtitle)) { Source = card });
            _iconText = new TextBlock { FontSize = 14, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center };
            _icon = new Border { Width = 18, Height = 18, CornerRadius = new CornerRadius(9), BorderThickness = new Thickness(1.5),
                VerticalAlignment = VerticalAlignment.Top, Child = _iconText,
                RenderTransform = _iconRotation, RenderTransformOrigin = new Point(.5, .5) };
            _barFill = new Border { Height = 3, HorizontalAlignment = HorizontalAlignment.Left, CornerRadius = new CornerRadius(2) };
            _progressTrack = new Border { Height = 3, Background = new SolidColorBrush(Color.FromArgb(70, 142, 155, 176)),
                CornerRadius = new CornerRadius(2), Margin = new Thickness(0, 5, 0, 0), Child = _barFill };
            var text = new StackPanel { Margin = new Thickness(7, 0, 0, 0) };
            text.Children.Add(title);
            text.Children.Add(subtitle);
            text.Children.Add(_progressTrack);
            var layout = new Grid { Margin = new Thickness(10, 9, 8, 7) };
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
            layout.ColumnDefinitions.Add(new ColumnDefinition());
            layout.Children.Add(_icon);
            Grid.SetColumn(text, 1);
            layout.Children.Add(text);
            Border = new Border { Width = CardWidth, MinHeight = CardHeight, CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1.5), Child = layout, Effect = _shadow,
                RenderTransformOrigin = new Point(.5, .5), IsHitTestVisible = false };
            var transform = new TransformGroup();
            transform.Children.Add(_scale);
            transform.Children.Add(_rotation);
            Border.RenderTransform = transform;
            AutomationProperties.SetName(Border, card.Title);
        }

        public void Update(double x, double y, double scale, double opacity, double rotation,
            double follow, double time, bool dark, TaskCardState state, double? progress)
        {
            if (!_placed) { _x = x; _y = y; _s = scale; _opacity = opacity; _angle = rotation; _placed = true; }
            else
            {
                _x += (x - _x) * follow; _y += (y - _y) * follow;
                _s += (scale - _s) * follow; _opacity += (opacity - _opacity) * follow;
                _angle += (rotation - _angle) * follow;
            }
            Canvas.SetLeft(Border, _x - CardWidth / 2);
            Canvas.SetTop(Border, _y - CardHeight / 2);
            _scale.ScaleX = _scale.ScaleY = _s;
            _rotation.Angle = _angle;
            Border.Opacity = _opacity;
            string accentKey = state switch
            {
                TaskCardState.Busy => "Mascot.Card.Busy",
                TaskCardState.Done => "Mascot.Card.Done",
                TaskCardState.Failed => "Mascot.Card.Failed",
                _ => "Mascot.Card.Todo"
            };
            Brush accent = Palette(dark, accentKey);
            bool highContrast = SystemParameters.HighContrast;
            Border.Background = highContrast ? SystemColors.WindowBrush : Palette(dark, "Mascot.Card.Panel");
            Border.BorderBrush = highContrast ? SystemColors.WindowTextBrush : accent;
            Border.SetValue(TextElement.ForegroundProperty, highContrast ? SystemColors.WindowTextBrush :
                Palette(dark, "Mascot.Card.Text"));
            _icon.BorderBrush = highContrast ? SystemColors.WindowTextBrush : accent;
            _icon.Background = state == TaskCardState.Todo ? Brushes.Transparent :
                highContrast ? SystemColors.HighlightBrush : accent;
            _iconText.Text = state switch { TaskCardState.Done => "✓", TaskCardState.Failed => "×", TaskCardState.Busy => "↻", _ => "" };
            _iconText.Foreground = state == TaskCardState.Todo ? Brushes.Transparent :
                highContrast ? SystemColors.HighlightTextBrush : Brushes.Black;
            _iconRotation.Angle = state == TaskCardState.Busy ? (time * 200) % 360 : 0;
            _progressTrack.Visibility = state == TaskCardState.Busy ? Visibility.Visible : Visibility.Collapsed;
            _barFill.Background = accent;
            _barFill.Width = progress.HasValue ? 96 * progress.Value : 28;
            if (state == TaskCardState.Busy && !progress.HasValue)
                _barFill.Margin = new Thickness((Math.Sin(time * 3) + 1) * 34, 0, 0, 0);
            else _barFill.Margin = default;
            _shadow.Opacity = state == TaskCardState.Busy ? .25 + .18 * (1 + Math.Sin(time * 2 * Math.PI / 1.8)) / 2 : .32;
        }
    }
}
