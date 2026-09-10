using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Services;
using Xunit;

namespace Igloo.Core.Tests;

/// <summary>Disk safety checks use synthetic layouts and files only; no storage APIs are called.</summary>
public sealed class InstallationTargetIdentityTests
{
    private const long MiB = 1024 * 1024;
    private const long GiB = 1024 * MiB;
    private static readonly Guid InstallationId = new("11111111-1111-4111-8111-111111111111");
    private static readonly Guid RootId = new("66666666-6666-4666-8666-666666666666");
    private static readonly Guid OtherId = new("88888888-8888-4888-8888-888888888888");
    private static readonly Guid LinuxType = new("0fc63daf-8483-4772-8e79-3d69d8477de4");
    private static readonly Guid WindowsType = new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7");

    [Fact]
    public void Shared_fixture_round_trips_and_selects_the_owned_root_among_existing_linux_partitions()
    {
        var json = FixtureJson();
        var manifest = JsonSerializer.Deserialize<MigrationManifest>(json)!;
        var claim = InstallationTargetManifest.ReadClaim(json, InstallationId);

        claim.RootPartitionGuid.Should().Be(RootId);
        claim.Disk.Partitions.Count(p => p.GptType == LinuxType).Should().Be(2);
        InstallationTargetValidation.RequireClaim(manifest, InstallationId).Should().BeEquivalentTo(claim);
        InstallationTargetManifest.ReadClaim(JsonSerializer.Serialize(manifest), InstallationId)
            .Should().BeEquivalentTo(claim);
    }

    [Fact]
    public void Capture_requires_exactly_the_new_partition_in_the_approved_fragmented_extent()
    {
        var claim = FixtureClaim();
        var captured = InstallationTargetValidation.CaptureCreated(Request(), claim.Disk, RootId);

        captured.Should().BeEquivalentTo(claim);
        captured.Ownership.Should().Be("created-by-igloo");
    }

    [Fact]
    public void Fresh_manifests_get_distinct_installation_ids_without_issuing_ownership_claims()
    {
        var user = new UserSetup { WindowsUsername = "fixture", LinuxUsername = "fixture" };
        var report = new PreflightReport
        {
            IsUefi = true, SecureBootEnabled = false, TpmPresent = true,
            BitLocker = BitLockerState.NotEncrypted, Disks = [], GpuVendor = "intel",
            TotalRamBytes = 16 * GiB, Findings = [],
        };
        var staging = new FileStagingResult("fixture-staging", 0, 0);
        var first = ManifestGeneratorService.Generate("deepin", user, report, staging);
        var second = ManifestGeneratorService.Generate("deepin", user, report, staging);

        first.InstallationId.Should().NotBeNull().And.NotBe(Guid.Empty);
        second.InstallationId.Should().NotBeNull().And.NotBe(first.InstallationId!.Value);
        first.InstallationTarget.Should().BeNull();
        second.InstallationTarget.Should().BeNull();
    }

    [Fact]
    public void Published_claim_freezes_the_observed_partition_collection()
    {
        var original = FixtureClaim().Disk;
        var providerPartitions = original.Partitions.ToList();
        var captured = InstallationTargetValidation.CaptureCreated(Request(),
            original with { Partitions = providerPartitions }, RootId);
        providerPartitions.Clear();

        captured.Disk.Partitions.Should().HaveCount(5);
        InstallationTargetValidation.ValidateClaim(captured);
    }

    [Fact]
    public void Reordered_partitions_are_the_same_layout_and_disk_enumeration_is_irrelevant()
    {
        var claim = FixtureClaim();
        var reversed = claim.Disk with { Partitions = claim.Disk.Partitions.Reverse().ToArray() };
        InstallationTargetValidation.ValidateUnchangedLayout(claim.Disk, reversed);
        var unrelated = claim.Disk with
        {
            DiskGuid = OtherId,
            Partitions = [Partition(OtherId, 2 * MiB, MiB)],
        };

        InstallationTargetValidation.ValidateObserved(claim, [unrelated, reversed])
            .DiskGuid.Should().Be(claim.Disk.DiskGuid);
    }

