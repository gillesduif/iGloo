using System.Collections;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Igloo.App.Controls;

/// <summary>
/// A 3D Cover Flow carousel built on <see cref="Viewport3D"/>: textured quads on a path
/// that recedes into depth, the selected cover centered, facing the camera and lit head-on,
/// flanking covers rotated away and fading into fog, each with a floor reflection.
///
/// Reusable and MVVM-clean: bind <see cref="ItemsSource"/>, <see cref="SelectedItem"/> /
/// <see cref="SelectedIndex"/>, supply a <see cref="CoverImageResolver"/> for textures and
/// an optional <see cref="ConfirmCommand"/> (fired by Enter or clicking the centered cover,
/// gated by <see cref="CanConfirm"/>). Code-behind here is exclusively scene, camera and
/// animation state.
///
/// Animation is a single <see cref="CompositionTarget.Rendering"/> loop easing one scalar
/// "flow offset" toward the selected index (rather than Storyboards): selection retargets
/// mid-flight constantly, and one frame callback drives every cover transform plus the
/// camera dolly without storyboard churn or handoff snapping. The callback detaches as
/// soon as the scene settles, so idle cost is zero.
/// </summary>
public sealed class CoverFlow3DControl : FrameworkElement
{
    // ── Scene constants ──────────────────────────────────────────────────────

    private const double FlankAngleDeg   = 56;    // Y rotation of side covers
    private const double FirstFlankX     = 2.05;  // gap between center and first flank
    private const double FlankSpacingX   = 0.62;  // spacing between subsequent flanks
    private const double FlankDepthZ     = 1.30;  // how far the first flank drops back
    private const double FlankRecedeZ    = 0.34;  // additional depth per further flank
    private const double CenterScale     = 1.16;  // the centered cover is slightly larger
    private const double CameraBaseZ     = 6.1;
    private const double CameraDollyMax  = 0.85;  // dolly-out at high transition speed
    private const double EaseRate        = 9.0;   // exponential ease-out (~330 ms to settle)

    private const int FullResPixels      = 512;   // texture size near center
    private const int LowResPixels       = 144;   // texture size beyond the window
    private const int FullResWindow      = 4;     // ± positions that get full-res textures

    private static readonly Color BackgroundColor = Color.FromRgb(0x07, 0x0B, 0x16);

    /// <summary>Cover quad spans x,y ∈ [-1,1] at z=0; rotation pivots its own center.</summary>
    private static readonly MeshGeometry3D FrontMesh = BuildQuad(
        new Point3D(-1, -1, 0), new Point3D(1, -1, 0), new Point3D(1, 1, 0), new Point3D(-1, 1, 0),
        new Point(0, 1), new Point(1, 1), new Point(1, 0), new Point(0, 0));

    /// <summary>
    /// Floor-reflection quad below the cover. Texture coordinates run bottom-up so the
    /// (unflipped) cover brush appears vertically mirrored.
    /// </summary>
    private static readonly MeshGeometry3D ReflectionMesh = BuildQuad(
        new Point3D(-1, -3.08, 0), new Point3D(1, -3.08, 0), new Point3D(1, -1.08, 0), new Point3D(-1, -1.08, 0),
        new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(0, 1));

    // ── Visual tree ──────────────────────────────────────────────────────────

    private readonly Grid              _root;
    private readonly Border            _focusRing;
    private readonly Viewport3D        _viewport;
    private readonly PerspectiveCamera _camera;
    private readonly ModelVisual3D     _lightsVisual;

    // ── Scene/animation state (the only state this control owns) ────────────

    private readonly List<Cover>                _covers       = [];
    private readonly Dictionary<Model3D, int>   _modelToIndex = [];
    private readonly List<CoverItemAutomationPeer> _itemPeers = [];
    private List<object> _items = [];
    private double _offset;          // continuous flow position, eased toward _target
    private double _target;
    private bool   _animating;
    private bool   _syncingSelection;
    private TimeSpan _lastRenderTime;

