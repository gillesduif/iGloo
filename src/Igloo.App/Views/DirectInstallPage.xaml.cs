using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace Igloo.App.Views;

public sealed partial class DirectInstallPage
{
    // Captions are the sentences from the numbered list this replaces, unchanged.
    private static readonly (string Image, string Caption)[] Journey =
    [
        ("01-restart.png",   "Windows restarts into the Linux installer."),
        ("02-installing.png", "Linux installs automatically into the allocated space alongside Windows."),
        ("03-firstboot.png", "The migration agent restores your folders, browser profiles and applications."),
        ("04-bootmenu.png",  "The system restarts into a boot menu for operating system selection."),
    ];

    private readonly List<(BitmapImage? Image, string Caption)> _slides = [];
    private int _slide;

    public DirectInstallPage()
    {
        InitializeComponent();

        foreach (var (file, caption) in Journey)
            _slides.Add((TryLoad(file), caption));

        Focusable = true;
        ShowSlide(0);
    }

    /// <summary>Returns null when the asset is not in the build, so a missing
    /// screenshot leaves the caption readable instead of throwing at load.</summary>
    private static BitmapImage? TryLoad(string file)
    {
        try
        {
            return new BitmapImage(new Uri($"pack://application:,,,/Assets/Journey/{file}"));
        }
        catch (IOException)
        {
            return null;
        }
    }

    private void ShowSlide(int index)
    {
        _slide = Math.Clamp(index, 0, _slides.Count - 1);
        var (image, caption) = _slides[_slide];

        SlideImage.Source = image;
        SlideCaption.Text = caption;
        PrevSlide.IsEnabled = _slide > 0;
        NextSlide.IsEnabled = _slide < _slides.Count - 1;
    }

    private void PrevSlide_Click(object sender, RoutedEventArgs e) => ShowSlide(_slide - 1);

    private void NextSlide_Click(object sender, RoutedEventArgs e) => ShowSlide(_slide + 1);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        ArgumentNullException.ThrowIfNull(e);

        switch (e.Key)
        {
            case Key.Left:
                ShowSlide(_slide - 1);
                e.Handled = true;
                break;
            case Key.Right:
                ShowSlide(_slide + 1);
                e.Handled = true;
                break;
            default:
                base.OnKeyDown(e);
                break;
        }
    }
}
