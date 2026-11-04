using CommunityToolkit.Mvvm.ComponentModel;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Host;
using Microsoft.Extensions.Logging;

namespace Igloo.Community.App.ViewModels;

public sealed partial class DistroSelectionViewModel : ObservableObject
{
    private readonly IProviderCatalog _loader;
    private readonly IProviderCompatibility _compatibility;
    private readonly ILogger<DistroSelectionViewModel> _logger;

    // Rebuild bookkeeping: RefreshCompatibility only rebuilds the list when one of
    // these actually changed (see the comment there for why that matters).
    private bool _built;
    private bool _lastSecureBootOn;
    private PreflightReport? _lastReport;
    private string _lastCategory = AllCategory;
    private IReadOnlyList<string> _recommendedIds = [];

    //   Observable state                           

    [ObservableProperty]
    private IReadOnlyList<DistroListItem> _distroItems = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedDistro))]
    [NotifyPropertyChangedFor(nameof(CanProceed))]
    private DistroListItem? _selectedItem;

    //   Derived                                

    
    public DistroManifest? SelectedDistro => SelectedItem?.Manifest;

    public bool CanProceed => SelectedItem is { IsCompatible: true, IsComingSoon: false };

    //   Constructor                              

    public DistroSelectionViewModel(IProviderCatalog loader, IProviderCompatibility compatibility,
        ILogger<DistroSelectionViewModel> logger)
    {
        _loader = loader;
        _compatibility = compatibility;
        _logger = logger;
        // Initial population with no preflight data (no constraints yet).
        RefreshCompatibility(null);
    }

    //   API                                  

    
    public const string RecommendedCategory = "Recommended";

    public void SetRecommendation(IReadOnlyList<string> distroIds)
    {
        ArgumentNullException.ThrowIfNull(distroIds);

        if (_recommendedIds.SequenceEqual(distroIds, StringComparer.OrdinalIgnoreCase))
            return;
        _recommendedIds = distroIds;
        _built = false;
        if (distroIds.Count > 0)
            SelectedCategory = RecommendedCategory;   // triggers a rebuild via its change hook
    }

    public const string AllCategory = "All";

    [ObservableProperty]
    private IReadOnlyList<string> _categories = [AllCategory];

    [ObservableProperty]
    private string _selectedCategory = AllCategory;

    partial void OnSelectedCategoryChanged(string value) =>
        RefreshCompatibility(_lastReport);

    public void RefreshCompatibility(PreflightReport? report)
    {
        var secureBootOn = report?.SecureBootEnabled ?? false;
        var category = SelectedCategory ?? AllCategory;

        // Only rebuild the list when something that affects it actually changed.
        // Rebuilding reassigns DistroItems, which makes WPF's bound ListBox reset
        // its SelectedItem to null (the old item is gone from the new list)  so an
        // unconditional rebuild on every visit silently drops the user's selection
        // when they navigate back to this step, NRE-ing downstream (distro.Id).
        // A new preflight report MUST rebuild: the plugins' hardware findings
        // (RAM floors, BitLocker, …) are evaluated against it. A category switch
        // must rebuild too  it changes which distros are visible.
        if (_built && secureBootOn == _lastSecureBootOn && ReferenceEquals(report, _lastReport)
                   && string.Equals(category, _lastCategory, StringComparison.OrdinalIgnoreCase))
            return;

        var previousId = SelectedItem?.Manifest.Id;

        // Categories always reflect the FULL catalog (never the filtered view),
        // so a chip can't disappear while it is active. The Recommended chip
        // leads (right after All) and only exists once the quiz was answered.
        var head = new List<string> { AllCategory };
        if (_recommendedIds.Count > 0)
            head.Add(RecommendedCategory);
        var categories = head
            .Concat(_loader.LoadedDistros
                .Select(m => m.DefaultDesktopEnvironment)
                .Where(de => !string.IsNullOrWhiteSpace(de))
                .Select(de => de!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(de => de, StringComparer.OrdinalIgnoreCase))
            .ToList();
        // Replacing an unchanged ItemsSource makes the chip ListBox discard its
        // keyboard selection while the user is moving between categories.
        if (!Categories.SequenceEqual(categories, StringComparer.OrdinalIgnoreCase))
            Categories = categories;

        DistroItems = _loader.LoadedDistros
            .Where(m => string.Equals(category, AllCategory, StringComparison.OrdinalIgnoreCase)
                     || (string.Equals(category, RecommendedCategory, StringComparison.OrdinalIgnoreCase)
                         && _recommendedIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase))
                     || string.Equals(m.DefaultDesktopEnvironment?.Trim(), category,
                                      StringComparison.OrdinalIgnoreCase))
            .Select(m => EvaluateItem(m, report) with
            {
                IsRecommended = _recommendedIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase),
            })
            .ToList();
        _built = true;
        _lastSecureBootOn = secureBootOn;
        _lastReport = report;
        _lastCategory = category;

        // On a desktop filter, center an installable distribution when one is
        // available. Otherwise a coming-soon cover can sit in the center while
        // the filtered shelf contains a working installer.
        var restored = DistroItems.FirstOrDefault(i => i.Manifest.Id == previousId);
        var recommended = _recommendedIds
            .Select(id => DistroItems.FirstOrDefault(
                i => string.Equals(i.Manifest.Id, id, StringComparison.OrdinalIgnoreCase) &&
                     i is { IsCompatible: true, IsComingSoon: false }))
            .FirstOrDefault(i => i is not null);
        var filteredCompatible = !string.Equals(category, AllCategory,
                                     StringComparison.OrdinalIgnoreCase)
            ? DistroItems.FirstOrDefault(i => i is { IsCompatible: true, IsComingSoon: false })
            : null;
        SelectedItem = restored is { IsCompatible: true, IsComingSoon: false } ? restored
                     : recommended ?? filteredCompatible;
    }

    //   Helpers                                

    private DistroListItem EvaluateItem(DistroManifest m, PreflightReport? report)
    {
        var comingSoon = !m.IsAvailable;

        if (report is not null && !_compatibility.IsLoaded(m.Id))
        {
            return new DistroListItem(m,
                IsCompatible: false,
                IncompatibilityReason: "Choose another distribution. Installer support is unavailable.",
                IsComingSoon: comingSoon);
        }

        // Plugin-declared hardware requirements (BitLocker state, distro-specific
        // RAM floors such as Ubuntu's in-memory installer, …). A Blocker finding
        // makes the distro unselectable, with the reason shown in the catalog.
        // This call is the ONLY place plugin CheckCompatibility is enforced 
        // before it was wired up, a machine below Ubuntu's RAM floor sailed into
        // an install that could only fail after repartitioning had begun.
        if (report is not null)
        {
            try
            {
                var blocker = _compatibility.Evaluate(m.Id, report)
                    .FirstOrDefault(f => f.Severity == FindingSeverity.Blocker);
                if (blocker is not null)
                {
                    var reason = string.IsNullOrWhiteSpace(blocker.Remediation)
                        ? blocker.Message
                        : $"{blocker.Remediation} {blocker.Message}";
                    return new DistroListItem(m, IsCompatible: false,
                        IncompatibilityReason: reason, IsComingSoon: comingSoon);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed compatibility check cannot establish installation support.
                _logger.LogWarning(ex, "CheckCompatibility failed for {DistroId}", m.Id);
                return new DistroListItem(m, IsCompatible: false,
                    IncompatibilityReason: "Run the system check again. Compatibility could not be verified.",
                    IsComingSoon: comingSoon);
            }
        }

        return new DistroListItem(m, IsCompatible: true, IncompatibilityReason: null,
                                  IsComingSoon: comingSoon);
    }

}

public sealed record DistroListItem(
    DistroManifest Manifest,
    bool IsCompatible,
    string? IncompatibilityReason,
    bool IsComingSoon = false,
    bool IsRecommended = false);
