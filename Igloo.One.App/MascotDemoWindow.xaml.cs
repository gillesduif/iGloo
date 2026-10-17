using System.IO;
using System.Diagnostics.CodeAnalysis;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Controls.Primitives;
using Igloo.Mascot.Core;
using Igloo.Mascot.Wpf;

namespace Igloo.One.App;

[SuppressMessage("Maintainability", "CA1515:Consider making public types internal", Justification = "WPF XAML-generated partial class is public.")]
public partial class MascotDemoWindow : Window
{
    private CancellationTokenSource? _demoCancellation;
    public long RenderedFrames => Avatar.RenderedFrames;
    public int CompletedTasks => Ring.Cards.Count(card => card.State == TaskCardState.Done);

    public MascotDemoWindow()
    {
        InitializeComponent();
        Ring.Cards.Add(new() { Title = "Download Fedora KDE", Subtitle = "Get the latest stable ISO" });
        Ring.Cards.Add(new() { Title = "Verify image", Subtitle = "Check integrity (checksum)" });
        Ring.Cards.Add(new() { Title = "Prepare external SSD", Subtitle = "Partition and make it bootable" });
        Ring.Cards.Add(new() { Title = "Migrate Firefox data", Subtitle = "Transfer profile and settings" });
        Ring.WaitingChanged += (_, _) => PulseInput();
        InputBox.SizeChanged += (_, _) => SetInputGaze();
        LocationChanged += (_, _) => SetInputGaze();
        SizeChanged += (_, _) => SetInputGaze();
        Loaded += (_, _) => SetInputGaze();
        Closed += (_, _) => _demoCancellation?.Cancel();
        BuildControls();
    }

    private void SetInputGaze()
    {
        if (InputBox.IsLoaded)
            Ring.SetGazeTarget(InputBox.PointToScreen(new Point(InputBox.ActualWidth / 2, InputBox.ActualHeight / 2)));
    }

    private void PulseInput()
    {
        InputBox.BorderBrush = Ring.WaitingForUser ? Brushes.DeepSkyBlue : Brushes.Gray;
        if (!Ring.WaitingForUser) { InputBox.BeginAnimation(OpacityProperty, null); InputBox.Opacity = 1; return; }
        InputBox.BeginAnimation(OpacityProperty, new DoubleAnimation(.55, 1, TimeSpan.FromSeconds(.7))
        {
            AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever
        });
    }

    private void BuildControls()
    {
        Controls.Children.Add(new TextBlock { Text = "Tux animation lab", FontSize = 22,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        var actions = new WrapPanel { Margin = new Thickness(0, 0, 0, 12) };
        AddButton(actions, "Wave", () => Avatar.Trigger(MascotAction.Wave));
        AddButton(actions, "Point", () => Avatar.Trigger(MascotAction.Point));
        AddButton(actions, "Think", () => Avatar.Trigger(MascotAction.Think));
        AddButton(actions, "Celebrate", () => Avatar.Trigger(MascotAction.Celebrate));
        AddButton(actions, "Talk", () => Avatar.Talk());
        Controls.Children.Add(actions);
        AddCheck("Follow cursor", nameof(TuxAvatar.FollowCursor));
        AddCheck("Show pivots", nameof(TuxAvatar.ShowPivots));
        var darkCards = new CheckBox { Content = "Dark card theme", Margin = new Thickness(0, 2, 0, 2) };
        darkCards.SetBinding(ToggleButton.IsCheckedProperty, new Binding(nameof(TaskCardRing.DarkTheme))
        {
            Source = Ring, Mode = BindingMode.TwoWay
        });
        Controls.Children.Add(darkCards);
        AddHeading("Left wing in view · Tux’s right hand");
        AddSlider("Shoulder", nameof(TuxAvatar.LeftShoulder), -20, 160);
        AddSlider("Arm", nameof(TuxAvatar.LeftArm), -90, 90);
        AddSlider("Hand", nameof(TuxAvatar.LeftHand), -70, 70);
        AddHeading("Right wing in view");
        AddSlider("Shoulder", nameof(TuxAvatar.RightShoulder), -20, 160);
        AddSlider("Arm", nameof(TuxAvatar.RightArm), -90, 90);
        AddSlider("Hand", nameof(TuxAvatar.RightHand), -70, 70);
        AddHeading("Face and rig");
        AddSlider("Head tilt", nameof(TuxAvatar.HeadTilt), -15, 15);
        AddSlider("Head turn", nameof(TuxAvatar.HeadTurn), -8, 8);
        AddSlider("Mouth", nameof(TuxAvatar.MouthOpen), 0, 1);
        AddSlider("Eyes closed", nameof(TuxAvatar.EyesClosed), 0, 1);
        AddSlider("Elbow position", nameof(TuxAvatar.ElbowPosition), .15, .6);
        AddSlider("Wrist position", nameof(TuxAvatar.WristPosition), .55, .95);
        AddSlider("Blend softness", nameof(TuxAvatar.BlendSoftness), .04, .3);
        AddHeading("Four-task flow");
        var tasks = new WrapPanel();
        AddButton(tasks, "Run demo", () => _ = RunDemoAsync());
        AddButton(tasks, "Start next", StartNext);
        AddButton(tasks, "Complete", () => Ring.CompleteTask());
        AddButton(tasks, "Wait / resume", () => { SetInputGaze(); Ring.SetWaiting(!Ring.WaitingForUser); });
        AddButton(tasks, "Reset", Reset);
        AddButton(tasks, "Capture poses", () => CaptureAll(Path.Combine(AppContext.BaseDirectory, "mascot-captures")));
        Controls.Children.Add(tasks);
    }

    private void AddHeading(string text) => Controls.Children.Add(new TextBlock
    {
        Text = text, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4)
    });

    private static void AddButton(Panel parent, string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(9, 5, 9, 5),
            Margin = new Thickness(0, 0, 6, 6) };
        button.Click += (_, _) => action();
        parent.Children.Add(button);
    }

