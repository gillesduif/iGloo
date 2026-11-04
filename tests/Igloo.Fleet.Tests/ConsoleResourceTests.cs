using System.Text.RegularExpressions;
using System.Xml.Linq;
using Igloo.Fleet.Contracts;
using Igloo.Fleet.Web.Presentation;
using Microsoft.AspNetCore.Components;
using Xunit;

namespace Igloo.Fleet.Tests;

/// <summary>Terminology and localization contracts of the operator console: the strings are the deliverable.</summary>
public sealed class ConsoleResourceTests
{
    private static readonly string[] BannedWords =
        ["seamless", "effortless", "powerful", "unlock", "empower", "leverage", "magic", "smart", "ai-powered", "revolutionary"];

    private static readonly string ResourcesDirectory = Path.Combine(RepositoryRoot(), "src", "Igloo.Fleet.Web", "Resources");

    public static TheoryData<string> NeutralFiles =>
        new(Directory.EnumerateFiles(ResourcesDirectory, "*.resx").Where(static f => !Regex.IsMatch(Path.GetFileName(f), @"\.[a-z]{2}\.resx$")).Select(Path.GetFileName)!);

    [Theory]
    [MemberData(nameof(NeutralFiles))]
    public void EnglishStringsFollowTheCopyRules(string file)
    {
        foreach (var (key, value) in Read(Path.Combine(ResourcesDirectory, file)))
        {
            foreach (var banned in BannedWords)
            {
                Assert.False(Regex.IsMatch(value, @"\b" + Regex.Escape(banned) + @"\b", RegexOptions.IgnoreCase), $"{file}:{key} uses banned word '{banned}': {value}");
            }

            Assert.False(value.Contains('!', StringComparison.Ordinal), $"{file}:{key} contains an exclamation mark.");
            Assert.False(value.Any(static c => char.IsSurrogate(c)), $"{file}:{key} contains a non-BMP character (emoji).");
        }
    }

    [Fact]
    public void SatelliteFilesOnlyTranslateKeysThatExistInEnglish()
    {
        foreach (var satellite in Directory.EnumerateFiles(ResourcesDirectory, "*.??.resx"))
        {
            var neutral = Path.Combine(ResourcesDirectory, Regex.Replace(Path.GetFileName(satellite), @"\.[a-z]{2}\.resx$", ".resx"));
            var english = Read(neutral).Select(static p => p.Key).ToHashSet(StringComparer.Ordinal);
            foreach (var (key, value) in Read(satellite))
            {
                Assert.True(english.Contains(key), $"{Path.GetFileName(satellite)} translates '{key}', which English does not define.");
                Assert.False(string.IsNullOrWhiteSpace(value), $"{Path.GetFileName(satellite)}:{key} is empty; omit it so English is used instead.");
            }
        }
    }

    [Fact]
    public void EveryStatusInTheCatalogueHasALabel()
    {
        var keys = Read(Path.Combine(ResourcesDirectory, "StatusStrings.resx")).Select(static p => p.Key).ToHashSet(StringComparer.Ordinal);
        var presentations = new List<StatusPresentation>();
        presentations.AddRange(Enum.GetValues<AgentTrustStatus>().Select(StatusCatalog.Trust));
        presentations.AddRange(Enum.GetValues<ReadOnlyWorkStatus>().Select(StatusCatalog.Work));
        presentations.AddRange(Enum.GetValues<CheckStatus>().Select(StatusCatalog.Check));
        presentations.AddRange(Enum.GetValues<PlanningEligibility>().Select(StatusCatalog.Eligibility));
        presentations.AddRange(Enum.GetValues<EligibilityStatus>().Select(StatusCatalog.Assessment));
        presentations.AddRange(Enum.GetValues<ApprovalState>().Select(StatusCatalog.Approval));
        presentations.AddRange(Enum.GetValues<PreparedPlanState>().Select(StatusCatalog.Plan));
        presentations.Add(StatusCatalog.Connection(null));
        presentations.AddRange(Enum.GetValues<Igloo.Fleet.Web.Services.OperatorFailureKind>().Select(k => StatusCatalog.Connection(k)));
        var now = DateTimeOffset.UtcNow;
        presentations.Add(StatusCatalog.Certificate(now.AddDays(100), now, 14));
        presentations.Add(StatusCatalog.Certificate(now.AddDays(3), now, 14));
        presentations.Add(StatusCatalog.Certificate(now.AddHours(2), now, 14));
        presentations.Add(StatusCatalog.Certificate(now.AddDays(-1), now, 14));

        foreach (var presentation in presentations)
        {
            Assert.True(keys.Contains(presentation.LabelKey), $"StatusStrings lacks label '{presentation.LabelKey}'.");
            if (presentation.MeaningKey is not null)
            {
                Assert.True(keys.Contains(presentation.MeaningKey), $"StatusStrings lacks meaning '{presentation.MeaningKey}'.");
            }
        }

        foreach (var key in Enum.GetValues<ReadOnlyWorkType>().Select(StatusCatalog.WorkTypeKey)
                     .Concat(Enum.GetValues<CheckId>().Select(StatusCatalog.CheckIdKey))
                     .Concat(Enum.GetValues<PlanningReason>().Select(StatusCatalog.ReasonKey))
                     .Concat(Enum.GetValues<PlanningReason>().Select(StatusCatalog.ReasonMeaningKey))
                     .Concat(Enum.GetValues<PlanningCapability>().Select(StatusCatalog.CapabilityKey))
                     .Concat(Enum.GetValues<PlanningCapability>().Select(StatusCatalog.CapabilityMeaningKey))
                     .Concat(new[] { "Operator", "Agent", "Server", "Enrollment" }.Select(StatusCatalog.ActorTypeKey)))
        {
            Assert.True(keys.Contains(key), $"StatusStrings lacks '{key}'.");
        }
    }

