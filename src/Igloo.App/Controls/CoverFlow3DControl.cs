using System.Collections;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Threading;

namespace Igloo.App.Controls;

public sealed class CoverFlow3DControl : FrameworkElement
{
    //   Scene constants                            

    private const double FlankAngleDeg = 56;         // Y rotation of side covers
    private const double FirstFlankX = 2.05;        // gap between center and first flank
    private const double FlankSpacingX = 0.62;     // spacing between subsequent flanks
    private const double FlankDepthZ = 1.30;      // how far the first flank drops back
    private const double FlankRecedeZ = 0.34;    // additional depth per further flank
    private const double CenterScale = 1.16;    // the centered cover is slightly larger
    private const double CameraBaseZ = 6.1;
    private const double CameraDollyMax = 0.85;  // dolly-out at high transition speed
    private const double EaseRate = 9.0;        // exponential ease-out (~330 ms to settle)

    private const int FullResPixels = 512;      // texture size near center
    private const int LowResPixels = 144;      // texture size beyond the window
    private const int FullResWindow = 4;      // ± positions that get full-res textures


  
    private static readonly MeshGeometry3D FrontMesh = BuildQuad(
        new Point3D(-1, -1, 0), new Point3D(1, -1, 0), new Point3D(1, 1, 0), new Point3D(-1, 1, 0),
        new Point(0, 1), new Point(1, 1), new Point(1, 0), new Point(0, 0));


    private static readonly MeshGeometry3D ReflectionMesh = BuildQuad(
        new Point3D(-1, -2.92, 0), new Point3D(1, -2.92, 0), new Point3D(1, -0.92, 0), new Point3D(-1, -0.92, 0),
        new Point(0, 0), new Point(1, 0), new Point(1, 1), new Point(0, 1));

    //   Visual tree                              

    private readonly Grid _root;
    private readonly Border _focusRing;
    private readonly Viewport3D _viewport;
    private readonly PerspectiveCamera _camera;
    private readonly ModelVisual3D _lightsVisual;

    //   Scene/animation state       

    private readonly List<Cover> _covers = [];
    private readonly Dictionary<Model3D, int> _modelToIndex = [];
    private readonly List<CoverItemAutomationPeer> _itemPeers = [];
    private List<object> _items = [];
    private double _offset;        
    private double _target;
    private bool _animating;
    private bool _syncingSelection;
    private TimeSpan _lastRenderTime;

