using System.Text.Json;
using FluentAssertions;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Services;
using Igloo.Distro.Deepin;
using Xunit;

namespace Igloo.Distro.Deepin.Tests;

public sealed class DeepinTargetIdentityTests
{
    [Fact]
    public void Deepin_requires_both_an_owned_root_and_an_explicit_esp()
    {
        var consumer = new DeepinPlugin();
        consumer.Should().BeAssignableTo<IInstallationTargetConsumer>();
        consumer.TargetRequirement.Should().Be(InstallationTargetRequirement.CreatedRootAndEsp);
    }

    [Fact]
    public void Valid_identity_does_not_enable_any_Deepin_execution()
    {
        var manifest = Manifest();
        var claim = InstallationTargetValidation.RequireClaim(manifest, manifest.InstallationId!.Value);
        InstallationTargetValidation.ValidateObserved(claim, [claim.Disk]).Should().Be(claim.Disk);
        var plugin = new DeepinPlugin();
        ((Action)(() => plugin.RenderInstallerConfigAsync(manifest))).Should().Throw<NotSupportedException>();
        ((Action)(() => plugin.GetInstallerBootSpec())).Should().Throw<NotSupportedException>();
        ((Action)(() => plugin.GetAgentPayloadAsync())).Should().Throw<NotSupportedException>();
    }

    [Theory]
    [InlineData("absent")]
    [InlineData("malformed")]
    [InlineData("missing-esp")]
    [InlineData("wrong-esp")]
    [InlineData("stale")]
    public void Required_identity_rejects_invalid_Deepin_targets(string defect)
    {
        var manifest = Manifest();
        var expectedId = manifest.InstallationId!.Value;
        var claim = manifest.InstallationTarget!;
        manifest = defect switch
        {
            "absent" => manifest with { InstallationTarget = null },
            "malformed" => manifest with { InstallationTarget = claim with { RootPartitionGuid = Guid.Empty } },
            "missing-esp" => manifest with { InstallationTarget = claim with { EspPartitionGuid = null } },
            "wrong-esp" => manifest with { InstallationTarget = claim with { EspPartitionGuid = claim.RootPartitionGuid } },
            "stale" => manifest with { InstallationId = Guid.NewGuid() },
            _ => throw new InvalidOperationException(),
        };
        ((Action)(() => InstallationTargetValidation.RequireClaim(manifest, expectedId)))
            .Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void A_Deepin_target_must_resolve_uniquely_even_with_valid_metadata()
    {
        var manifest = Manifest();
        var claim = InstallationTargetValidation.RequireClaim(manifest, manifest.InstallationId!.Value);
        ((Action)(() => InstallationTargetValidation.ValidateObserved(claim, [claim.Disk, claim.Disk])))
            .Should().Throw<InvalidDataException>();
        ((Action)(() => InstallationTargetValidation.ValidateObserved(claim, [])))
            .Should().Throw<InvalidDataException>();
    }

    private static MigrationManifest Manifest()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var path = Path.Join(directory.FullName, "tests", "fixtures", "installation-target.json");
            if (File.Exists(path))
                return JsonSerializer.Deserialize<MigrationManifest>(File.ReadAllText(path))!;
            directory = directory.Parent;
        }
        throw new FileNotFoundException("The shared installation target fixture is missing.");
    }
}