    [Fact]
    public void EveryAuditActionTheServerEmitsHasALabel()
    {
        var keys = Read(Path.Combine(ResourcesDirectory, "StatusStrings.resx")).Select(static p => p.Key).ToHashSet(StringComparer.Ordinal);
        var domain = Directory.EnumerateFiles(Path.Combine(RepositoryRoot(), "src", "Igloo.Fleet.Domain"), "*.cs")
            .SelectMany(File.ReadAllLines)
            .SelectMany(static line => Regex.Matches(line, "Audit\\(state, \"[A-Za-z]+\", [^,]+, \"([A-Za-z]+)\"").Select(static m => m.Groups[1].Value))
            .Distinct()
            .ToArray();
        Assert.NotEmpty(domain);
        foreach (var action in domain)
        {
            Assert.True(keys.Contains(StatusCatalog.ActionKey(action)), $"StatusStrings lacks a label for audit action '{action}'.");
        }
    }

    [Fact]
    public void EveryModulePageHasLabelsForItsGroupFieldsActionsAndStatuses()
    {
        var shell = Read(Path.Combine(ResourcesDirectory, "ShellStrings.resx")).Select(static p => p.Key).ToHashSet(StringComparer.Ordinal);
        var module = Read(Path.Combine(ResourcesDirectory, "ModuleStrings.resx")).Select(static p => p.Key).ToHashSet(StringComparer.Ordinal);
        var status = Read(Path.Combine(ResourcesDirectory, "StatusStrings.resx")).Select(static p => p.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var group in ModuleCatalog.Groups)
        {
            Assert.True(shell.Contains(group.Key), $"ShellStrings lacks group '{group.Key}'.");
            foreach (var page in group.Pages)
            {
                Assert.True(shell.Contains(page.Key), $"ShellStrings lacks page '{page.Key}'.");
                Assert.True(page.CreateKey is null || module.Contains(page.CreateKey), $"ModuleStrings lacks action '{page.CreateKey}'.");
            }
        }

        foreach (var key in ModuleCatalog.AllFieldKeys().Append("Id"))
        {
            Assert.True(module.Contains("Field_" + key), $"ModuleStrings lacks field 'Field_{key}'.");
        }

        var subtle = new HashSet<string>(StringComparer.Ordinal) { "Unknown", "NotEvaluated", "Draft", "Disabled", "Inactive", "NotRun", "Low", "Cancelled", "Stopped" };
        foreach (var token in ModuleCatalog.AllStatusTokens())
        {
            Assert.True(status.Contains("Module_" + token), $"StatusStrings lacks status 'Module_{token}'.");
            var presentation = StatusCatalog.Module(token);
            Assert.True(subtle.Contains(token) || presentation.Color != Microsoft.FluentUI.AspNetCore.Components.BadgeColor.Subtle, $"Status '{token}' falls through to the unknown presentation.");
        }

        var hrefs = ModuleCatalog.AllPages().Select(static p => p.Href).ToArray();
        Assert.Equal(hrefs.Length, hrefs.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void EveryNavigationEntryPointsAtAPage()
    {
        var templates = typeof(Igloo.Fleet.Web.SharedStrings).Assembly.GetTypes()
            .SelectMany(static t => t.GetCustomAttributes(typeof(RouteAttribute), false).Cast<RouteAttribute>())
            .Select(static r => r.Template)
            .ToArray();
        foreach (var entry in ConsoleNavigation.AllEntries().Where(static e => e.Implemented))
        {
            Assert.True(templates.Any(t => Matches(t, entry.Href)), $"Navigation entry '{entry.Key}' ({entry.Href}) has no page.");
        }
    }

    private static bool Matches(string template, string href)
    {
        var templateParts = template.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var hrefParts = href.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (templateParts.Length != hrefParts.Length)
        {
            return false;
        }

        return templateParts.Zip(hrefParts).All(static pair =>
            pair.First.StartsWith('{') || string.Equals(pair.First, pair.Second, StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<(string Key, string Value)> Read(string path) =>
        XDocument.Load(path).Root!.Elements("data")
            .Select(static d => (d.Attribute("name")!.Value, d.Element("value")?.Value ?? string.Empty));

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Igloo.sln")))
        {
            root = root.Parent;
        }

        return root?.FullName ?? throw new InvalidOperationException("Igloo.sln not found.");
    }
}