    public CoverFlow3DControl()
    {
        Focusable = true;
        FocusVisualStyle = null; 

        _camera = new PerspectiveCamera
        {
            FieldOfView = 62,
            UpDirection = new Vector3D(0, 1, 0),
            Position = new Point3D(0, 0.30, CameraBaseZ),
            LookDirection = new Vector3D(0, -0.10, -1),
        };

     
        var lights = new Model3DGroup();
        lights.Children.Add(new AmbientLight(Color.FromRgb(0x5C, 0x5E, 0x6A)));
        lights.Children.Add(new DirectionalLight(Color.FromRgb(0xF2, 0xF2, 0xFA), new Vector3D(-0.06, -0.22, -1)));
        lights.Freeze();
        _lightsVisual = new ModelVisual3D { Content = lights };

        _viewport = new Viewport3D { Camera = _camera, ClipToBounds = true };
        _viewport.Children.Add(_lightsVisual);

        _focusRing = new Border
        {
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(12),
            BorderBrush = Brushes.Transparent,
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

    //   Public surface                            ─

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

    public Func<object, int, ImageSource?>? CoverImageResolver { get; set; }

    
    public Func<object, string>? ItemNameResolver { get; set; }

    //   FrameworkElement plumbing                       

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
                        

    private void OnItemsSourceChanged()
    {
        _items = ItemsSource?.Cast<object>().ToList() ?? [];

        var index = SelectedItem is { } current ? _items.IndexOf(current) : -1;
        if (index < 0 && _items.Count > 0)
            index = _items.Count / 2;

        _syncingSelection = true;
        try
        {
            SelectedIndex = index;
            SelectedItem = index >= 0 ? _items[index] : null;
        }
        finally
        {
            _syncingSelection = false;
        }

        BuildScene();
        _lastDepthCenter = int.MinValue;   // force a depth re-sort for the new scene

        // New scene: snap into place, no cross-refresh animation.
        _target = _offset = Math.Max(index, 0);
        LayoutScene();
        ScheduleTextureRefresh();
    }

    private void OnSelectedItemChanged(object? newValue)
    {
        if (_syncingSelection)
            return;

        if (newValue is null)
        {
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
        if (_syncingSelection)
            return;

        var index = Math.Clamp(SelectedIndex, _items.Count == 0 ? -1 : 0, _items.Count - 1);
        _syncingSelection = true;
        try
        {
            SelectedIndex = index;
            SelectedItem = index >= 0 ? _items[index] : null;
        }
        finally
        {
            _syncingSelection = false;
        }

        if (index < 0)
            return;

        _target = index;
        StartAnimation();
        ScheduleTextureRefresh();
        AnnounceSelection(index);
    }

    // Input                                 

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnKeyDown(e);
        if (_items.Count == 0)
            return;

        switch (e.Key)
        {
            case Key.Left:
                SelectedIndex = Math.Max(0, SelectedIndex - 1);
                e.Handled = true;
                break;
            case Key.Right:
                SelectedIndex = Math.Min(_items.Count - 1, SelectedIndex + 1);
                e.Handled = true;
                break;
            case Key.Home:
                SelectedIndex = 0;
                e.Handled = true;
                break;
            case Key.End:
                SelectedIndex = _items.Count - 1;
                e.Handled = true;
                break;
            case Key.Enter:
            case Key.Space:
                Confirm();
                e.Handled = true;
                break;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseWheel(e);
        if (_items.Count == 0)
            return;
        var step = e.Delta < 0 ? 1 : -1;
        SelectedIndex = Math.Clamp(SelectedIndex + step, 0, _items.Count - 1);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);
        base.OnMouseLeftButtonDown(e);
        Focus();

        // 3D hit test: ray through the clicked pixel against the cover meshes.
        var hit = VisualTreeHelper.HitTest(_viewport, e.GetPosition(_viewport));
        if (hit is RayMeshGeometry3DHitTestResult meshHit
            && _modelToIndex.TryGetValue(meshHit.ModelHit, out var index))
        {
            if (index == SelectedIndex)
                Confirm();  
            else
                SelectedIndex = index;             
            e.Handled = true;
        }
    }

    private void Confirm()
    {
        if (!CanConfirm)
            return;
        if (ConfirmCommand is { } command && command.CanExecute(null))
            command.Execute(null);
    }

    private void UpdateFocusRing() =>
        _focusRing.BorderBrush = Brushes.Transparent;

    // Scene construction                         

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
            _modelToIndex[cover.FrontModel] = i;
            _modelToIndex[cover.ReflectionModel] = i;
            _itemPeers.Add(new CoverItemAutomationPeer(this, _items[i], i));
        }

        if (UIElementAutomationPeer.FromElement(this) is CoverFlowAutomationPeer peer)
            peer.ResetChildrenCache();
    }

