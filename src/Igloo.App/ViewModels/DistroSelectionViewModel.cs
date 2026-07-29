using CommunityToolkit.Mvvm.ComponentModel;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Plugins;
using Microsoft.Extensions.Logging;

namespace Igloo.App.ViewModels;

public sealed partial class DistroSelectionViewModel : ObservableObject
{
    private readonly DistroLoader _loader;
    private readonly DistroRegistry _registry;
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

    public DistroSelectionViewModel(DistroLoader loader, DistroRegistry registry,
        ILogger<DistroSelectionViewModel> logger)
    {
        _loader = loader;
        _registry = registry;
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
        Categories = head
            .Concat(_loader.LoadedDistros
                .Select(m => m.DefaultDesktopEnvironment)
                .Where(de => !string.IsNullOrWhiteSpace(de))
                .Select(de => de!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(de => de, StringComparer.OrdinalIgnoreCase))
            .ToList();

        DistroItems = _loader.LoadedDistros
            .Where(m => string.Equals(category, AllCategory, StringComparison.OrdinalIgnoreCase)
                     || (string.Equals(category, RecommendedCategory, StringComparison.OrdinalIgnoreCase)
                         && _recommendedIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase))
                     || string.Equals(m.DefaultDesktopEnvironment?.Trim(), category,
                                      StringComparison.OrdinalIgnoreCase))
            .Select(m => EvaluateItem(m, secureBootOn, report) with
            {
                IsRecommended = _recommendedIds.Contains(m.Id, StringComparer.OrdinalIgnoreCase),
            })
            .ToList();
        _built = true;
        _lastSecureBootOn = secureBootOn;
        _lastReport = report;
        _lastCategory = category;

        // Selection preference: the user's previous pick, else the TOP-ranked
        // recommendation (the carousel opens centered on the selection), else
        // null (the carousel centers the middle of the shelf).
        var restored = DistroItems.FirstOrDefault(i => i.Manifest.Id == previousId);
        var recommended = _recommendedIds
            .Select(id => DistroItems.FirstOrDefault(
                i => string.Equals(i.Manifest.Id, id, StringComparison.OrdinalIgnoreCase)))
            .FirstOrDefault(i => i is not null);
        SelectedItem = restored is { IsCompatible: true } ? restored
                     : recommended is { IsCompatible: true } ? recommended
                     : null;
    }

    //   Helpers                                

    private DistroListItem EvaluateItem(DistroManifest m, bool secureBootOn, PreflightReport? report)
    {
        var comingSoon = !m.IsAvailable;

        if (secureBootOn && !HasSecureBootTag(m))
        {
            return new DistroListItem(m,
                IsCompatible: false,
                IncompatibilityReason: "Requires Secure Boot to be disabled",
                IsComingSoon: comingSoon);
        }

        // Plugin-declared hardware requirements (BitLocker state, distro-specific
        // RAM floors such as Ubuntu's in-memory installer, …). A Blocker finding
        // makes the distro unselectable, with the reason shown in the catalog.
        // This call is the ONLY place plugin CheckCompatibility is enforced 
        // before it was wired up, a machine below Ubuntu's RAM floor sailed into
        // an install that could only fail after repartitioning had begun.
        if (report is not null && _registry.TryGet(m.Id, out var plugin))
        {
            try
            {
                var blocker = plugin.CheckCompatibility(report)
                    .FirstOrDefault(f => f.Severity == FindingSeverity.Blocker);
                if (blocker is not null)
                {
                    var reason = string.IsNullOrWhiteSpace(blocker.Remediation)
                        ? blocker.Message
                        : $"{blocker.Message} {blocker.Remediation}";
                    return new DistroListItem(m, IsCompatible: false,
                        IncompatibilityReason: reason, IsComingSoon: comingSoon);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A buggy plugin must not take down the catalog; fail open with a log.
                _logger.LogWarning(ex, "CheckCompatibility failed for {DistroId}", m.Id);
            }
        }

        return new DistroListItem(m, IsCompatible: true, IncompatibilityReason: null,
                                  IsComingSoon: comingSoon);
    }

    private static bool HasSecureBootTag(DistroManifest m)
        => m.Tags.Any(t => string.Equals(t, "secure-boot-supported", StringComparison.OrdinalIgnoreCase));
}

public sealed record DistroListItem(
    DistroManifest Manifest,
    bool IsCompatible,
    string? IncompatibilityReason,
    bool IsComingSoon = false,
    bool IsRecommended = false);
