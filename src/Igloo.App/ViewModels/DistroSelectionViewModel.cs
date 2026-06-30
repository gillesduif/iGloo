using CommunityToolkit.Mvvm.ComponentModel;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Plugins;

namespace Igloo.App.ViewModels;

/// <summary>
/// View-model for the distribution selection step.
///
/// Wraps each <see cref="DistroManifest"/> in a <see cref="DistroListItem"/> that carries
/// a pre-computed compatibility flag. Compatibility is (re-)evaluated every time the user
/// navigates to this step via <see cref="RefreshCompatibility"/>, which receives the latest
/// <see cref="PreflightReport"/> from <c>MainWindowViewModel</c>.
///
/// Current compatibility rules
/// ───────────────────────────
/// • Secure Boot ON  → distro must declare the <c>secure-boot-supported</c> tag.
/// </summary>
public sealed partial class DistroSelectionViewModel : ObservableObject
{
    private readonly DistroLoader _loader;

    // ── Observable state ────────────────────────────────────────────────────

    [ObservableProperty]
    private IReadOnlyList<DistroListItem> _distroItems = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDistro))]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private DistroListItem? _selectedItem;

    // ── Derived ──────────────────────────────────────────────────────────────

    /// <summary>The manifest of the currently selected (and compatible) distro; null otherwise.</summary>
    public DistroManifest? SelectedDistro => SelectedItem?.Manifest;

    /// <summary>
    /// True when the user has selected a compatible, installable distro - enables "Next".
    /// Coming-soon entries (no IDistroPlugin yet) can be browsed but never installed.
    /// </summary>
    public bool CanProceed => SelectedItem is { IsCompatible: true, IsComingSoon: false };

    // ── Constructor ──────────────────────────────────────────────────────────

    public DistroSelectionViewModel(DistroLoader loader)
    {
        _loader = loader;
        // Initial population with no preflight data (no constraints yet).
        RefreshCompatibility(null);
    }

    // ── API ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// (Re-)evaluates distro compatibility against <paramref name="report"/> and rebuilds
    /// <see cref="DistroItems"/>. Called by <c>MainWindowViewModel</c> every time the user
    /// navigates to this step so the list always reflects the current hardware state.
    /// </summary>
    private bool  _built;
    private bool  _lastSecureBootOn;

    public void RefreshCompatibility(PreflightReport? report)
    {
        var secureBootOn = report?.SecureBootEnabled ?? false;

        // Only rebuild the list when something that affects it actually changed.
        // Rebuilding reassigns DistroItems, which makes WPF's bound ListBox reset
        // its SelectedItem to null (the old item is gone from the new list) — so an
        // unconditional rebuild on every visit silently drops the user's selection
        // when they navigate back to this step, NRE-ing downstream (distro.Id).
        if (_built && secureBootOn == _lastSecureBootOn)
            return;

        var previousId = SelectedItem?.Manifest.Id;

        DistroItems = _loader.LoadedDistros
            .Select(m => EvaluateItem(m, secureBootOn))
            .ToList();
        _built            = true;
        _lastSecureBootOn = secureBootOn;

        // Restore the previous selection if it is still compatible.
        var restored = DistroItems.FirstOrDefault(i => i.Manifest.Id == previousId);
        SelectedItem = restored is { IsCompatible: true } ? restored : null;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static DistroListItem EvaluateItem(DistroManifest m, bool secureBootOn)
    {
        var comingSoon = !m.IsAvailable;

        if (secureBootOn && !HasSecureBootTag(m))
        {
            return new DistroListItem(m,
                IsCompatible:          false,
                IncompatibilityReason: "Requires Secure Boot to be disabled",
                IsComingSoon:          comingSoon);
        }

        return new DistroListItem(m, IsCompatible: true, IncompatibilityReason: null,
                                  IsComingSoon: comingSoon);
    }

    private static bool HasSecureBootTag(DistroManifest m)
        => m.Tags.Any(t => string.Equals(t, "secure-boot-supported", StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// A <see cref="DistroManifest"/> decorated with compatibility info for the current machine.
/// </summary>
public sealed record DistroListItem(
    DistroManifest Manifest,
    bool           IsCompatible,
    string?        IncompatibilityReason,
    bool           IsComingSoon = false);