    [Theory]
    [InlineData("disk-guid")]
    [InlineData("disk-size")]
    [InlineData("sector-size")]
    [InlineData("partition-guid")]
    [InlineData("partition-type")]
    [InlineData("offset")]
    [InlineData("size")]
    [InlineData("missing")]
    [InlineData("extra")]
    public void Existing_layout_changes_abort_creation_capture(string change)
    {
        var request = Request();
        var observed = ChangeLayout(FixtureClaim().Disk, change);

        var act = () => InstallationTargetValidation.CaptureCreated(request, observed, RootId);
        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("wrong-returned-guid")]
    [InlineData("existing-returned-guid")]
    [InlineData("wrong-root-type")]
    [InlineData("wrong-root-offset")]
    [InlineData("wrong-root-size")]
    [InlineData("missing-root")]
    public void Capture_never_adopts_an_existing_or_different_partition(string change)
    {
        var claim = FixtureClaim();
        var returned = change switch
        {
            "wrong-returned-guid" => OtherId,
            "existing-returned-guid" => claim.Disk.Partitions[2].PartitionGuid,
            _ => RootId,
        };
        var disk = change switch
        {
            "wrong-root-type" => ReplaceRoot(claim.Disk, p => p with { GptType = WindowsType }),
            "wrong-root-offset" => ReplaceRoot(claim.Disk, p => p with { OffsetBytes = p.OffsetBytes + MiB }),
            "wrong-root-size" => ReplaceRoot(claim.Disk, p => p with { LengthBytes = p.LengthBytes - MiB }),
            "missing-root" => claim.Disk with { Partitions = claim.Disk.Partitions.Where(p => p.PartitionGuid != RootId).ToArray() },
            _ => claim.Disk,
        };

        var act = () => InstallationTargetValidation.CaptureCreated(Request(), disk, returned);
        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("empty-disk-guid")]
    [InlineData("unsupported-sector")]
    [InlineData("zero-disk")]
    [InlineData("unaligned-disk")]
    [InlineData("empty-partition-guid")]
    [InlineData("empty-type")]
    [InlineData("duplicate-guid")]
    [InlineData("overlap")]
    [InlineData("negative-offset")]
    [InlineData("zero-size")]
    [InlineData("unaligned-offset")]
    [InlineData("unaligned-size")]
    [InlineData("gpt-header")]
    [InlineData("gpt-trailer")]
    [InlineData("overflow")]
    public void Invalid_layout_geometry_and_identity_fail_closed(string change)
    {
        var disk = FixtureClaim().Disk;
        var first = disk.Partitions[0];
        disk = change switch
        {
            "empty-disk-guid" => disk with { DiskGuid = Guid.Empty },
            "unsupported-sector" => disk with { LogicalSectorSize = 1024 },
            "zero-disk" => disk with { DiskSizeBytes = 0 },
            "unaligned-disk" => disk with { DiskSizeBytes = disk.DiskSizeBytes - 1 },
            "empty-partition-guid" => WithFirst(disk, first with { PartitionGuid = Guid.Empty }),
            "empty-type" => WithFirst(disk, first with { GptType = Guid.Empty }),
            "duplicate-guid" => WithFirst(disk, first with { PartitionGuid = disk.Partitions[1].PartitionGuid }),
            "overlap" => WithFirst(disk, first with { LengthBytes = 2 * GiB }),
            "negative-offset" => WithFirst(disk, first with { OffsetBytes = -512 }),
            "zero-size" => WithFirst(disk, first with { LengthBytes = 0 }),
            "unaligned-offset" => WithFirst(disk, first with { OffsetBytes = first.OffsetBytes + 1 }),
            "unaligned-size" => WithFirst(disk, first with { LengthBytes = first.LengthBytes + 1 }),
            "gpt-header" => WithFirst(disk, first with { OffsetBytes = 512 }),
            "gpt-trailer" => WithFirst(disk, first with { OffsetBytes = disk.DiskSizeBytes - MiB, LengthBytes = MiB }),
            "overflow" => WithFirst(disk, first with { OffsetBytes = long.MaxValue - 511, LengthBytes = 1024 }),
            _ => throw new ArgumentException("Unknown scenario", nameof(change)),
        };

        var act = () => InstallationTargetValidation.ValidateLayout(disk);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Four_kilobyte_logical_sectors_are_supported_without_512_byte_assumptions()
    {
        var claim = FixtureClaim();
        var disk = claim.Disk with { LogicalSectorSize = 4096 };
        InstallationTargetValidation.ValidateLayout(disk);
        var request = Request() with { ExpectedLayout = BeforeCreation(disk) };
        InstallationTargetValidation.CaptureCreated(request, disk, RootId).Disk.LogicalSectorSize.Should().Be(4096);
    }

    [Theory]
    [InlineData("no-installation-id")]
    [InlineData("unaligned-offset")]
    [InlineData("unaligned-length")]
    [InlineData("zero-length")]
    [InlineData("overlap-existing")]
    [InlineData("past-disk")]
    [InlineData("header-guard")]
    [InlineData("trailer-guard")]
    [InlineData("missing-esp")]
    [InlineData("wrong-esp-type")]
    public void Unsafe_creation_requests_are_rejected_before_any_storage_call(string change)
    {
        var request = Request();
        request = change switch
        {
            "no-installation-id" => request with { InstallationId = Guid.Empty },
            "unaligned-offset" => request with { RootOffsetBytes = request.RootOffsetBytes + 1 },
            "unaligned-length" => request with { RootLengthBytes = request.RootLengthBytes + 1 },
            "zero-length" => request with { RootLengthBytes = 0 },
            "overlap-existing" => request with { RootOffsetBytes = 60 * GiB },
            "past-disk" => request with { RootLengthBytes = long.MaxValue },
            "header-guard" => request with { RootOffsetBytes = 512, RootLengthBytes = 512 },
            "trailer-guard" => request with { RootOffsetBytes = 127 * GiB, RootLengthBytes = GiB },
            "missing-esp" => request with { EspPartitionGuid = OtherId },
            "wrong-esp-type" => request with { EspPartitionGuid = request.ExpectedLayout.Partitions[1].PartitionGuid },
            _ => throw new ArgumentException("Unknown scenario", nameof(change)),
        };

        var act = () => InstallationTargetValidation.ValidateRequest(request);
        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("missing-disk")]
    [InlineData("duplicate-disk")]
    [InlineData("changed-layout")]
    [InlineData("missing-root")]
    public void Observed_disks_must_match_one_complete_unchanged_claim(string change)
    {
        var claim = FixtureClaim();
        GptDiskLayout[] disks = change switch
        {
            "missing-disk" => [],
            "duplicate-disk" => [claim.Disk, claim.Disk],
            "changed-layout" => [ChangeLayout(claim.Disk, "offset")],
            "missing-root" => [BeforeCreation(claim.Disk)],
            _ => throw new ArgumentException("Unknown scenario", nameof(change)),
        };

        var act = () => InstallationTargetValidation.ValidateObserved(claim, disks);
        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("claimed-partition-guid")]
    [InlineData("unrelated-partition-guid")]
    [InlineData("unrelated-disk-guid")]
    public void Duplicate_identities_anywhere_in_the_observed_inventory_are_rejected(string scenario)
    {
        var claim = FixtureClaim();
        var otherDisk = claim.Disk with
        {
            DiskGuid = OtherId,
            Partitions = [Partition(scenario == "claimed-partition-guid" ? RootId : OtherId, 2 * MiB, MiB)],
        };
        var thirdDisk = otherDisk with
        {
            DiskGuid = scenario == "unrelated-disk-guid" ? OtherId : Guid.Parse("99999999-9999-4999-8999-999999999999"),
            Partitions = scenario == "unrelated-disk-guid" ? [] : otherDisk.Partitions,
        };
        var observed = scenario == "claimed-partition-guid"
            ? new[] { claim.Disk, otherDisk }
            : [claim.Disk, otherDisk, thirdDisk];

        var act = () => InstallationTargetValidation.ValidateObserved(claim, observed);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Old_manifests_remain_readable_but_cannot_authorize_formatting()
    {
        var node = FixtureNode();
        node.Remove("installationId");
        node.Remove("installationTarget");
        var manifest = JsonSerializer.Deserialize<MigrationManifest>(node.ToJsonString())!;

        manifest.InstallationId.Should().BeNull();
        manifest.InstallationTarget.Should().BeNull();
        var act = () => InstallationTargetValidation.RequireClaim(manifest, InstallationId);
        act.Should().Throw<InvalidDataException>();
    }

    [Theory]
    [InlineData("version")]
    [InlineData("ownership")]
    [InlineData("installation-id")]
    [InlineData("root-id")]
    [InlineData("root-type")]
    [InlineData("esp-id")]
    [InlineData("esp-type")]
    public void Invalid_claims_never_authorize_an_observed_disk(string change)
    {
        var claim = FixtureClaim();
        claim = change switch
        {
            "version" => claim with { Version = 2 },
            "ownership" => claim with { Ownership = "existing-partition" },
            "installation-id" => claim with { InstallationId = Guid.Empty },
            "root-id" => claim with { RootPartitionGuid = OtherId },
            "root-type" => claim with { Disk = ReplaceRoot(claim.Disk, p => p with { GptType = WindowsType }) },
            "esp-id" => claim with { EspPartitionGuid = OtherId },
            "esp-type" => claim with { EspPartitionGuid = claim.Disk.Partitions[1].PartitionGuid },
            _ => throw new ArgumentException("Unknown scenario", nameof(change)),
        };

        var act = () => InstallationTargetValidation.ValidateObserved(claim, [claim.Disk]);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Installation_ids_must_agree_at_envelope_claim_and_expected_invocation()
    {
        var manifest = JsonSerializer.Deserialize<MigrationManifest>(FixtureJson())!;
        Action wrongExpected = () => InstallationTargetValidation.RequireClaim(manifest, OtherId);
        Action wrongEnvelope = () => InstallationTargetValidation.RequireClaim(manifest with { InstallationId = OtherId }, InstallationId);
        Action wrongClaim = () => InstallationTargetValidation.RequireClaim(
            manifest with { InstallationTarget = manifest.InstallationTarget! with { InstallationId = OtherId } }, InstallationId);

        wrongExpected.Should().Throw<InvalidDataException>();
        wrongEnvelope.Should().Throw<InvalidDataException>();
        wrongClaim.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Null_esp_requires_an_explicit_non_uefi_consumer()
    {
        var claim = FixtureClaim() with { EspPartitionGuid = null };
        var manifest = JsonSerializer.Deserialize<MigrationManifest>(FixtureJson())! with { InstallationTarget = claim };
        Action uefi = () => InstallationTargetValidation.RequireClaim(manifest, InstallationId);
        uefi.Should().Throw<InvalidDataException>();
        InstallationTargetValidation.RequireClaim(manifest, InstallationId, requireEsp: false).Should().Be(claim);
        InstallationTargetValidation.ValidateObserved(claim, [claim.Disk], requireEsp: false).Should().Be(claim.Disk);
    }

    public static IEnumerable<object[]> InvalidWireCases()
    {
        foreach (var path in new[] { "schemaVersion", "installationId", "installationTarget", "installationTarget.version", "installationTarget.installationId", "installationTarget.ownership", "installationTarget.disk", "installationTarget.rootPartitionGuid", "installationTarget.espPartitionGuid", "installationTarget.disk.diskGuid", "installationTarget.disk.logicalSectorSize", "installationTarget.disk.diskSizeBytes", "installationTarget.disk.partitions" })
            yield return ["missing:" + path];
        foreach (var path in new[] { "installationId", "installationTarget", "installationTarget.disk", "installationTarget.ownership", "installationTarget.rootPartitionGuid", "installationTarget.disk.partitions" })
            yield return ["null:" + path];
        foreach (var value in new[] { "bad-guid", "uppercase-guid", "braced-guid", "zero-guid", "wrong-version", "old-version", "old-envelope-version", "future-envelope-version", "boolean-version", "fractional-version", "unknown-claim", "unknown-disk", "unknown-partition", "partial-partition", "missing-partition-guid", "missing-partition-offset", "missing-partition-length", "null-partition", "empty-partitions", "string-size", "overflow-size", "mismatched-id", "duplicate-key", "duplicate-nested-key", "duplicate-partition-key", "invalid-json", "array-envelope" })
            yield return [value];
    }

    [Theory]
    [MemberData(nameof(InvalidWireCases))]
    public void Strict_wire_reader_rejects_partial_noncanonical_or_ambiguous_claims(string scenario)
    {
        var json = InvalidWireJson(scenario);
        var act = () => InstallationTargetManifest.ReadClaim(json, InstallationId);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Strict_wire_reader_allows_unrelated_manifest_extensions()
    {
        var node = FixtureNode();
        node["futureMigrationFeature"] = new JsonObject { ["enabled"] = true };
        InstallationTargetManifest.ReadClaim(node.ToJsonString(), InstallationId).RootPartitionGuid.Should().Be(RootId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Strict_wire_reader_requires_the_expected_active_installation_id(bool missing)
    {
        var act = () => InstallationTargetManifest.ReadClaim(FixtureJson(), missing ? Guid.Empty : OtherId);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task Writing_claim_preserves_unrelated_manifest_data_and_is_not_repeatable()
    {
        var path = TemporaryPath();
        try
        {
            var pending = FixtureNode();
            pending.Remove("installationTarget");
            pending["futureMigrationFeature"] = new JsonObject { ["userText"] = "Keep my settings" };
            await File.WriteAllTextAsync(path, pending.ToJsonString());
            var hash = await InstallationTargetManifest.ValidatePendingAsync(path, InstallationId, CancellationToken.None);
            await InstallationTargetManifest.WriteClaimAsync(path, FixtureClaim(), hash, CancellationToken.None);

            var savedJson = await File.ReadAllTextAsync(path);
            var saved = JsonNode.Parse(savedJson)!;
            saved["futureMigrationFeature"]!["userText"]!.GetValue<string>().Should().Be("Keep my settings");
            saved["user"]!.ToJsonString().Should().Be(pending["user"]!.ToJsonString());
            InstallationTargetManifest.ReadClaim(savedJson, InstallationId).Should().BeEquivalentTo(FixtureClaim());
            Func<Task> repeat = () => InstallationTargetManifest.WriteClaimAsync(path, FixtureClaim(), hash, CancellationToken.None);
            await repeat.Should().ThrowAsync<InvalidDataException>();
            (await File.ReadAllTextAsync(path)).Should().Be(savedJson);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Changed_manifest_bytes_abort_publication_and_preserve_the_new_bytes()
    {
        var path = TemporaryPath();
        try
        {
            var node = FixtureNode();
            node.Remove("installationTarget");
            await File.WriteAllTextAsync(path, node.ToJsonString());
            var hash = await InstallationTargetManifest.ValidatePendingAsync(path, InstallationId, CancellationToken.None);
            var changed = node.ToJsonString() + "\n";
            await File.WriteAllTextAsync(path, changed);

            Func<Task> act = () => InstallationTargetManifest.WriteClaimAsync(path, FixtureClaim(), hash, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidDataException>();
            (await File.ReadAllTextAsync(path)).Should().Be(changed);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Explicit_null_target_is_pending_and_cancellation_keeps_it_unchanged()
    {
        var path = TemporaryPath();
        try
        {
            var node = FixtureNode();
            node["installationTarget"] = null;
            var original = node.ToJsonString();
            await File.WriteAllTextAsync(path, original);
            var hash = await InstallationTargetManifest.ValidatePendingAsync(path, InstallationId, CancellationToken.None);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();

            Func<Task> act = () => InstallationTargetManifest.WriteClaimAsync(path, FixtureClaim(), hash, cancellation.Token);
            await act.Should().ThrowAsync<OperationCanceledException>();
            (await File.ReadAllTextAsync(path)).Should().Be(original);
            Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".*.tmp").Should().BeEmpty();
        }
        finally { File.Delete(path); }
    }

    [Theory]
    [InlineData("missing-id")]
    [InlineData("different-id")]
    [InlineData("missing-version")]
    [InlineData("different-version")]
    [InlineData("already-claimed")]
    public async Task Pending_manifest_requires_the_current_unclaimed_invocation(string scenario)
    {
        var path = TemporaryPath();
        try
        {
            var node = FixtureNode();
            if (scenario != "already-claimed") node.Remove("installationTarget");
            switch (scenario)
            {
                case "missing-id": node.Remove("installationId"); break;
                case "different-id": node["installationId"] = OtherId.ToString("D"); break;
                case "missing-version": node.Remove("schemaVersion"); break;
                case "different-version": node["schemaVersion"] = 2; break;
            }
            await File.WriteAllTextAsync(path, node.ToJsonString());
            Func<Task> act = () => InstallationTargetManifest.ValidatePendingAsync(path, InstallationId, CancellationToken.None);
            await act.Should().ThrowAsync<InvalidDataException>();
        }
        finally { File.Delete(path); }
    }

    private static string InvalidWireJson(string scenario)
    {
        var node = FixtureNode();
        if (scenario.StartsWith("missing:", StringComparison.Ordinal) || scenario.StartsWith("null:", StringComparison.Ordinal))
        {
            var parts = scenario[(scenario.IndexOf(':', StringComparison.Ordinal) + 1)..].Split('.');
            var parent = node;
            foreach (var part in parts[..^1]) parent = parent[part]!.AsObject();
            if (scenario.StartsWith("missing:", StringComparison.Ordinal)) parent.Remove(parts[^1]);
            else parent[parts[^1]] = null;
            return node.ToJsonString();
        }

        var target = node["installationTarget"]!.AsObject();
        var disk = target["disk"]!.AsObject();
        var part0 = disk["partitions"]![0]!.AsObject();
        switch (scenario)
        {
            case "bad-guid": target["rootPartitionGuid"] = "not-a-guid"; break;
            case "uppercase-guid": part0["gptType"] = part0["gptType"]!.GetValue<string>().ToUpperInvariant(); break;
            case "braced-guid": target["rootPartitionGuid"] = "{" + RootId.ToString("D") + "}"; break;
            case "zero-guid": target["rootPartitionGuid"] = Guid.Empty.ToString("D"); break;
            case "wrong-version": target["version"] = 2; break;
            case "old-version": target["version"] = 0; break;
            case "old-envelope-version": node["schemaVersion"] = 0; break;
            case "future-envelope-version": node["schemaVersion"] = 2; break;
            case "boolean-version": target["version"] = true; break;
            case "fractional-version": target["version"] = 1.5; break;
            case "unknown-claim": target["formatWholeDisk"] = true; break;
            case "unknown-disk": disk["device"] = "/dev/sda"; break;
            case "unknown-partition": part0["partitionNumber"] = 1; break;
            case "partial-partition": part0.Remove("gptType"); break;
            case "missing-partition-guid": part0.Remove("partitionGuid"); break;
            case "missing-partition-offset": part0.Remove("offsetBytes"); break;
            case "missing-partition-length": part0.Remove("lengthBytes"); break;
            case "null-partition": disk["partitions"]![0] = null; break;
            case "empty-partitions": disk["partitions"] = new JsonArray(); break;
            case "string-size": part0["lengthBytes"] = "272629760"; break;
            case "overflow-size": return node.ToJsonString().Replace("272629760", "9223372036854775808", StringComparison.Ordinal);
            case "mismatched-id": target["installationId"] = OtherId.ToString("D"); break;
            case "duplicate-key": return node.ToJsonString().Insert(1, "\"schemaVersion\":1,");
            case "duplicate-nested-key": return node.ToJsonString().Replace("\"ownership\":", "\"ownership\":\"created-by-igloo\",\"ownership\":", StringComparison.Ordinal);
            case "duplicate-partition-key": return node.ToJsonString().Replace("\"lengthBytes\":", "\"lengthBytes\":1048576,\"lengthBytes\":", StringComparison.Ordinal);
            case "invalid-json": return "{";
            case "array-envelope": return "[]";
        }
        return node.ToJsonString();
    }

    private static GptDiskLayout ChangeLayout(GptDiskLayout disk, string change)
    {
        var existing = disk.Partitions[1];
        var replacement = change switch
        {
            "partition-guid" => existing with { PartitionGuid = OtherId },
            "partition-type" => existing with { GptType = LinuxType },
            "offset" => existing with { OffsetBytes = existing.OffsetBytes + MiB },
            "size" => existing with { LengthBytes = existing.LengthBytes - MiB },
            _ => existing,
        };
        return change switch
        {
            "disk-guid" => disk with { DiskGuid = OtherId },
            "disk-size" => disk with { DiskSizeBytes = disk.DiskSizeBytes + GiB },
            "sector-size" => disk with { LogicalSectorSize = 4096 },
            "missing" => disk with { Partitions = disk.Partitions.Where(p => p.PartitionGuid != existing.PartitionGuid).ToArray() },
            "extra" => disk with { Partitions = [.. disk.Partitions, Partition(OtherId, 72 * GiB, GiB)] },
            _ => disk with { Partitions = disk.Partitions.Select(p => p.PartitionGuid == existing.PartitionGuid ? replacement : p).ToArray() },
        };
    }

    private static GptDiskLayout WithFirst(GptDiskLayout disk, GptPartitionIdentity first) =>
        disk with { Partitions = [first, .. disk.Partitions.Skip(1)] };

    private static GptDiskLayout ReplaceRoot(GptDiskLayout disk, Func<GptPartitionIdentity, GptPartitionIdentity> change) =>
        disk with { Partitions = disk.Partitions.Select(p => p.PartitionGuid == RootId ? change(p) : p).ToArray() };

    private static GptPartitionIdentity Partition(Guid id, long offset, long size) => new()
    {
        PartitionGuid = id, GptType = LinuxType, OffsetBytes = offset, LengthBytes = size,
    };

    private static InstallationTargetRequest Request()
    {
        var claim = FixtureClaim();
        var root = claim.Disk.Partitions.Single(p => p.PartitionGuid == RootId);
        return new InstallationTargetRequest
        {
            InstallationId = InstallationId,
            ExpectedLayout = BeforeCreation(claim.Disk),
            RootOffsetBytes = root.OffsetBytes,
            RootLengthBytes = root.LengthBytes,
            EspPartitionGuid = claim.EspPartitionGuid,
        };
    }

    private static GptDiskLayout BeforeCreation(GptDiskLayout disk) =>
        disk with { Partitions = disk.Partitions.Where(p => p.PartitionGuid != RootId).ToArray() };

    private static InstallationTargetClaim FixtureClaim() =>
        JsonSerializer.Deserialize<MigrationManifest>(FixtureJson())!.InstallationTarget!;

    private static JsonObject FixtureNode() => JsonNode.Parse(FixtureJson())!.AsObject();

    private static string TemporaryPath() => Path.Combine(Path.GetTempPath(), "igloo-target-test-" + Guid.NewGuid().ToString("N") + ".json");

    private static string FixtureJson()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var path = Path.Combine(directory.FullName, "tests", "fixtures", "installation-target.json");
            if (File.Exists(path)) return File.ReadAllText(path);
        }
        throw new FileNotFoundException("Shared installation-target fixture was not found above the test output directory.");
    }
}