    private static Cover CreateCover(object item)
    {
        var emissiveBrush = new ImageBrush { Opacity = 0 };

        var frontDiffuse = new DiffuseMaterial();
        var frontMaterial = new MaterialGroup();
        frontMaterial.Children.Add(frontDiffuse);
        frontMaterial.Children.Add(new EmissiveMaterial(emissiveBrush));

        var reflectionDiffuse = new DiffuseMaterial();
        var reflectionMaterial = new MaterialGroup();
        reflectionMaterial.Children.Add(reflectionDiffuse);

        var frontModel = new GeometryModel3D(FrontMesh, frontMaterial);
        var reflectionModel = new GeometryModel3D(ReflectionMesh, reflectionMaterial);

        var rotation = new AxisAngleRotation3D(new Vector3D(0, 1, 0), 0);
        // Constant lift: raises the whole ensemble (cover + reflection) in the
        // frame, closing the dead space between the category chips and the art.
        var translation = new TranslateTransform3D { OffsetY = 0.42 };
        var scale = new ScaleTransform3D(1, 1, 1);
        var transform = new Transform3DGroup();
        transform.Children.Add(scale);
        transform.Children.Add(new RotateTransform3D(rotation));
        transform.Children.Add(translation);

        var group = new Model3DGroup { Transform = transform };
        group.Children.Add(frontModel);
        group.Children.Add(reflectionModel);

        return new Cover
        {
            Item = item,
            Visual = new ModelVisual3D { Content = group },
            Rotation = rotation,
            Translation = translation,
            Scale = scale,
            EmissiveBrush = emissiveBrush,
            FrontModel = frontModel,
            ReflectionModel = reflectionModel,
            FrontDiffuse = frontDiffuse,
            ReflectionDiffuse = reflectionDiffuse,
        };
    }

    //   Texture management                          ─

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

        Brush front = source is not null ? new ImageBrush(source) : FallbackFrontBrush.Clone();
        var reflection = BuildReflectionBrush(source);

