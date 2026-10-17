using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using Igloo.Mascot.Core;

namespace Igloo.Mascot.Wpf;

/// <summary>Native vector Tux with a measured, continuously skinned wing rig.</summary>
public sealed class TuxAvatar : Control
{
    private static readonly (WingProfile Left, WingProfile Right) Profiles = WingProfile.LoadArtwork();
    private readonly MascotMotion _motion = new();
    private readonly Stopwatch _clock = new();
    private readonly WingPainter _left = new(Profiles.Left, false, Color.FromRgb(124, 124, 124));
    private readonly WingPainter _right = new(Profiles.Right, true, Color.FromRgb(131, 131, 132));
    private Viewbox? _viewbox;
    private Canvas? _root, _tux, _face, _leftPupil, _rightPupil, _rightShadow;
    private Path? _headGlare, _leftLid, _rightLid;
    private Canvas? _lowerJaw, _upperJaw;
    private readonly TranslateTransform _bobTransform = new();
    private readonly TranslateTransform _leftEyeTransform = new(), _rightEyeTransform = new();
    private readonly TranslateTransform _leftLidTransform = new(), _rightLidTransform = new();
    private readonly TranslateTransform _lowerJawTransform = new(), _upperJawTransform = new();
    private readonly MatrixTransform _rightShadowTransform = new();
    private readonly RotateTransform _headRotate = new(0, 108, 108);
    private readonly TranslateTransform _headTranslate = new();
    private readonly TransformGroup _headTransform = new();
    private readonly Ellipse[] _pivotDots = new Ellipse[6];
    private Window? _window;
    private double _lastTime;
    private bool _rendering;
    private Point? _heldCardCentreScreen, _waitingTargetScreen;
    private MascotFrame _frame;

