using Igloo.Core.Abstractions;
using Igloo.Distro.Debian.Deployment;
using Igloo.TestData;
using Xunit;
using static Igloo.Migration.Tests.DebianDeploymentFixture;

namespace Igloo.Migration.Tests;

public sealed class DebianRecommendationPolicyTests
{
    private static DebianUnresolvedRelationV1 Relation() => new("debian", "trixie", "gvfs-backends",
        "1.57.2-2+deb13u1", "amd64", "Recommends", "wsdd", false);

    private static OfflineDebianPackageSetV1 Set()
    {
        var set = DebianContentFoundationTests.PackageSet(Plan(TargetRootFixture.Create()));
        return set with { Packages = set.Packages.Add(new("debian", "gvfs-backends", "1.57.2-2+deb13u1", "amd64",
            new("pool/main/g/gvfs/gvfs.deb", 99, Hash))),
            DependencyPolicy = new(1, [Relation()], [new(DebianRecommendationPolicy.Wsdd.Id, Relation())], []) };
    }

    [Fact]
    public void ReviewedExceptionIsVersionedAndDoesNotChangeRecommends()
    {
        var policy = DebianWorkstationPolicy.Trixie;
        Assert.True(policy.InstallRecommends);
        Assert.False(policy.InstallSuggests);
        Assert.Equal("trixie-workstation-2026-09-28.1", policy.Revision);
        Assert.Equal(DebianRecommendationPolicy.Wsdd, Assert.Single(policy.RecommendationExceptions));
        Assert.DoesNotContain("wsdd2", policy.Packages);
        Assert.Equal(TimeSpan.FromDays(30), DebianRecommendationPolicy.Wsdd.ReviewBeforeUtc - DebianRecommendationPolicy.Wsdd.ReviewedUtc);
    }

    [Fact]
    public void ExactAppliedExceptionRetainsOriginalRelationAcrossReopen()
    {
        var set = Set();
        var bytes = OfflineDebianPackageSets.Serialize(set);
        var reopened = OfflineDebianPackageSets.Reopen(bytes, DebianConfiguredRootArtifacts.Digest(bytes), set.GenerationId);
        Assert.Equal(Relation(), Assert.Single(reopened.DependencyPolicy.OriginalUnresolved));
        Assert.Equal(DebianRecommendationPolicy.Wsdd.Id, Assert.Single(reopened.DependencyPolicy.AppliedExceptions).ExceptionId);
        Assert.Empty(reopened.DependencyPolicy.RemainingUnresolved);
    }

    [Theory]
    [InlineData("repository")]
    [InlineData("suite")]
    [InlineData("package")]
    [InlineData("version")]
    [InlineData("architecture")]
    [InlineData("depends")]
    [InlineData("predepends")]
    [InlineData("versioned-relation")]
    [InlineData("alternative")]
    [InlineData("available")]
    public void ExceptionCannotCoverAnotherRelation(string change)
    {
        var relation = change switch
        {
            "repository" => Relation() with { RepositoryId = "debian-security" },
            "suite" => Relation() with { Suite = "trixie-security" },
            "package" => Relation() with { Package = "gnome" },
            "version" => Relation() with { Version = "1.57.2-3" },
            "architecture" => Relation() with { Architecture = "all" },
            "depends" => Relation() with { Kind = "Depends" },
            "predepends" => Relation() with { Kind = "PreDepends" },
            "versioned-relation" => Relation() with { Relation = "wsdd (>= 1)" },
            "alternative" => Relation() with { Relation = "wsdd | wsdd2" },
            "available" => Relation() with { CandidateAvailable = true },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        var set = Set() with { DependencyPolicy = new(1, [relation], [new(DebianRecommendationPolicy.Wsdd.Id, relation)], []) };
        Assert.False(DebianRecommendationPolicy.Matches(relation));
        Assert.Equal(ObservationAvailability.Ambiguous, OfflineDebianPackageSets.Validate(set, set.GenerationId).Availability);
    }

    [Theory]
    [InlineData("missing-application")]
    [InlineData("unknown-exception")]
    [InlineData("extra-application")]
    [InlineData("extra-original")]
    [InlineData("remaining")]
    [InlineData("package-changed")]
    public void ReportRequiresBidirectionalExactCorrespondence(string change)
    {
        var set = Set(); var report = set.DependencyPolicy;
        set = change switch
        {
            "missing-application" => set with { DependencyPolicy = report with { AppliedExceptions = [] } },
            "unknown-exception" => set with { DependencyPolicy = report with { AppliedExceptions = [new("ignore-all", Relation())] } },
            "extra-application" => set with { DependencyPolicy = report with { AppliedExceptions = report.AppliedExceptions.Add(report.AppliedExceptions[0]) } },
            "extra-original" => set with { DependencyPolicy = report with { OriginalUnresolved = report.OriginalUnresolved.Add(Relation()) } },
            "remaining" => set with { DependencyPolicy = report with { RemainingUnresolved = [Relation() with { Relation = "missing-other" }] } },
            "package-changed" => set with { Packages = set.Packages.SetItem(set.Packages.Length - 1, set.Packages[^1] with { Version = "1.57.2-3" }) },
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };
        Assert.Equal(ObservationAvailability.Ambiguous, OfflineDebianPackageSets.Validate(set, set.GenerationId).Availability);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RepositoryAliasRequiresSameAuthenticatedPackageBytes(bool changed)
    {
        var set = Set(); var package = set.Packages[^1];
        var alias = package with { RepositoryId = "debian-security", File = package.File with {
            Path = "pool/updates/main/g/gvfs/gvfs.deb", Sha256 = changed ? new string('B', 64) : package.File.Sha256 } };
        set = set with { RepositoryAliases = [alias] };
        Assert.Equal(changed ? ObservationAvailability.Ambiguous : ObservationAvailability.Available,
            OfflineDebianPackageSets.Validate(set, set.GenerationId).Availability);
    }

    [Theory]
    [InlineData("7zip", true)]
    [InlineData("-option", false)]
    [InlineData("7", false)]
    [InlineData("7ZIP", false)]
    [InlineData("7zip;cmd", false)]
    public void RealPackageNameGrammarAllowsDigitsWithoutAllowingOptions(string name, bool valid)
    {
        var package = new DebianPackageV1(name, "25.01+dfsg-1~deb13u2", "amd64", 99, Hash);
        if (valid)
        {
            Assert.Contains("7zip:amd64=25.01+dfsg-1~deb13u2", DebianWorkstationPolicy.InstallPinned([package]).Arguments);
            var plan = Plan(TargetRootFixture.Create());
            Assert.True(DebianDeploymentPlanning.ValidStructure(plan with { Source = plan.Source with { Packages = plan.Source.Packages.Add(package) } }));
        }
        else Assert.Throws<InvalidDataException>(() => DebianWorkstationPolicy.InstallPinned([package]));
    }
}
