using CommunityToolkit.Mvvm.ComponentModel;
using Igloo.Core.Models;
using Igloo.Core.Plugins;

namespace Igloo.App.ViewModels;

/// <summary>One selectable answer in the Welcome-step "find your Linux" quiz.</summary>
public sealed record QuizOption(string Id, string Label);

/// <summary>
/// Welcome step. Besides routing to WelcomePage, it hosts the "find your Linux"
/// quiz: three optional multiple-choice questions whose answers produce a
/// recommended distro. The wizard hands <see cref="RecommendedDistroId"/> to the
/// distro picker on navigation, where it becomes the RECOMMENDED badge and the
/// carousel's opening position.
/// </summary>
public sealed partial class WelcomeViewModel : ObservableObject
{
    private readonly DistroLoader _loader;

    public WelcomeViewModel(DistroLoader loader) => _loader = loader;

    public IReadOnlyList<QuizOption> UseOptions { get; } =
    [
        new(DistroRecommender.UseEveryday, "Everyday & web"),
        new(DistroRecommender.UseGaming,   "Gaming"),
        new(DistroRecommender.UseWork,     "Work & school"),
        new(DistroRecommender.UseTinker,   "Tinkering & code"),
    ];

    public IReadOnlyList<QuizOption> StyleOptions { get; } =
    [
        new(DistroRecommender.StyleFamiliar, "Familiar, like Windows"),
        new(DistroRecommender.StyleFresh,    "Fresh & modern"),
    ];

    public IReadOnlyList<QuizOption> UpdateOptions { get; } =
    [
        new(DistroRecommender.UpdatesStable, "Rock-solid stable"),
        new(DistroRecommender.UpdatesLatest, "Latest & greatest"),
    ];

    [ObservableProperty]
    private QuizOption? _selectedUse;

    [ObservableProperty]
    private QuizOption? _selectedStyle;

    [ObservableProperty]
    private QuizOption? _selectedUpdates;

    partial void OnSelectedUseChanged(QuizOption? value) => NotifyRecommendationChanged();
    partial void OnSelectedStyleChanged(QuizOption? value) => NotifyRecommendationChanged();
    partial void OnSelectedUpdatesChanged(QuizOption? value) => NotifyRecommendationChanged();

    /// <summary>
    /// Ranked winning distros, best first; empty until all three questions are
    /// answered. No visible result here — the picker shows a Recommended
    /// category chip plus per-cover badges on the next step.
    /// </summary>
    public IReadOnlyList<string> RecommendedDistroIds =>
        DistroRecommender.Recommend(
                _loader.LoadedDistros, SelectedUse?.Id, SelectedStyle?.Id, SelectedUpdates?.Id)
            .Select(m => m.Id)
            .ToList();

    private void NotifyRecommendationChanged()
        => OnPropertyChanged(nameof(RecommendedDistroIds));
}