        cover.FrontBrush = front;
        cover.ReflectionBrush = reflection;
        cover.FrontDiffuse.Brush = front;
        cover.ReflectionDiffuse.Brush = reflection;
        cover.EmissiveBrush.ImageSource = source;
        cover.TexturePixels = pixels;
        ApplyFog(cover);   // re-apply the cover's current fog to the fresh brushes
    }


    private static void ApplyFog(Cover cover)
    {
        var visibility = 1 - cover.Fog;
        if (!cover.FrontBrush.IsFrozen)
            cover.FrontBrush.Opacity = visibility;
        if (!cover.ReflectionBrush.IsFrozen)
            cover.ReflectionBrush.Opacity = visibility;
    }

    private static readonly Brush FallbackFrontBrush = CreateFallbackFrontBrush();

    private static LinearGradientBrush CreateFallbackFrontBrush()
    {
        var brush = new LinearGradientBrush(Color.FromRgb(0x2A, 0x31, 0x47), Color.FromRgb(0x14, 0x18, 0x26), 90);
        brush.Freeze();
        return brush;
    }

    private static ImageBrush BuildReflectionBrush(ImageSource? source)
    {
        var bounds = new Rect(0, 0, 1, 1);
        var group = new DrawingGroup();

        if (source is not null)
            group.Children.Add(new ImageDrawing(source, bounds));
        else
            group.Children.Add(new GeometryDrawing(FallbackFrontBrush, null, new RectangleGeometry(bounds)));


        group.OpacityMask = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.0),
                new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.45),
                new GradientStop(Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF), 1.0),
            },
        };

        const int bake = 256;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawDrawing(new DrawingGroup
            {
                Children = { group },
                Transform = new ScaleTransform(bake, bake),
            });
        var baked = new RenderTargetBitmap(bake, bake, 96, 96, PixelFormats.Pbgra32);
        baked.Render(visual);
        baked.Freeze();

        // Unfrozen ImageBrush: the render loop fades Opacity per frame (depth fog).
        return new ImageBrush(baked) { Stretch = Stretch.Fill };
    }

    // Animation                               

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

        if (_animating)
            return;
        _animating = true;
        _lastRenderTime = TimeSpan.Zero;
        CompositionTarget.Rendering += OnRendering;
    }

    private void StopAnimation()
    {
        if (!_animating)
            return;
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
        if (dt <= 0)
            return;

        _offset += (_target - _offset) * (1 - Math.Exp(-EaseRate * dt));

        if (Math.Abs(_target - _offset) < 0.002)
        {
            _offset = _target;
            StopAnimation();
        }

        LayoutScene();
    }

    private int _lastDepthCenter = int.MinValue;

    private void ReorderForDepth(int center)
    {
        _lastDepthCenter = center;

        _viewport.Children.Clear();
        _viewport.Children.Add(_lightsVisual);
        foreach (var i in Enumerable.Range(0, _covers.Count)
                     .OrderByDescending(i => Math.Abs(i - center))
                     .ThenBy(i => i))
            _viewport.Children.Add(_covers[i].Visual);
    }

    
    private void LayoutScene()
    {
        var depthCenter = (int)Math.Round(_offset);
        if (depthCenter != _lastDepthCenter)
            ReorderForDepth(depthCenter);

        for (var i = 0; i < _covers.Count; i++)
        {
            var cover = _covers[i];
            var d = i - _offset;
            var side = Math.Sign(d);
            var a = Math.Abs(d);
            var t = Math.Min(a, 1.0);       // 0 = centered … 1 = first flank pose
            var extra = Math.Max(a - 1.0, 0.0); // positions beyond the first flank

            cover.Translation.OffsetX = side * (FirstFlankX * t + FlankSpacingX * extra);
            cover.Translation.OffsetZ = -(FlankDepthZ * t + FlankRecedeZ * extra);
            cover.Rotation.Angle = -side * FlankAngleDeg * t;

            var scale = 1 + (CenterScale - 1) * (1 - t);
            cover.Scale.ScaleX = scale;
            cover.Scale.ScaleY = scale;

            // Only touch brushes whose fog actually changed: a static scene then
            // costs zero brush invalidations per frame.
            var fog = Math.Min(0.26 * t + 0.11 * extra, 0.88);
            if (Math.Abs(fog - cover.Fog) > 0.002)
            {
                cover.Fog = fog;
                ApplyFog(cover);
            }
            var emissive = 0.24 * (1 - t);
            if (Math.Abs(emissive - cover.EmissiveBrush.Opacity) > 0.002)
                cover.EmissiveBrush.Opacity = emissive;
        }

        // Camera dolly: ease back proportionally to transition speed so the whole scene
        // breathes during a jump, with a slight lateral lean into the motion.
        var delta = _target - _offset;
        var dolly = Math.Min(Math.Abs(delta), 2.5) / 2.5 * CameraDollyMax;
        var lean = Math.Clamp(delta, -1.0, 1.0) * 0.12;
        _camera.Position = new Point3D(lean, 0.30, CameraBaseZ + dolly);
        _camera.LookDirection = new Vector3D(-lean * 0.04, -0.10, -1);
    }

    //   Geometry                               ─

    private static MeshGeometry3D BuildQuad(
        Point3D p0, Point3D p1, Point3D p2, Point3D p3,
        Point uv0, Point uv1, Point uv2, Point uv3)
    {
        var mesh = new MeshGeometry3D
        {
            Positions = [p0, p1, p2, p3],
            TextureCoordinates = [uv0, uv1, uv2, uv3],
            TriangleIndices = [0, 1, 2, 0, 2, 3],
        };
        mesh.Freeze();
        return mesh;
    }

    private sealed class Cover
    {
        public required object Item;
        public required ModelVisual3D Visual;
        public required AxisAngleRotation3D Rotation;
        public required TranslateTransform3D Translation;
        public required ScaleTransform3D Scale;
        public required ImageBrush EmissiveBrush;
        public required GeometryModel3D FrontModel;
        public required GeometryModel3D ReflectionModel;
        public required DiffuseMaterial FrontDiffuse;
        public required DiffuseMaterial ReflectionDiffuse;
        public int TexturePixels;

        /* Depth-fog state: 0 = centered/fully visible … 0.88 = far flank. Applied
        by fading FrontBrush/ReflectionBrush opacity (never an overlay layer).*/

        public double Fog;
        public Brush FrontBrush = Brushes.Transparent;
        public Brush ReflectionBrush = Brushes.Transparent;
    }

    //   UI Automation                   

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
            _item = item;
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
