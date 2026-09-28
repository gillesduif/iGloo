using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Igloo.Core.Abstractions;
using Igloo.Core.Execution;
using Igloo.Core.Recovery;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class RecoveryAcquisitionTests
{
    [Fact]
    public void NativeFirmwareAttributesReachTheCanonicalSnapshotWithoutInference()
    {
        var raw = WindowsFirmwareReader.ProjectNativeRead([1, 0, 99], 2, 123, 7);
        var observed = EfiRecoveryParser.Observe(raw);
        Assert.Equal(new byte[] { 1, 0 }, observed.Bytes.Value.ToArray());
        Assert.Equal(7u, observed.VariableAttributes.Value);
        Assert.Equal(0, observed.NativeError); // LastError is undefined on successful native reads.
        var snapshot = CommunityRecoveryFixture.Exact();
        var bytes = snapshot.Firmware.BootOrderRaw.Bytes.Value.ToArray();
        var next = snapshot with { Firmware = snapshot.Firmware with
        { BootOrderRaw = EfiRecoveryParser.Observe(WindowsFirmwareReader.ProjectNativeRead(bytes, (uint)bytes.Length, 0, 7)) } };
        Assert.Equal(BootRecoverySupport.Exact, RecoverySnapshotRules.Assess(RecoverySnapshotSerialization.Seal(next)).Support);
    }

    [Theory]
    [InlineData(5, ObservationAvailability.AccessDenied)]
    [InlineData(1314, ObservationAvailability.AccessDenied)]
    [InlineData(50, ObservationAvailability.Unsupported)]
    [InlineData(1, ObservationAvailability.Unsupported)]
    [InlineData(122, ObservationAvailability.Unavailable)]
    [InlineData(203, ObservationAvailability.Absent)]
    public void FailedFirmwareReadDoesNotPublishTheOutParameter(int error, ObservationAvailability expected)
    {
        var result = EfiRecoveryParser.Observe(WindowsFirmwareReader.ProjectNativeRead([9], 0, error, 7));
        Assert.Equal(expected, result.VariableAttributes.Availability);
        Assert.Equal(expected, result.Bytes.Availability);
        Assert.Throws<InvalidOperationException>(() => result.VariableAttributes.Value);
        Assert.Equal(error, result.NativeError);
    }

    [Fact]
    public void UnknownNativeAttributesAreRetainedButCannotBecomeExact()
    {
        var snapshot = CommunityRecoveryFixture.Exact();
        var bytes = snapshot.Firmware.BootOrderRaw.Bytes.Value.ToArray();
        var raw = EfiRecoveryParser.Observe(WindowsFirmwareReader.ProjectNativeRead(bytes, (uint)bytes.Length, 0, 0x27));
        Assert.Equal(0x27u, raw.VariableAttributes.Value);
        Assert.Equal(BootRecoverySupport.Unsupported, RecoverySnapshotRules.Assess(RecoverySnapshotSerialization.Seal(snapshot with
        { Firmware = snapshot.Firmware with { BootOrderRaw = raw } })).Support);
    }

    [Fact]
    public void LegacyFirmwareProviderStillHasUnsupportedAttributes()
    {
        var raw = EfiRecoveryParser.Observe(new([0, 0], 0));
        Assert.Equal(ObservationAvailability.Unsupported, raw.VariableAttributes.Availability);
        Assert.Throws<InvalidOperationException>(() => raw.VariableAttributes.Value);
    }

    [Theory]
    [InlineData("fr-BE")]
    [InlineData("ar-SA")]
    public void WinReConfigurationUsesTypedXmlIndependentOfDisplayLanguage(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var fixture = CommunityRecoveryFixture.Exact();
            var source = Xml(Configuration(fixture));
            var parsed = WinReConfigurationParser.Parse(source);
            Assert.Equal(ObservationAvailability.Available, parsed.Availability);
            Assert.Equal(1u, parsed.Value.InstallState);
            Assert.Equal(fixture.WinRe.RecoveryLoaderId.Value, parsed.Value.RecoveryLoaderId.Value);
            Assert.Equal(source.Value.ToArray(), parsed.Value.RawXml.ToArray());
            var reader = new WindowsWinReReader(() => source, (volume, path) =>
            {
                Assert.Equal(fixture.WinRe.Image.Value.VolumeGuid, volume);
                Assert.Equal(fixture.WinRe.Image.Value.RelativePath, path);
                return fixture.WinRe.Image;
            });
            var observed = reader.Capture(A(Inventory(fixture.WinRe.RecoveryVolume.Value)));
            Assert.Equal(fixture.WinRe.RecoveryVolume.Value, observed.RecoveryVolume.Value);
            Assert.Equal(ObservationAvailability.Unsupported, observed.Enabled.Availability);
            Assert.Equal("WinReInstallStateSemanticsUnproven", observed.Enabled.Code);
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Theory]
    [InlineData(ObservationAvailability.Absent, ObservationAvailability.Unavailable)]
    [InlineData(ObservationAvailability.AccessDenied, ObservationAvailability.AccessDenied)]
    [InlineData(ObservationAvailability.Unsupported, ObservationAvailability.Unsupported)]
    [InlineData(ObservationAvailability.Unavailable, ObservationAvailability.Unavailable)]
    public void MissingOrFailedConfigurationDoesNotMeanWinReDisabled(ObservationAvailability sourceState, ObservationAvailability configuredState)
    {
        var reader = new WindowsWinReReader(() => F<ImmutableArray<byte>>(sourceState), (_, _) => throw new InvalidOperationException("Must not read image"));
        Assert.Equal(sourceState, reader.ReadConfiguration().Availability);
        var result = reader.Capture(F<WindowsStorageSnapshot>(ObservationAvailability.Unavailable));
        Assert.Equal(configuredState, result.Enabled.Availability);
        Assert.Throws<InvalidOperationException>(() => result.Enabled.Value);
    }

    [Fact]
    public void ExplicitlyEmptyConfiguredLocationIsAbsentButDoesNotInferDisabled()
    {
        var source = Xml("<WindowsRE version=\"2.0\"><WinreBCD id=\"\"/><InstallState state=\"0\"/><WinreLocation path=\"\" guid=\"00000000-0000-0000-0000-000000000000\" offset=\"0\"/></WindowsRE>");
        var parsed = WinReConfigurationParser.Parse(source);
        Assert.Equal(ObservationAvailability.Absent, parsed.Value.Location.Availability);
        Assert.Equal(ObservationAvailability.Absent, parsed.Value.RecoveryLoaderId.Availability);
        var reader = new WindowsWinReReader(() => source, (_, _) => throw new InvalidOperationException());
        var result = reader.Capture(F<WindowsStorageSnapshot>(ObservationAvailability.Unavailable));
        Assert.Equal(ObservationAvailability.Absent, result.RecoveryVolume.Availability);
        Assert.Equal(ObservationAvailability.Unsupported, result.Enabled.Availability);
    }

    [Theory]
    [InlineData("duplicate")]
    [InlineData("badnumber")]
    [InlineData("traversal")]
    [InlineData("dtd")]
    public void AmbiguousOrUnsafeWinReXmlCannotBecomeConfiguration(string kind)
    {
        var xml = Configuration(CommunityRecoveryFixture.Exact());
        xml = kind switch
        {
            "duplicate" => xml.Replace("</WindowsRE>", "<InstallState state=\"0\"/></WindowsRE>", StringComparison.Ordinal),
            "badnumber" => xml.Replace("state=\"1\"", "state=\"1,0\"", StringComparison.Ordinal),
            "traversal" => xml.Replace(@"\Recovery\WindowsRE", @"\..\Windows", StringComparison.Ordinal),
            _ => "<!DOCTYPE WindowsRE [<!ENTITY x SYSTEM 'file:///not-read'>]>" + xml,
        };
        Assert.Equal(ObservationAvailability.Ambiguous, WinReConfigurationParser.Parse(Xml(xml)).Availability);
    }

    [Fact]
    public void FutureWinReSchemaIsUnsupported()
    {
        var xml = Configuration(CommunityRecoveryFixture.Exact()).Replace("version=\"2.0\"", "version=\"3.0\"", StringComparison.Ordinal);
        Assert.Equal(ObservationAvailability.Unsupported, WinReConfigurationParser.Parse(Xml(xml)).Availability);
    }

    [Theory]
    [InlineData(2u)]
    [InlineData(uint.MaxValue)]
    public void UninterpretedInstallStateIsRetainedWithoutCertifyingEnabled(uint state)
    {
        var fixture = CommunityRecoveryFixture.Exact();
        var xml = Configuration(fixture).Replace("state=\"1\"", "state=\"" + state.ToString(CultureInfo.InvariantCulture) + "\"", StringComparison.Ordinal);
        var reader = new WindowsWinReReader(() => Xml(xml), (_, _) => fixture.WinRe.Image);
        Assert.Equal(state, reader.ReadConfiguration().Value.InstallState);
        var observed = reader.Capture(A(Inventory(fixture.WinRe.RecoveryVolume.Value)));
        Assert.Equal(ObservationAvailability.Unsupported, observed.Enabled.Availability);
        Assert.Throws<InvalidOperationException>(() => observed.Enabled.Value);
    }

    [Fact]
    public void ObservedQualifiedRamdiskProviderFailureRemainsUnavailable()
    {
        var error = System.Runtime.InteropServices.Marshal.GetExceptionForHR(unchecked((int)0xd000000d), new IntPtr(-1))!;
        Assert.Equal(ObservationAvailability.Unavailable, WindowsStorageReader.ClassifyError(error));
        Assert.Equal(unchecked((int)0xd000000d), error.HResult);
    }

    [Fact]
    public void WinReLocationCannotChooseBetweenDuplicatePartitions()
    {
        var fixture = CommunityRecoveryFixture.Exact();
        var inventory = Inventory(fixture.WinRe.RecoveryVolume.Value);
        var location = WinReConfigurationParser.Parse(Xml(Configuration(fixture))).Value.Location;
        var duplicate = inventory with { Partitions = inventory.Partitions.Add(inventory.Partitions[0]) };
        Assert.Equal(ObservationAvailability.Ambiguous, WindowsWinReReader.CorrelateLocation(A(duplicate), location).Availability);
        Assert.Equal(ObservationAvailability.Unavailable, WindowsWinReReader.CorrelateLocation(A(inventory with { Partitions = [] }), location).Availability);
    }

    [Fact]
    public void FullyQualifiedNestedRamdiskRetainsEveryParentAndOptionsIdentity()
    {
        var volume = CommunityRecoveryFixture.Exact().WinRe.RecoveryVolume.Value;
        var options = Guid.NewGuid();
        var raw = Row("BcdDeviceFileData", 4, ("Path", @"\Recovery\WindowsRE\Winre.wim"), ("AdditionalOptions", options.ToString("B")),
            ("Parent", Row("BcdDeviceQualifiedPartitionData", 6, ("PartitionStyle", 1u),
                ("DiskSignature", volume.Disk.GptDiskGuid.ToString("B")), ("PartitionIdentifier", volume.PartitionGuid.ToString("B")))));
        var file = Assert.IsType<BcdFileDevice>(WindowsBcdReader.ProjectDeviceEvidence(raw));
        var parent = Assert.IsType<BcdQualifiedGptPartitionDevice>(file.Parent);
        Assert.Equal(volume.Disk.GptDiskGuid, parent.DiskId);
        Assert.Equal(volume.PartitionGuid, parent.PartitionId);
        Assert.Equal(options, file.AdditionalOptions);
        Assert.Equal(4u, file.Kind);
    }

    [Theory]
    [InlineData(ObservationAvailability.AccessDenied)]
    [InlineData(ObservationAvailability.Unsupported)]
    [InlineData(ObservationAvailability.Unavailable)]
    [InlineData(ObservationAvailability.Ambiguous)]
    public void WinReCorrelationPreservesFailedIdentityObservations(ObservationAvailability state)
    {
        var fixture = CommunityRecoveryFixture.Exact();
        var inventory = Inventory(fixture.WinRe.RecoveryVolume.Value);
        var location = WinReConfigurationParser.Parse(Xml(Configuration(fixture))).Value.Location;
        Assert.Equal(state, WindowsWinReReader.CorrelateLocation(A(inventory with
        { Disks = [inventory.Disks[0] with { GptGuid = F<Guid>(state) }] }), location).Availability);
        Assert.Equal(state, WindowsWinReReader.CorrelateLocation(A(inventory with
        { Partitions = [inventory.Partitions[0] with { Offset = F<ulong>(state) }] }), location).Availability);
        Assert.Equal(state, WindowsWinReReader.CorrelateLocation(A(inventory with
        { Volumes = [inventory.Volumes[0] with { PartitionGuid = F<Guid>(state) }] }), location).Availability);
    }

    [Theory]
    [InlineData("opaque")]
    [InlineData("native")]
    [InlineData("wrongkind")]
    public void UnresolvedRamdiskParentNeverAcquiresGuessedGptIdentity(string kind)
    {
        var parent = kind switch
        {
            "native" => Row("BcdDevicePartitionData", 2, ("Path", @"\Device\HarddiskVolume4")),
            "wrongkind" => Row("BcdDeviceQualifiedPartitionData", 5, ("PartitionStyle", 1u), ("Data", new byte[] { 1, 2, 3 })),
            _ => Row("BcdDeviceUnknownData", 5, ("Data", new byte[] { 1, 2, 3 })),
        };
        var file = Assert.IsType<BcdFileDevice>(WindowsBcdReader.ProjectDeviceEvidence(Row("BcdDeviceFileData", 4, ("Path", @"\Winre.wim"), ("Parent", parent))));
        Assert.IsNotType<BcdQualifiedGptPartitionDevice>(file.Parent);
        var id = Guid.NewGuid();
        var graph = new BcdRecoveryGraphV1(1, A<ImmutableArray<BcdObjectSnapshot>>([new(id, BcdRecoveryRoles.WindowsLoaderType,
            A<ImmutableArray<BcdElementSnapshot>>([new(0x11000001, A<BcdElementValue>(new BcdDeviceElementValue(file)))]))]), A(id));
        Assert.False(BcdDependencyAnalysis.Analyze(graph, [id]).CanRepresentExactly);
        if (file.Parent is BcdOpaqueDevice opaque) Assert.Equal(new byte[] { 1, 2, 3 }, opaque.RawData.Value.ToArray());
    }

    [Fact]
    public void OnlyEmptyOptionalDataHasProvenDependencySemantics()
    {
        Assert.True(EfiRecoveryParser.OptionalDataDependenciesKnown([]).Value);
        Assert.Equal(ObservationAvailability.Unsupported, EfiRecoveryParser.OptionalDataDependenciesKnown(Encoding.Unicode.GetBytes("BCDOBJECT={9dea862c-5cdd-4e70-acc1-f32b344d4795}").ToImmutableArray()).Availability);
        Assert.Equal(ObservationAvailability.Unsupported, EfiRecoveryParser.OptionalDataDependenciesKnown([0xff, 0, 3]).Availability);
        Assert.Equal(ObservationAvailability.Ambiguous, EfiRecoveryParser.OptionalDataDependenciesKnown(default).Availability);
    }

    [Fact]
    public void TwoIndependentCompleteCapturesCompareExactly()
    {
        var fixture = CommunityRecoveryFixture.Exact(); var calls = 0;
        var capture = new WindowsRecoverySnapshotCapture((_, _) =>
        { calls++; return RecoverySnapshotSerialization.Deserialize(RecoverySnapshotSerialization.Serialize(fixture)); });
        var result = capture.Capture(fixture.Scope, fixture.Binding.Value.TargetVolume.VolumeGuid);
        Assert.Equal(2, calls); Assert.True(result.IndependentReadback.Value);
        Assert.Equal(RecoverySnapshotMatch.ExactMatch, RecoverySnapshotRules.Compare(fixture, result).Result);
    }

    [Fact]
    public void ChangedDependencyDuringRecaptureCannotBecomeExact()
    {
        var fixture = CommunityRecoveryFixture.Exact(); var calls = 0;
        var changed = RecoverySnapshotSerialization.Seal(fixture with { WinRe = fixture.WinRe with
        { Image = A(fixture.WinRe.Image.Value with { Sha256 = new string('B', 64) }) } });
        Assert.Equal(RecoverySnapshotMatch.Changed, RecoverySnapshotRules.Compare(fixture, changed).Result);
        var capture = new WindowsRecoverySnapshotCapture((_, _) => ++calls == 1 ? fixture : changed);
        var result = capture.Capture(fixture.Scope, fixture.Binding.Value.TargetVolume.VolumeGuid);
        Assert.False(result.IndependentReadback.Value);
        Assert.NotEqual(BootRecoverySupport.Exact, RecoverySnapshotRules.Assess(result).Support);
    }

    [Theory]
    [InlineData(ObservationAvailability.Absent)]
    [InlineData(ObservationAvailability.Unavailable)]
    [InlineData(ObservationAvailability.AccessDenied)]
    [InlineData(ObservationAvailability.Unsupported)]
    [InlineData(ObservationAvailability.Ambiguous)]
    public void MissingOrFailedDependencyDuringRecaptureRetainsItsState(ObservationAvailability state)
    {
        var fixture = CommunityRecoveryFixture.Exact(); var calls = 0;
        var changed = RecoverySnapshotSerialization.Seal(fixture with { WinRe = fixture.WinRe with { Image = F<CanonicalFileIdentityV1>(state) } });
        var expected = state switch
        {
            ObservationAvailability.Absent => RecoverySnapshotMatch.Missing,
            ObservationAvailability.Unsupported => RecoverySnapshotMatch.Unsupported,
            ObservationAvailability.Ambiguous => RecoverySnapshotMatch.Ambiguous,
            _ => RecoverySnapshotMatch.ObservationUnavailable,
        };
        Assert.Equal(expected, RecoverySnapshotRules.Compare(fixture, changed).Result);
        var capture = new WindowsRecoverySnapshotCapture((_, _) => ++calls == 1 ? fixture : changed);
        var result = capture.Capture(fixture.Scope, fixture.Binding.Value.TargetVolume.VolumeGuid);
        Assert.Equal(state, result.IndependentReadback.Availability);
        Assert.NotEqual(BootRecoverySupport.Exact, RecoverySnapshotRules.Assess(result).Support);
    }

    [Theory]
    [InlineData(ObservationAvailability.Unavailable)]
    [InlineData(ObservationAvailability.AccessDenied)]
    [InlineData(ObservationAvailability.Unsupported)]
    public void ConfiguredPartitionAccessPathExcludesADifferentKnownVolume(ObservationAvailability unrelatedOwnerState)
    {
        var fixture = CommunityRecoveryFixture.Exact();
        var inventory = InventoryWithOtherVolume(fixture, unrelatedOwnerState);
        var location = WinReConfigurationParser.Parse(Xml(Configuration(fixture))).Value.Location;
        var bound = WindowsWinReReader.CorrelateLocation(A(inventory), location);
        Assert.Equal(fixture.WinRe.RecoveryVolume.Value, bound.Value);
        Assert.Equal(unrelatedOwnerState, inventory.Volumes[1].PartitionGuid.Availability);
    }

    [Theory]
    [InlineData("duplicate-volume", ObservationAvailability.Ambiguous)]
    [InlineData("duplicate-owner", ObservationAvailability.Ambiguous)]
    [InlineData("missing-selected", ObservationAvailability.Unavailable)]
    [InlineData("unknown-volume-id", ObservationAvailability.AccessDenied)]
    [InlineData("unknown-access-paths", ObservationAvailability.AccessDenied)]
    [InlineData("wrong-selected-owner", ObservationAvailability.Ambiguous)]
    [InlineData("denied-selected-owner", ObservationAvailability.AccessDenied)]
    [InlineData("multiple-volume-paths", ObservationAvailability.Ambiguous)]
    [InlineData("letter-only", ObservationAvailability.Unavailable)]
    [InlineData("malformed-volume-path", ObservationAvailability.Ambiguous)]
    public void AccessPathCorrelationRequiresCompleteUniqueEvidence(string defect, ObservationAvailability expected)
    {
        var fixture = CommunityRecoveryFixture.Exact();
        var inventory = InventoryWithOtherVolume(fixture, ObservationAvailability.Unavailable);
        var selected = inventory.Volumes[0]; var other = inventory.Volumes[1]; var partition = inventory.Partitions[0];
        inventory = defect switch
        {
            "duplicate-volume" => inventory with { Volumes = [selected, other, selected] },
            "duplicate-owner" => inventory with { Partitions = [partition, partition with { Offset = A(partition.Offset.Value + 4096) }] },
            "missing-selected" => inventory with { Volumes = [other] },
            "unknown-volume-id" => inventory with { Volumes = [selected, other with { VolumeGuid = F<Guid>(ObservationAvailability.AccessDenied) }] },
            "unknown-access-paths" => inventory with { Partitions = [partition with { AccessPaths = F<ImmutableArray<string>>(ObservationAvailability.AccessDenied) }] },
            "wrong-selected-owner" => inventory with { Volumes = [selected with { PartitionGuid = A(Guid.NewGuid()) }, other] },
            "denied-selected-owner" => inventory with { Volumes = [selected with { PartitionGuid = F<Guid>(ObservationAvailability.AccessDenied) }, other] },
            "multiple-volume-paths" => inventory with { Partitions = [partition with { AccessPaths = A(partition.AccessPaths.Value.Add(VolumePath(other.VolumeGuid.Value))) }] },
            "malformed-volume-path" => inventory with { Partitions = [partition with { AccessPaths = A(partition.AccessPaths.Value.Add(@"\\?\Volume{invalid}\")) }] },
            _ => inventory with { Partitions = [partition with { AccessPaths = A<ImmutableArray<string>>([@"R:\"]) }] },
        };
        var location = WinReConfigurationParser.Parse(Xml(Configuration(fixture))).Value.Location;
        Assert.Equal(expected, WindowsWinReReader.CorrelateLocation(A(inventory), location).Availability);
    }

    [Fact]
    public void ObservedWindowsOptionalPayloadStillPreventsExact()
    {
        var payload = Convert.FromHexString("57494E444F5753000100000088000000780000004200430044004F0042004A004500430054003D007B00390064006500610038003600320063002D0035006300640064002D0034006500370030002D0061006300630031002D006600330032006200330034003400640034003700390035007D00000064000100000010000000040000007FFF0400").ToImmutableArray();
        Assert.Equal(136, payload.Length);
        var fixture = CommunityRecoveryFixture.Exact();
        var entry = fixture.Firmware.BootEntries.Single(e => e.Index == fixture.Scope.WindowsBootEntry);
        Assert.Empty(entry.LoadOption.Value.OptionalData);
        var raw = entry.Raw with { Bytes = A(entry.Raw.Bytes.Value.AddRange(payload)) };
        var replacement = EfiRecoveryParser.ParseBootEntry(entry.Index, raw);
        Assert.Equal(payload.ToArray(), replacement.LoadOption.Value.OptionalData.ToArray());
        var snapshot = RecoverySnapshotSerialization.Seal(fixture with { Firmware = fixture.Firmware with
        { BootEntries = fixture.Firmware.BootEntries.Replace(entry, replacement) } });
        Assert.Equal(BootRecoverySupport.Unsupported, RecoverySnapshotRules.Assess(snapshot).Support);
        var reopened = RecoverySnapshotSerialization.Deserialize(RecoverySnapshotSerialization.Serialize(snapshot));
        var recoveredEntry = reopened.Firmware.BootEntries.Single(e => e.Index == entry.Index);
        Assert.Equal(raw.Bytes.Value.ToArray(), recoveredEntry.Raw.Bytes.Value.ToArray());
        Assert.Equal(payload.ToArray(), recoveredEntry.LoadOption.Value.OptionalData.ToArray());
        Assert.True(RecoverySnapshotSerialization.VerifyHash(reopened));
        Assert.Contains(RecoverySnapshotRules.Assess(reopened).Evidence, e =>
            e.Identity == "Boot0000" && e.Relevance == RecoveryRelevance.RelevantOpaque);
        Assert.Equal(RecoverySnapshotMatch.Unsupported, RecoverySnapshotRules.Compare(fixture, reopened).Result);
    }

    [Fact]
    public void QualifiedSdiCannotSubstituteForFailedActiveRamdiskQualification()
    {
        var fixture = CommunityRecoveryFixture.Exact();
        var objects = fixture.BootConfiguration.Objects.Value;
        var loader = objects.Single(o => o.Id == fixture.WinRe.RecoveryLoaderId.Value);
        var elements = loader.Elements.Value.Select(element =>
        {
            if (element.Value.Value is not BcdDeviceElementValue { Device: BcdFileDevice file }) return element;
            return element with
            {
                Value = A<BcdElementValue>(new BcdDeviceElementValue(file with { Parent = new BcdPartitionDevice(@"\Device\HarddiskVolume4") })),
                QualifiedDevice = Observations.Failure<BcdDeviceValue>(ObservationAvailability.Unavailable, "BcdQualifiedDeviceUnavailable"),
            };
        }).ToImmutableArray();
        var snapshot = RecoverySnapshotSerialization.Seal(fixture with { BootConfiguration = fixture.BootConfiguration with
        { Objects = A(objects.Replace(loader, loader with { Elements = A(elements) })) } });
        var result = RecoverySnapshotRules.Assess(snapshot);
        Assert.NotEqual(BootRecoverySupport.Exact, result.Support);
        Assert.Contains(result.Issues, i => i.Code == RecoverySnapshotIssueCode.BcdDependency && i.Availability == ObservationAvailability.Unavailable);
        Assert.Equal(RecoverySnapshotMatch.ObservationUnavailable, RecoverySnapshotRules.Compare(fixture, snapshot).Result);
    }

    private static WindowsStorageSnapshot InventoryWithOtherVolume(RecoverySnapshotV1 fixture, ObservationAvailability ownerState)
    {
        var inventory = Inventory(fixture.WinRe.RecoveryVolume.Value);
        return inventory with
        {
            Partitions = [inventory.Partitions[0] with { AccessPaths = A<ImmutableArray<string>>([VolumePath(fixture.WinRe.RecoveryVolume.Value.VolumeGuid), @"R:\"]) }],
            Volumes = [inventory.Volumes[0], inventory.Volumes[0] with { VolumeGuid = A(Guid.NewGuid()), PartitionGuid = F<Guid>(ownerState) }],
        };
    }
    private static string VolumePath(Guid id) => @"\\?\Volume{" + id.ToString("D") + @"}\";

    private static WindowsStorageRow Row(string name, uint kind, params (string Key, object Value)[] values)
    {
        var dictionary = new Dictionary<string, object?> { ["__CLASS"] = name, ["DeviceType"] = kind, ["AdditionalOptions"] = "" };
        foreach (var (key, value) in values) dictionary[key] = value;
        return new(dictionary.ToImmutableDictionary());
    }
    private static string Configuration(RecoverySnapshotV1 fixture) =>
        $"<WindowsRE version=\"2.0\"><WinreBCD id=\"{fixture.WinRe.RecoveryLoaderId.Value:B}\"/><InstallState state=\"1\"/>" +
        $"<WinreLocation path=\"\\Recovery\\WindowsRE\" guid=\"{fixture.WinRe.RecoveryVolume.Value.Disk.GptDiskGuid:B}\" offset=\"{fixture.WinRe.RecoveryVolume.Value.OffsetBytes.ToString(CultureInfo.InvariantCulture)}\"/></WindowsRE>";
    private static Observation<ImmutableArray<byte>> Xml(string value) => A(Encoding.UTF8.GetBytes(value).ToImmutableArray());
    private static Observation<T> A<T>(T value) => Observations.Available(value);
    private static Observation<T> F<T>(ObservationAvailability state) => Observations.Failure<T>(state, "fixture");
    private static WindowsStorageSnapshot Inventory(CanonicalVolumeIdentityV1 volume) => new(
        [new(A(3u), A(volume.Disk.UniqueId), A(volume.Disk.UniqueIdFormat), A("metadata"), A(volume.Disk.BusType), A("metadata"), A(volume.Disk.SizeBytes), A(2u),
            A(volume.Disk.GptDiskGuid), A(volume.Disk.LogicalSectorSize), A(volume.Disk.PhysicalSectorSize))],
        [new(A(3u), A(7u), A(volume.PartitionGuid), A(volume.PartitionType), A(volume.OffsetBytes), A(volume.SizeBytes),
            A(false), A(false), A(false), F<char>(ObservationAvailability.Absent), A<ImmutableArray<string>>([]))],
        [new(A(volume.VolumeGuid), A(volume.PartitionGuid), A(volume.FileSystem), A("metadata"), F<char>(ObservationAvailability.Absent), A("OK"), A(false))]);
}