    static TuxAvatar()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(TuxAvatar), new FrameworkPropertyMetadata(typeof(TuxAvatar)));
    }

    public TuxAvatar()
    {
        _headTransform.Children.Add(_headRotate);
        _headTransform.Children.Add(_headTranslate);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateRendering();
    }

    private static DependencyProperty Number(string name, double initial, double min, double max) =>
        DependencyProperty.Register(name, typeof(double), typeof(TuxAvatar),
            new FrameworkPropertyMetadata(initial, FrameworkPropertyMetadataOptions.AffectsRender,
                null, (_, value) => Math.Clamp((double)value, min, max)));

    public static readonly DependencyProperty LeftShoulderProperty = Number(nameof(LeftShoulder), 0, -20, 160);
    public static readonly DependencyProperty LeftArmProperty = Number(nameof(LeftArm), 0, -90, 90);
    public static readonly DependencyProperty LeftHandProperty = Number(nameof(LeftHand), 0, -70, 70);
    public static readonly DependencyProperty RightShoulderProperty = Number(nameof(RightShoulder), 0, -20, 160);
    public static readonly DependencyProperty RightArmProperty = Number(nameof(RightArm), 0, -90, 90);
    public static readonly DependencyProperty RightHandProperty = Number(nameof(RightHand), 0, -70, 70);
    public static readonly DependencyProperty HeadTiltProperty = Number(nameof(HeadTilt), 0, -15, 15);
    public static readonly DependencyProperty HeadTurnProperty = Number(nameof(HeadTurn), 0, -8, 8);
    public static readonly DependencyProperty MouthOpenProperty = Number(nameof(MouthOpen), 0, 0, 1);
    public static readonly DependencyProperty EyesClosedProperty = Number(nameof(EyesClosed), 0, 0, 1);
    public static readonly DependencyProperty ElbowPositionProperty = Number(nameof(ElbowPosition), .38, .15, .6);
    public static readonly DependencyProperty WristPositionProperty = Number(nameof(WristPosition), .78, .55, .95);
    public static readonly DependencyProperty BlendSoftnessProperty = Number(nameof(BlendSoftness), .14, .04, .3);
    public static readonly DependencyProperty FollowCursorProperty = DependencyProperty.Register(nameof(FollowCursor),
        typeof(bool), typeof(TuxAvatar), new PropertyMetadata(false));
    public static readonly DependencyProperty ShowPivotsProperty = DependencyProperty.Register(nameof(ShowPivots),
        typeof(bool), typeof(TuxAvatar), new PropertyMetadata(false));
    public static readonly DependencyProperty IsHoldingCardProperty = DependencyProperty.Register(nameof(IsHoldingCard),
        typeof(bool), typeof(TuxAvatar), new PropertyMetadata(false));
    public static readonly DependencyProperty WaitingForUserProperty = DependencyProperty.Register(nameof(WaitingForUser),
        typeof(bool), typeof(TuxAvatar), new PropertyMetadata(false));

    public double LeftShoulder { get => (double)GetValue(LeftShoulderProperty); set => SetValue(LeftShoulderProperty, value); }
    public double LeftArm { get => (double)GetValue(LeftArmProperty); set => SetValue(LeftArmProperty, value); }
    public double LeftHand { get => (double)GetValue(LeftHandProperty); set => SetValue(LeftHandProperty, value); }
    public double RightShoulder { get => (double)GetValue(RightShoulderProperty); set => SetValue(RightShoulderProperty, value); }
    public double RightArm { get => (double)GetValue(RightArmProperty); set => SetValue(RightArmProperty, value); }
    public double RightHand { get => (double)GetValue(RightHandProperty); set => SetValue(RightHandProperty, value); }
    public double HeadTilt { get => (double)GetValue(HeadTiltProperty); set => SetValue(HeadTiltProperty, value); }
    public double HeadTurn { get => (double)GetValue(HeadTurnProperty); set => SetValue(HeadTurnProperty, value); }
    public double MouthOpen { get => (double)GetValue(MouthOpenProperty); set => SetValue(MouthOpenProperty, value); }
    public double EyesClosed { get => (double)GetValue(EyesClosedProperty); set => SetValue(EyesClosedProperty, value); }
    public double ElbowPosition { get => (double)GetValue(ElbowPositionProperty); set => SetValue(ElbowPositionProperty, value); }
    public double WristPosition { get => (double)GetValue(WristPositionProperty); set => SetValue(WristPositionProperty, value); }
    public double BlendSoftness { get => (double)GetValue(BlendSoftnessProperty); set => SetValue(BlendSoftnessProperty, value); }
    public bool FollowCursor { get => (bool)GetValue(FollowCursorProperty); set => SetValue(FollowCursorProperty, value); }
    public bool ShowPivots { get => (bool)GetValue(ShowPivotsProperty); set => SetValue(ShowPivotsProperty, value); }
    public bool IsHoldingCard { get => (bool)GetValue(IsHoldingCardProperty); set => SetValue(IsHoldingCardProperty, value); }
    public bool WaitingForUser { get => (bool)GetValue(WaitingForUserProperty); set => SetValue(WaitingForUserProperty, value); }
    public MascotFrame CurrentFrame => _frame;
    public long RenderedFrames { get; private set; }
    public ILipSyncSource? LipSyncSource { get => _motion.LipSyncSource; set => _motion.LipSyncSource = value; }
    public event EventHandler? FrameRendered;

    public void Trigger(MascotAction action) => _motion.Trigger(action, _clock.Elapsed.TotalSeconds);
    public void Talk(double seconds = 3.5) => _motion.Talk(_clock.Elapsed.TotalSeconds, seconds);
    public void WordBoundary() => _motion.WordBoundary();
    public void SetWaiting(bool value) => WaitingForUser = value;
    public void SetGazeTarget(Point screenPoint) => _waitingTargetScreen = screenPoint;
    public void SetHeldCardGaze(Point screenPoint) => _heldCardCentreScreen = screenPoint;

    /// <summary>Diagnostic only: removes WPF blur effects for an A/B performance sample.</summary>
    public void DisableBlurEffectsForDiagnostics()
    {
        ApplyTemplate();
        if (_root is not null) RemoveBlur(_root);
    }

    private static void RemoveBlur(DependencyObject visual)
    {
        if (visual is UIElement element && element.Effect is BlurEffect) element.Effect = null;
        int children = VisualTreeHelper.GetChildrenCount(visual);
        for (int i = 0; i < children; i++) RemoveBlur(VisualTreeHelper.GetChild(visual, i));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _viewbox = GetTemplateChild("PART_Viewbox") as Viewbox;
        _root = GetTemplateChild("PART_Root") as Canvas;
        _tux = GetTemplateChild("tux") as Canvas;
        _face = GetTemplateChild("face") as Canvas;
        _headGlare = GetTemplateChild("head_glare") as Path;
        _leftPupil = GetTemplateChild("pupil_left") as Canvas;
        _rightPupil = GetTemplateChild("pupil_right") as Canvas;
        _leftLid = GetTemplateChild("eyelid_left") as Path;
        _rightLid = GetTemplateChild("eyelid_right") as Path;
        _lowerJaw = GetTemplateChild("mandible_lower") as Canvas;
        _upperJaw = GetTemplateChild("mandible_upper") as Canvas;
        _rightShadow = GetTemplateChild("wing_tip_right_shadow") as Canvas;
        if (_root is null || _tux is null || _face is null || _headGlare is null ||
            _leftPupil is null || _rightPupil is null || _leftLid is null || _rightLid is null ||
            _lowerJaw is null || _upperJaw is null || _rightShadow is null ||
            GetTemplateChild("wing_left") is not Canvas wingLeft ||
            GetTemplateChild("wing_right") is not Canvas wingRight)
            throw new InvalidOperationException("Tux vector template lost a required SVG id.");
        _left.Attach(wingLeft);
        _right.Attach(wingRight);
        _tux.RenderTransform = _bobTransform;
        _leftPupil.RenderTransform = _leftEyeTransform;
        _rightPupil.RenderTransform = _rightEyeTransform;
        _leftLid.RenderTransform = _leftLidTransform;
        _rightLid.RenderTransform = _rightLidTransform;
        _lowerJaw.RenderTransform = _lowerJawTransform;
        _upperJaw.RenderTransform = _upperJawTransform;
        _rightShadow.RenderTransform = _rightShadowTransform;
        _face.RenderTransform = _headTransform;
        _headGlare.RenderTransform = _headTransform;
        for (int i = 0; i < _pivotDots.Length; i++)
        {
            var dot = new Ellipse { Width = 6, Height = 6,
                Fill = new SolidColorBrush((i % 3) switch { 0 => Colors.Red, 1 => Colors.Yellow, _ => Colors.Cyan }),
                Stroke = Brushes.White, StrokeThickness = 1, IsHitTestVisible = false };
            _pivotDots[i] = dot;
            _root.Children.Add(dot);
        }
        // WPF BlurEffect on the SVG's many tiny highlights proved expensive on
        // this render host. The source conversion retains those effects for
        // audit, while the live art uses its existing gradients and wing halo.
        RemoveBlur(_root);
        Paint(_frame);
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        _window = Window.GetWindow(this);
        if (_window is not null) _window.StateChanged += OnWindowStateChanged;
        if (!_clock.IsRunning) _clock.Start();
        UpdateRendering();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        StopRendering();
        if (_window is not null) _window.StateChanged -= OnWindowStateChanged;
        _window = null;
    }

    private void OnWindowStateChanged(object? sender, EventArgs args) => UpdateRendering();

    private void UpdateRendering()
    {
        bool shouldRun = IsLoaded && IsVisible && _window?.WindowState != WindowState.Minimized;
        if (shouldRun && !_rendering)
        {
            _lastTime = _clock.Elapsed.TotalSeconds;
            CompositionTarget.Rendering += OnRendering;
            _rendering = true;
        }
        else if (!shouldRun) StopRendering();
    }

    private void StopRendering()
    {
        if (!_rendering) return;
        CompositionTarget.Rendering -= OnRendering;
        _rendering = false;
    }

    private void OnRendering(object? sender, EventArgs args)
    {
        double now = _clock.Elapsed.TotalSeconds;
        double dt = Math.Min(.05, now - _lastTime);
        _lastTime = now;
        var manual = new ManualPose(new(LeftShoulder, LeftArm, LeftHand),
            new(RightShoulder, RightArm, RightHand), HeadTilt, HeadTurn, MouthOpen, EyesClosed);
        Vec2? cursor = FollowCursor && _viewbox is not null
            ? GazeSolver.FromScreen(ToVec(System.Windows.Input.Mouse.GetPosition(_viewbox)), default,
                new(Math.Max(1, _viewbox.ActualWidth), Math.Max(1, _viewbox.ActualHeight))) : null;
        Vec2? card = _heldCardCentreScreen is Point held && IsHoldingCard ? Normalized(held) : null;
        Vec2? wait = _waitingTargetScreen is Point waiting && WaitingForUser ? Normalized(waiting) : null;
        _frame = _motion.Step(now, dt, manual, new(cursor, card, wait), IsHoldingCard,
            WaitingForUser, !SystemParameters.ClientAreaAnimation);
        Paint(_frame);
        RenderedFrames++;
        FrameRendered?.Invoke(this, EventArgs.Empty);
    }

    private Vec2 Normalized(Point screenPoint)
    {
        if (_viewbox is null) return default;
        Point p = _viewbox.PointFromScreen(screenPoint);
        return GazeSolver.FromScreen(new(p.X, p.Y), default,
            new(Math.Max(1, _viewbox.ActualWidth), Math.Max(1, _viewbox.ActualHeight)));
    }

    private static Vec2 ToVec(Point point) => new(point.X, point.Y);

    private void Paint(MascotFrame frame)
    {
        if (_root is null || _tux is null || _face is null || _headGlare is null) return;
        WingTuning tuning = new(ElbowPosition, WristPosition, BlendSoftness);
        WingJoints left = _left.Paint(frame.Left, tuning);
        WingJoints right = _right.Paint(frame.Right, tuning);
        _bobTransform.Y = frame.Bob;
        _headRotate.Angle = frame.HeadTilt;
        _headTranslate.X = frame.HeadTurn;
        _leftEyeTransform.X = _rightEyeTransform.X = frame.EyeX;
        _leftEyeTransform.Y = _rightEyeTransform.Y = frame.EyeY;
        _leftLidTransform.Y = _rightLidTransform.Y = frame.Blink * 26;
        _lowerJawTransform.Y = frame.Mouth * 5;
        _upperJawTransform.Y = -frame.Mouth;
        Affine m = right.HandMatrix;
        _rightShadowTransform.Matrix = new Matrix(m.A, m.B, m.C, m.D, m.E, m.F);
        if (_rightShadow is not null)
            _rightShadow.Opacity = 1 - Math.Clamp((Math.Abs(frame.Right.Shoulder) +
                .6 * Math.Abs(frame.Right.Arm) + .4 * Math.Abs(frame.Right.Hand) - 4) / 8, 0, 1);
        PutDots(0, left, frame.Bob);
        PutDots(3, right, frame.Bob);
    }

    private void PutDots(int offset, WingJoints joints, double bob)
    {
        for (int i = 0; i < 3; i++)
        {
            Ellipse dot = _pivotDots[offset + i];
            if (dot is null) continue;
            Vec2 position = i switch { 0 => joints.Shoulder, 1 => joints.Elbow, _ => joints.Wrist };
            dot.Visibility = ShowPivots ? Visibility.Visible : Visibility.Collapsed;
            Canvas.SetLeft(dot, position.X - 3);
            Canvas.SetTop(dot, position.Y + bob - 3);
        }
    }

    public Point HandTipIn(UIElement ancestor, WingAngles? pose = null)
    {
        if (_root is null) return default;
        Vec2 tip = pose.HasValue ? Profiles.Left.Joints(pose.Value,
            new(ElbowPosition, WristPosition, BlendSoftness)).HandTip : _left.LastJoints.HandTip;
        return _root.TransformToAncestor(ancestor).Transform(new Point(tip.X, tip.Y + _frame.Bob));
    }

    /// <summary>Deterministic pose rendering for visual snapshots; next animation frame resumes normal motion.</summary>
    public void RenderExactPose(WingAngles left, WingAngles right, double headTilt = 0,
        double headTurn = 0, double mouth = 0, double blink = 0, double eyeX = 0, double eyeY = 0,
        bool waiting = false)
    {
        _frame = new(left, right, headTilt, headTurn, eyeX, eyeY, 0, mouth, blink, 0,
            waiting ? 1 : 0, waiting ? GazePriority.WaitingForUser : GazePriority.Drift);
        Paint(_frame);
    }

    private sealed class WingPainter(WingProfile profile, bool viewerRight, Color glareColour)
    {
        private readonly Vec2[] _points = new Vec2[WingProfile.Stations];
        private readonly Vec2[] _normals = new Vec2[WingProfile.Stations];
        private readonly StreamGeometry _outline = new() { FillRule = FillRule.Nonzero };
        private readonly StreamGeometry _halo = new() { FillRule = FillRule.Nonzero };
        private readonly StreamGeometry _ribbon = new() { FillRule = FillRule.Nonzero };
        private Path? _base, _glare;
        public WingJoints LastJoints { get; private set; }

        public void Attach(Canvas canvas)
        {
            _base = new Path { Data = _outline, Fill = new SolidColorBrush(Color.FromRgb(2, 2, 4)),
                IsHitTestVisible = false };
            var halo = new Path { Data = _halo, Fill = new SolidColorBrush(glareColour), Opacity = .25,
                Clip = _outline, IsHitTestVisible = false };
            _glare = new Path { Data = _ribbon, Fill = new SolidColorBrush(glareColour), Opacity = .93,
                Clip = _outline, IsHitTestVisible = false };
            canvas.Children.Add(_base);
            canvas.Children.Add(halo);
            canvas.Children.Add(_glare);
        }

        private Point Edge(int i, double fraction)
        {
            Vec2 p = _points[i] + _normals[i] * (fraction * profile.Width[i] / 2);
            return new Point(p.X, p.Y);
        }

        public WingJoints Paint(WingAngles angles, WingTuning tuning)
        {
            LastJoints = profile.Deform(angles, tuning, viewerRight, _points, _normals);
            using (StreamGeometryContext context = _outline.Open())
            {
                context.BeginFigure(Edge(0, 1), true, true);
                for (int i = 1; i < WingProfile.Stations; i++) context.LineTo(Edge(i, 1), true, false);
                double radius = profile.Width[^1] / 2;
                context.ArcTo(Edge(WingProfile.Stations - 1, -1), new Size(radius, radius), 0,
                    false, SweepDirection.Counterclockwise, true, false);
                for (int i = WingProfile.Stations - 2; i >= 0; i--) context.LineTo(Edge(i, -1), true, false);
                radius = profile.Width[0] / 2;
                context.ArcTo(Edge(0, 1), new Size(radius, radius), 0,
                    false, SweepDirection.Counterclockwise, true, false);
            }
            using (StreamGeometryContext context = _ribbon.Open())
            {
                int g0 = profile.GlareStart, g1 = profile.GlareEnd;
                context.BeginFigure(Edge(g0, Math.Max(profile.GlareHigh[g0], profile.GlareLow[g0] + .14)), true, true);
                for (int i = g0 + 1; i <= g1; i++)
                    context.LineTo(Edge(i, Math.Max(profile.GlareHigh[i], profile.GlareLow[i] + .14)), true, false);
                for (int i = g1; i >= g0; i--) context.LineTo(Edge(i, profile.GlareLow[i]), true, false);
            }
            using (StreamGeometryContext context = _halo.Open())
            {
                int g0 = profile.GlareStart, g1 = profile.GlareEnd;
                context.BeginFigure(Edge(g0, Math.Min(1, profile.GlareHigh[g0] + .18)), true, true);
                for (int i = g0 + 1; i <= g1; i++)
                    context.LineTo(Edge(i, Math.Min(1, profile.GlareHigh[i] + .18)), true, false);
                for (int i = g1; i >= g0; i--)
                    context.LineTo(Edge(i, Math.Max(-1, profile.GlareLow[i] - .18)), true, false);
            }
            return LastJoints;
        }
    }
}