    private void AddCheck(string label, string property)
    {
        var check = new CheckBox { Content = label, Margin = new Thickness(0, 2, 0, 2) };
        check.SetBinding(ToggleButton.IsCheckedProperty, new Binding(property)
        {
            Source = Avatar, Mode = BindingMode.TwoWay
        });
        Controls.Children.Add(check);
    }

    private void AddSlider(string label, string property, double minimum, double maximum)
    {
        var row = new Grid { Margin = new Thickness(0, 1, 0, 2) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(105) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
        row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center });
        var slider = new Slider { Minimum = minimum, Maximum = maximum,
            SmallChange = maximum - minimum > 10 ? 1 : .01, TickFrequency = maximum - minimum > 10 ? 1 : .01 };
        slider.SetBinding(RangeBase.ValueProperty, new Binding(property) { Source = Avatar, Mode = BindingMode.TwoWay });
        Grid.SetColumn(slider, 1); row.Children.Add(slider);
        var value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        value.SetBinding(TextBlock.TextProperty, new Binding(property)
        {
            Source = Avatar, StringFormat = "{0:0.##}"
        });
        Grid.SetColumn(value, 2); row.Children.Add(value);
        Controls.Children.Add(row);
    }

    private void StartNext()
    {
        for (int i = 0; i < Ring.Cards.Count; i++)
            if (Ring.Cards[i].State == TaskCardState.Todo) { Ring.StartTask(i); return; }
    }

    private void Reset()
    {
        _demoCancellation?.Cancel();
        Ring.SetWaiting(false);
        Ring.CompleteTask();
        foreach (TaskCardViewModel card in Ring.Cards) { card.Progress = 0; card.State = TaskCardState.Todo; }
    }

    public async Task RunDemoAsync()
    {
        Reset();
        using var cancellation = new CancellationTokenSource();
        _demoCancellation = cancellation;
        CancellationToken token = cancellation.Token;
        try
        {
            await Task.Delay(700, token).ConfigureAwait(true);
            double[] durations = [5, 3.5, 4.5, 4.5];
            for (int i = 0; i < Ring.Cards.Count; i++)
            {
                Ring.StartTask(i, durations[i]);
                if (i == 2)
                {
                    await Task.Delay(1600, token).ConfigureAwait(true);
                    SetInputGaze(); Ring.SetWaiting(true);
                    await Task.Delay(3200, token).ConfigureAwait(true);
                    Ring.SetWaiting(false);
                }
                while (Ring.Cards[i].State == TaskCardState.Busy) await Task.Delay(100, token).ConfigureAwait(true);
                await Task.Delay(1000, token).ConfigureAwait(true);
            }
            Avatar.Trigger(MascotAction.Celebrate);
        }
        catch (TaskCanceledException) { }
        finally { if (ReferenceEquals(_demoCancellation, cancellation)) _demoCancellation = null; }
    }

    public void CaptureAll(string directory)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "coordinates.txt"), string.Empty);
        Ring.UpdateLayout();
        Capture(directory, "01-rest", new(0, 0, 0), new(0, 0, 0));
        Capture(directory, "02-shoulders-100", new(100, 0, 0), new(100, 0, 0));
        Capture(directory, "03-both-45-25-15", new(45, 25, 15), new(45, 25, 15));
        Capture(directory, "04-both-60-50-30", new(60, 50, 30), new(60, 50, 30));
        Ring.StartTask(0);
        Capture(directory, "05-hold", new(32, 80, 18), new(20, -70, -15));
        Ring.CompleteTask();
        Ring.Cards[0].State = TaskCardState.Todo;
        Ring.Cards[0].Progress = 0;
        Capture(directory, "06-fold", new(110, -70, -40), new(110, -70, -40));
        Capture(directory, "07-blink", new(0, 0, 0), new(0, 0, 0), blink: 1);
        Capture(directory, "08-mouth", new(0, 0, 0), new(0, 0, 0), mouth: 1);
        Capture(directory, "09-head-minus-6", new(0, 0, 0), new(0, 0, 0), headTilt: -6);
        Capture(directory, "10-head-plus-6", new(0, 0, 0), new(0, 0, 0), headTilt: 6);
        Ring.StartTask(2);
        SetInputGaze(); Ring.SetWaiting(true);
        Avatar.RenderExactPose(new(32, 80, 18), new(20, -70, -15),
            headTilt: -7, headTurn: -3.5, eyeX: -2.6, eyeY: 1.8, waiting: true);
        Ring.ArrangeForSnapshot();
        Stage.UpdateLayout();
        SavePng(Stage, Path.Combine(directory, "11-waiting-for-user.png"));
        Ring.SetWaiting(false); Ring.CompleteTask();
        Ring.DarkTheme = false;
        Avatar.RenderExactPose(new(0, 0, 0), new(0, 0, 0));
        Ring.ArrangeForSnapshot(); Stage.UpdateLayout();
        SavePng(Stage, Path.Combine(directory, "12-light-cards.png"));
        Ring.DarkTheme = true;
        Width = MinWidth;
        UpdateLayout();
        Ring.StartTask(0);
        Avatar.RenderExactPose(new(32, 80, 18), new(20, -70, -15));
        Ring.ArrangeForSnapshot(); Stage.UpdateLayout();
        SavePng(Stage, Path.Combine(directory, "13-narrow-hold.png"));
        Ring.CompleteTask();
    }

    private void Capture(string directory, string name, WingAngles left, WingAngles right,
        double headTilt = 0, double mouth = 0, double blink = 0)
    {
        Avatar.RenderExactPose(left, right, headTilt, mouth: mouth, blink: blink);
        Ring.UpdateLayout();
        Ring.ArrangeForSnapshot();
        Ring.UpdateLayout();
        Point hand = Avatar.HandTipIn(Ring);
        File.AppendAllText(Path.Combine(directory, "coordinates.txt"), $"{name}: hand=({hand.X:F1},{hand.Y:F1}) card={Ring.HeldCardCentreInRing} ring=({Ring.ActualWidth:F1},{Ring.ActualHeight:F1}) avatar={Avatar.TranslatePoint(default, Ring)}{Environment.NewLine}");
        SavePng(Ring, Path.Combine(directory, name + ".png"));
    }

    private static void SavePng(FrameworkElement visual, string path)
    {
        DpiScale dpi = VisualTreeHelper.GetDpi(visual);
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth * dpi.DpiScaleX),
            (int)Math.Ceiling(visual.ActualHeight * dpi.DpiScaleY), dpi.PixelsPerInchX, dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        var brush = new VisualBrush(visual)
        {
            ViewboxUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewport = new Rect(0, 0, visual.ActualWidth, visual.ActualHeight),
            Stretch = Stretch.None
        };
        using (DrawingContext context = drawing.RenderOpen())
            context.DrawRectangle(brush, null, new Rect(0, 0, visual.ActualWidth, visual.ActualHeight));
        bitmap.Render(drawing);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream output = File.Create(path);
        encoder.Save(output);
    }
}