    public CoverFlow3DControl()
    {
        Focusable = true;
        FocusVisualStyle = null; // replaced by the custom focus ring below

        _camera = new PerspectiveCamera
        {
            FieldOfView   = 62,
            UpDirection   = new Vector3D(0, 1, 0),
            Position      = new Point3D(0, 0.30, CameraBaseZ),
            LookDirection = new Vector3D(0, -0.10, -1),
        };

        // AmbientLight keeps everything readable; the DirectionalLight points straight
        // down the camera axis, so the centered (camera-facing) cover catches it fully
        // while rotated flanks fall off by the cosine of their angle - the selected
        // distro literally glows brighter than the rest.
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0x5C, 0x5E, 0x6A)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0xF2, 0xF2, 0xFA), new Vector3D(-0.06, -0.22, -1)));
        lights.Freeze();
        _lightsVisual = new ModelVisual3D { Content = lights };

        _viewport = new Viewport3D { Camera = _camera, ClipToBounds = true };
        _viewport.Children.Add(_lightsVisual);

        _focusRing = new Border
        {
            BorderThickness  = new Thickness(1.5),
            CornerRadius     = new CornerRadius(12),
            BorderBrush      = Brushes.Transparent,
            IsHitTestVisible = false,
        };

        _root = new Grid { Background = Brushes.Transparent };
        _root.Children.Add(_viewport);
        _root.Children.Add(_focusRing);
        AddVisualChild(_root);
        AddLogicalChild(_root);

        IsKeyboardFocusedChanged += (_, _) => UpdateFocusRing();
        Unloaded += (_, _) => StopAnimation();
    }

    // ── Public surface ───────────────────────────────────────────────────────

    public static readonly DependencyProperty ItemsSourceProperty = DependencyProperty.Register(
        nameof(ItemsSource), typeof(IEnumerable), typeof(CoverFlow3DControl),
        new PropertyMetadata(null, (d, _) => ((CoverFlow3DControl)d).OnItemsSourceChanged()));

    public IEnumerable? ItemsSource
    {
        get => (IEnumerable?)GetValue(ItemsSourceProperty);
        set => SetValue(ItemsSourceProperty, value);
    }

    public static readonly DependencyProperty SelectedItemProperty = DependencyProperty.Register(
        nameof(SelectedItem), typeof(object), typeof(CoverFlow3DControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, e) => ((CoverFlow3DControl)d).OnSelectedItemChanged(e.NewValue)));

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(CoverFlow3DControl),
        new FrameworkPropertyMetadata(-1, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
            (d, _) => ((CoverFlow3DControl)d).OnSelectedIndexChanged()));

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>Command fired by Enter / clicking the centered cover, gated by <see cref="CanConfirm"/>.</summary>
    public static readonly DependencyProperty ConfirmCommandProperty = DependencyProperty.Register(
        nameof(ConfirmCommand), typeof(ICommand), typeof(CoverFlow3DControl), new PropertyMetadata(null));

    public ICommand? ConfirmCommand
    {
        get => (ICommand?)GetValue(ConfirmCommandProperty);
        set => SetValue(ConfirmCommandProperty, value);
    }

    public static readonly DependencyProperty CanConfirmProperty = DependencyProperty.Register(
        nameof(CanConfirm), typeof(bool), typeof(CoverFlow3DControl), new PropertyMetadata(true));

    public bool CanConfirm
    {
        get => (bool)GetValue(CanConfirmProperty);
        set => SetValue(CanConfirmProperty, value);
    }

    /// <summary>
    /// Resolves an item to a cover texture at the requested pixel edge length. The control
    /// asks for <see cref="FullResPixels"/> near the center and <see cref="LowResPixels"/>
    /// beyond ±<see cref="FullResWindow"/> positions.
    /// </summary>
    public Func<object, int, ImageSource?>? CoverImageResolver { get; set; }

    /// <summary>Resolves an item to its accessible name (UI Automation / Narrator).</summary>
    public Func<object, string>? ItemNameResolver { get; set; }

    // ── FrameworkElement plumbing ────────────────────────────────────────────

    protected override int VisualChildrenCount => 1;
    protected override Visual GetVisualChild(int index) => _root;

    protected override Size MeasureOverride(Size availableSize)
    {
        _root.Measure(availableSize);
        return new Size(
            double.IsInfinity(availableSize.Width) ? 640 : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? 360 : availableSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _root.Arrange(new Rect(finalSize));
        return finalSize;
    }

    // ── Selection plumbing ───────────────────────────────────────────────────

    private void OnItemsSourceChanged()
    {
        _items = ItemsSource?.Cast<object>().ToList() ?? [];

        // Keep the bound SelectedItem when it survives the refresh; otherwise center
        // the first cover (the carousel always has a center).
        var index = SelectedItem is { } current ? _items.IndexOf(current) : -1;
        if (index < 0 && _items.Count > 0) index = 0;

        _syncingSelection = true;
        try
        {
            SelectedIndex = index;
            SelectedItem  = index >= 0 ? _items[index] : null;
        }
        finally
        {
            _syncingSelection = false;
        }

        BuildScene();

        // New scene: snap into place, no cross-refresh animation.
        _target = _offset = Math.Max(index, 0);
        LayoutScene();
        ScheduleTextureRefresh();
    }

    private void OnSelectedItemChanged(object? newValue)
    {
        if (_syncingSelection) return;

        if (newValue is null)
        {
            // External null (initial binding activation, or the previous selection became
            // incompatible): the carousel always keeps a centered cover, so re-assert it
            // as the selection. Deferred - a write-back during the binding's own value
            // transfer is swallowed by WPF and would never reach the source.
            Dispatcher.InvokeAsync(() =>
            {
                if (SelectedItem is null && SelectedIndex >= 0 && SelectedIndex < _items.Count)
                    SelectedItem = _items[SelectedIndex];
            }, DispatcherPriority.Loaded);
            return;
        }

        var index = _items.IndexOf(newValue);
        if (index >= 0 && index != SelectedIndex)
            SelectedIndex = index;
    }

    private void OnSelectedIndexChanged()
    {
        if (_syncingSelection) return;

        var index = Math.Clamp(SelectedIndex, _items.Count == 0 ? -1 : 0, _items.Count - 1);
        _syncingSelection = true;
        try
        {
            SelectedIndex = index;
            SelectedItem  = index >= 0 ? _items[index] : null;
        }
        finally
        {
            _syncingSelection = false;
        }

        if (index < 0) return;

        _target = index;
        StartAnimation();
        ScheduleTextureRefresh();
        AnnounceSelection(index);
    }

    // ── Input ────────────────────────────────────────────────────────────────

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Count == 0) return;

        switch (e.Key)
        {
            case Key.Left:  SelectedIndex = Math.Max(0, SelectedIndex - 1);                e.Handled = true; break;
            case Key.Right: SelectedIndex = Math.Min(_items.Count - 1, SelectedIndex + 1); e.Handled = true; break;
            case Key.Home:  SelectedIndex = 0;                                             e.Handled = true; break;
            case Key.End:   SelectedIndex = _items.Count - 1;                              e.Handled = true; break;
            case Key.Enter:
            case Key.Space: Confirm();                                                     e.Handled = true; break;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_items.Count == 0) return;
        var step = e.Delta < 0 ? 1 : -1;
        SelectedIndex = Math.Clamp(SelectedIndex + step, 0, _items.Count - 1);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        // 3D hit test: ray through the clicked pixel against the cover meshes.
        var hit = VisualTreeHelper.HitTest(_viewport, e.GetPosition(_viewport));
        if (hit is RayMeshGeometry3DHitTestResult meshHit
            && _modelToIndex.TryGetValue(meshHit.ModelHit, out var index))
        {
            if (index == SelectedIndex) Confirm();   // click the centered cover → proceed
            else SelectedIndex = index;              // click a flank → animate it to center
            e.Handled = true;
        }
    }

    private void Confirm()
    {
        if (!CanConfirm) return;
        if (ConfirmCommand is { } command && command.CanExecute(null))
            command.Execute(null);
    }

    private void UpdateFocusRing() =>
        _focusRing.BorderBrush = IsKeyboardFocused
            ? new SolidColorBrush(Color.FromArgb(0x80, 0x6C, 0x9C, 0xFF))
            : Brushes.Transparent;

    // ── Scene construction ───────────────────────────────────────────────────

    private void BuildScene()
    {
        StopAnimation();
        _viewport.Children.Clear();
        _viewport.Children.Add(_lightsVisual);
        _covers.Clear();
        _modelToIndex.Clear();
        _itemPeers.Clear();

        for (var i = 0; i < _items.Count; i++)
        {
            var cover = CreateCover(_items[i]);
            _covers.Add(cover);
            _viewport.Children.Add(cover.Visual);
            _modelToIndex[cover.FrontModel]      = i;
            _modelToIndex[cover.ReflectionModel] = i;
            _itemPeers.Add(new CoverItemAutomationPeer(this, _items[i], i));
        }

        if (UIElementAutomationPeer.FromElement(this) is CoverFlowAutomationPeer peer)
            peer.ResetChildrenCache();
    }

    private Cover CreateCover(object item)
    {
        // Fog scrim: a background-colored translucent layer over both quads whose opacity
        // grows with distance from center - flanks sit lower-lit and far covers fade into
        // the void. One unfrozen brush per cover; only its Opacity changes per frame.
        var scrim = new SolidColorBrush(BackgroundColor) { Opacity = 0 };
        var scrimMaterial = new DiffuseMaterial(scrim);

        // Emissive boost on/near the center cover, on top of the directional key light.
        var emissiveBrush = new ImageBrush { Opacity = 0 };

        var frontDiffuse = new DiffuseMaterial();
        var frontMaterial = new MaterialGroup();
        frontMaterial.Children.Add(frontDiffuse);
        frontMaterial.Children.Add(new EmissiveMaterial(emissiveBrush));
        frontMaterial.Children.Add(scrimMaterial);

        var reflectionDiffuse = new DiffuseMaterial();
        var reflectionMaterial = new MaterialGroup();
        reflectionMaterial.Children.Add(reflectionDiffuse);
        reflectionMaterial.Children.Add(scrimMaterial);

        var frontModel      = new GeometryModel3D(FrontMesh, frontMaterial);
        var reflectionModel = new GeometryModel3D(ReflectionMesh, reflectionMaterial);

        var rotation    = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
        var translation = new TranslateTransform3D();
        var scale       = new ScaleTransform3D(1, 1, 1);
        var transform   = new Transform3DGroup();
        transform.Children.Add(scale);
        transform.Children.Add(new RotateTransform3D(rotation));
        transform.Children.Add(translation);

        var group = new Model3DGroup { Transform = transform };
        group.Children.Add(frontModel);
        group.Children.Add(reflectionModel);

        return new Cover
        {
            Item              = item,
            Visual            = new ModelVisual3D { Content = group },
            Rotation          = rotation,
            Translation       = translation,
            Scale             = scale,
            Scrim             = scrim,
            EmissiveBrush     = emissiveBrush,
            FrontModel        = frontModel,
            ReflectionModel   = reflectionModel,
            FrontDiffuse      = frontDiffuse,
            ReflectionDiffuse = reflectionDiffuse,
        };
    }

    // ── Texture management ───────────────────────────────────────────────────

    /// <summary>
    /// Full-resolution textures only near the center; cheap low-res ones beyond
    /// ±<see cref="FullResWindow"/>. Runs at background priority so wheel-spamming
    /// through a 30+ distro catalog never blocks a frame.
    /// </summary>
    private void ScheduleTextureRefresh() =>
        Dispatcher.InvokeAsync(() =>
        {
            var selected = SelectedIndex;
            for (var i = 0; i < _covers.Count; i++)
            {
                var want = Math.Abs(i - selected) <= FullResWindow ? FullResPixels : LowResPixels;
                if (_covers[i].TexturePixels != want)
                    ApplyTexture(_covers[i], want);
            }
        }, DispatcherPriority.Background);

    private void ApplyTexture(Cover cover, int pixels)
    {
        var source = CoverImageResolver?.Invoke(cover.Item, pixels);

        Brush front;
        if (source is not null)
        {
            var imageBrush = new ImageBrush(source);
            imageBrush.Freeze();
            front = imageBrush;
        }
        else
        {
            front = FallbackFrontBrush;
        }

        cover.FrontDiffuse.Brush       = front;
        cover.ReflectionDiffuse.Brush  = BuildReflectionBrush(source);
        cover.EmissiveBrush.ImageSource = source;
        cover.TexturePixels            = pixels;
    }

    private static readonly Brush FallbackFrontBrush = CreateFallbackFrontBrush();

    private static Brush CreateFallbackFrontBrush()
    {
        var brush = new LinearGradientBrush(Color.FromRgb(0x2A, 0x31, 0x47), Color.FromRgb(0x14, 0x18, 0x26), 90);
        brush.Freeze();
        return brush;
    }

    /// <summary>
    /// The mirrored cover with an opacity-gradient fade baked in: near the cover the image
    /// shows through faintly, further down it dissolves into the background - a specular
    /// floor hint, not a mirror.
    /// </summary>
    private static Brush BuildReflectionBrush(ImageSource? source)
    {
        var bounds = new Rect(0, 0, 1, 1);
        var group = new DrawingGroup();

        if (source is not null)
            group.Children.Add(new ImageDrawing(source, bounds));
        else
            group.Children.Add(new GeometryDrawing(FallbackFrontBrush, null, new RectangleGeometry(bounds)));

        // Brush-space y=0 is the far end of the reflection (v=0 on the mesh).
        var fade = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint   = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0xFF, BackgroundColor.R, BackgroundColor.G, BackgroundColor.B), 0.0),
                new GradientStop(Color.FromArgb(0xFF, BackgroundColor.R, BackgroundColor.G, BackgroundColor.B), 0.55),
                new GradientStop(Color.FromArgb(0xB4, BackgroundColor.R, BackgroundColor.G, BackgroundColor.B), 1.0),
            },
        };
        group.Children.Add(new GeometryDrawing(fade, null, new RectangleGeometry(bounds)));

        var brush = new DrawingBrush(group) { Stretch = Stretch.Fill };
        brush.Freeze();
        return brush;
    }

    // ── Animation ────────────────────────────────────────────────────────────

    private void StartAnimation()
    {
        // Respect the system reduced-motion preference: snap instead of easing.
        if (!SystemParameters.ClientAreaAnimation)
        {
            StopAnimation();
            _offset = _target;
            LayoutScene();
            return;
        }

        if (_animating) return;
        _animating = true;
        _lastRenderTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopAnimation()
    {
        if (!_animating) return;
        _animating = false;
        CompositionTarget.Rendering -= OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_lastRenderTime == TimeSpan.Zero)
        {
            _lastRenderTime = now;
            return;
        }

        var dt = Math.Min((now - _lastRenderTime).TotalSeconds, 0.05);
        _lastRenderTime = now;
        if (dt <= 0) return;

        // Exponential ease-out toward the target - equivalent feel to a ~330 ms
        // CubicEase Storyboard, but retargetable every frame without a restart.
        _offset += (_target - _offset) * (1 - Math.Exp(-EaseRate * dt));

        if (Math.Abs(_target - _offset) < 0.002)
        {
            _offset = _target;
            StopAnimation();
        }

        LayoutScene();
    }

    /// <summary>Positions every cover (and the camera) for the current flow offset.</summary>
    private void LayoutScene()
    {
        for (var i = 0; i < _covers.Count; i++)
        {
            var cover = _covers[i];
            var d     = i - _offset;
            var side  = Math.Sign(d);
            var a     = Math.Abs(d);
            var t     = Math.Min(a, 1.0);       // 0 = centered … 1 = first flank pose
            var extra = Math.Max(a - 1.0, 0.0); // positions beyond the first flank

            cover.Translation.OffsetX = side * (FirstFlankX * t + FlankSpacingX * extra);
            cover.Translation.OffsetZ = -(FlankDepthZ * t + FlankRecedeZ * extra);
            cover.Rotation.Angle      = -side * FlankAngleDeg * t;

            var scale = 1 + (CenterScale - 1) * (1 - t);
            cover.Scale.ScaleX = scale;
            cover.Scale.ScaleY = scale;

            cover.Scrim.Opacity         = Math.Min(0.26 * t + 0.11 * extra, 0.88);
            cover.EmissiveBrush.Opacity = 0.24 * (1 - t);
        }

        // Camera dolly: ease back proportionally to transition speed so the whole scene
        // breathes during a jump, with a slight lateral lean into the motion.
        var delta = _target - _offset;
        var dolly = Math.Min(Math.Abs(delta), 2.5) / 2.5 * CameraDollyMax;
        var lean  = Math.Clamp(delta, -1.0, 1.0) * 0.12;
        _camera.Position      = new Point3D(lean, 0.30, CameraBaseZ + dolly);
        _camera.LookDirection = new Vector3D(-lean * 0.04, -0.10, -1);
    }

    // ── Geometry ─────────────────────────────────────────────────────────────

    private static MeshGeometry3D BuildQuad(
        Point3D p0, Point3D p1, Point3D p2, Point3D p3,
        Point uv0, Point uv1, Point uv2, Point uv3)
    {
        var mesh = new MeshGeometry3D
        {
            Positions          = [p0, p1, p2, p3],
            TextureCoordinates = [uv0, uv1, uv2, uv3],
            TriangleIndices    = [0, 1, 2, 0, 2, 3],
        };
        mesh.Freeze();
        return mesh;
    }

    private sealed class Cover
    {
        public required object               Item;
        public required ModelVisual3D        Visual;
        public required AxisAngleRotation3D  Rotation;
        public required TranslateTransform3D Translation;
        public required ScaleTransform3D     Scale;
        public required SolidColorBrush      Scrim;
        public required ImageBrush           EmissiveBrush;
        public required GeometryModel3D      FrontModel;
        public required GeometryModel3D      ReflectionModel;
        public required DiffuseMaterial      FrontDiffuse;
        public required DiffuseMaterial      ReflectionDiffuse;
        public int TexturePixels;
    }

    // ── UI Automation ────────────────────────────────────────────────────────
    // The rendering is 3D, but Narrator users get a plain selectable list: the control
    // is a List whose children are ListItems named after the distros, with selection
    // changes announced via SelectionItemPatternOnElementSelected.

    protected override AutomationPeer OnCreateAutomationPeer() => new CoverFlowAutomationPeer(this);

    private void AnnounceSelection(int index)
    {
        if (index >= 0 && index < _itemPeers.Count
            && AutomationPeer.ListenerExists(AutomationEvents.SelectionItemPatternOnElementSelected))
        {
            _itemPeers[index].RaiseAutomationEvent(AutomationEvents.SelectionItemPatternOnElementSelected);
        }
    }

    private sealed class CoverFlowAutomationPeer : FrameworkElementAutomationPeer,
        System.Windows.Automation.Provider.ISelectionProvider
    {
        private readonly CoverFlow3DControl _owner;

        public CoverFlowAutomationPeer(CoverFlow3DControl owner) : base(owner) => _owner = owner;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.List;
        protected override string GetClassNameCore() => nameof(CoverFlow3DControl);
        protected override List<AutomationPeer> GetChildrenCore() => [.. _owner._itemPeers];

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.Selection ? this : base.GetPattern(patternInterface);

        public bool CanSelectMultiple => false;
        public bool IsSelectionRequired => true;

        public System.Windows.Automation.Provider.IRawElementProviderSimple[] GetSelection()
        {
            var index = _owner.SelectedIndex;
            return index >= 0 && index < _owner._itemPeers.Count
                ? [ProviderFromPeer(_owner._itemPeers[index])]
                : [];
        }
    }

    private sealed class CoverItemAutomationPeer : AutomationPeer,
        System.Windows.Automation.Provider.ISelectionItemProvider
    {
        private readonly CoverFlow3DControl _owner;
        private readonly object _item;
        private readonly int _index;

        public CoverItemAutomationPeer(CoverFlow3DControl owner, object item, int index)
        {
            _owner = owner;
            _item  = item;
            _index = index;
        }

        protected override string GetNameCore() =>
            _owner.ItemNameResolver?.Invoke(_item) ?? _item.ToString() ?? string.Empty;

        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ListItem;
        protected override string GetClassNameCore() => "CoverFlow3DItem";
        protected override string GetAutomationIdCore() => $"CoverFlowItem{_index}";
        protected override List<AutomationPeer> GetChildrenCore() => [];

        public override object? GetPattern(PatternInterface patternInterface) =>
            patternInterface == PatternInterface.SelectionItem ? this : null;

        protected override Rect GetBoundingRectangleCore() =>
            UIElementAutomationPeer.FromElement(_owner)?.GetBoundingRectangle() ?? Rect.Empty;

        protected override Point GetClickablePointCore() => new(double.NaN, double.NaN);
        protected override string GetAcceleratorKeyCore() => string.Empty;
        protected override string GetAccessKeyCore() => string.Empty;
        protected override string GetHelpTextCore() => string.Empty;
        protected override string GetItemStatusCore() => string.Empty;
        protected override string GetItemTypeCore() => string.Empty;
        protected override AutomationPeer? GetLabeledByCore() => null;
        protected override AutomationOrientation GetOrientationCore() => AutomationOrientation.None;
        protected override bool HasKeyboardFocusCore() => _owner.IsKeyboardFocused && IsSelected;
        protected override bool IsContentElementCore() => true;
        protected override bool IsControlElementCore() => true;
        protected override bool IsEnabledCore() => _owner.IsEnabled;
        protected override bool IsKeyboardFocusableCore() => false;
        protected override bool IsOffscreenCore() => false;
        protected override bool IsPasswordCore() => false;
        protected override bool IsRequiredForFormCore() => false;
        protected override void SetFocusCore() => _owner.Focus();

        public bool IsSelected => _owner.SelectedIndex == _index;

        public System.Windows.Automation.Provider.IRawElementProviderSimple? SelectionContainer =>
            UIElementAutomationPeer.FromElement(_owner) is { } peer ? ProviderFromPeer(peer) : null;

        public void Select() => _owner.SelectedIndex = _index;
        public void AddToSelection() => Select();
        public void RemoveFromSelection() => throw new InvalidOperationException("Selection is required.");
    }
}
