using System.Windows.Controls;
using Igloo.App.Controls;
using Igloo.App.ViewModels;

namespace Igloo.App.Views;

public sealed partial class DistroSelectionPage : UserControl
{
    public DistroSelectionPage()
    {
        InitializeComponent();

        // View-layer wiring only: how a DistroListItem becomes a cover texture and an
        // accessible name. Selection/compatibility logic stays in the view-model.
        var textures = new CoverTextureFactory();

        CoverFlow.CoverImageResolver = (item, pixels) =>
            item is DistroListItem distro
                ? textures.GetCover(distro.Manifest.Id, distro.Manifest.LogoAbsolutePath,
                                    distro.Manifest.DisplayName, pixels)
                : null;

        CoverFlow.ItemNameResolver = item =>
            (item as DistroListItem)?.Manifest.DisplayName ?? item.ToString() ?? string.Empty;

        // Make ← / → / Enter work the moment the step appears.
        Loaded += (_, _) => CoverFlow.Focus();
    }
}
