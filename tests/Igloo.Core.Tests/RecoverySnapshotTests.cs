using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Igloo.Core.Abstractions;
using Igloo.Core.Execution;
using Igloo.Core.Recovery;
using Xunit;

namespace Igloo.Core.Tests;

public sealed class RecoverySnapshotTests
{
    private static readonly Guid LoaderId = new("aaaaaaaa-bbbb-cccc-dddd-111111111111");
    private static readonly Guid RecoveryId = new("aaaaaaaa-bbbb-cccc-dddd-222222222222");
    private static readonly Guid ResumeId = new("aaaaaaaa-bbbb-cccc-dddd-333333333333");
    private static readonly Guid OptionsId = new("aaaaaaaa-bbbb-cccc-dddd-444444444444");
    private static readonly Guid UnrelatedId = new("aaaaaaaa-bbbb-cccc-dddd-555555555555");

    [Fact]
    public void FullyRepresentedSyntheticScopeIsExactButProvidesNoExecutionCapability()
    {
        var snapshot = Fixture();
        var result = RecoverySnapshotRules.Assess(snapshot);
        Assert.True(result.Support == BootRecoverySupport.Exact, string.Join(";", result.Issues));
        Assert.Empty(result.Issues);
        Assert.Equal(RecoverySnapshotMatch.ExactMatch, RecoverySnapshotRules.Compare(snapshot, snapshot).Result);
        Assert.Equal(WinReImageAssurance.ContentIdentityOnly, snapshot.WinRe.ImageAssurance);
    }

    [Fact]
    public void CanonicalSerializationIsDeterministicAndRoundTripsEveryRawBootByte()
    {
        var snapshot = Fixture();
        var bytes = RecoverySnapshotSerialization.Serialize(snapshot);
        Assert.Equal(bytes, RecoverySnapshotSerialization.Serialize(snapshot));
        var reopened = RecoverySnapshotSerialization.Deserialize(bytes);
        Assert.Equal(snapshot.CanonicalHash, reopened.CanonicalHash);
        Assert.True(RecoverySnapshotSerialization.VerifyHash(reopened));
        Assert.Equal(bytes, RecoverySnapshotSerialization.Serialize(reopened));
        Assert.Equal(snapshot.Firmware.BootEntries[0].Raw.Bytes.Value.ToArray(), reopened.Firmware.BootEntries[0].Raw.Bytes.Value.ToArray());
        Assert.Equal(BootRecoverySupport.Exact, RecoverySnapshotRules.Assess(reopened).Support);
    }

    [Fact]
    public void ObjectElementAndEntryCollectionOrderingDoesNotChangeSerializationOrHash()
    {
        var a = Fixture();
        var b = Seal(a with
        {
            BootConfiguration = a.BootConfiguration with { Objects = A(a.BootConfiguration.Objects.Value.Reverse().Select(o =>
                o with { Elements = A(o.Elements.Value.Reverse().ToImmutableArray()) }).ToImmutableArray()) },
            Firmware = a.Firmware with { BootEntries = a.Firmware.BootEntries.Reverse().ToImmutableArray() },
        });
        Assert.Equal(a.CanonicalHash, b.CanonicalHash);
        Assert.Equal(RecoverySnapshotSerialization.Serialize(a), RecoverySnapshotSerialization.Serialize(b));
    }

    [Fact]
    public void GuidEncodingIsLowercaseDFormatAndRawBytesUseBase64()
    {
        var s = Fixture();
        var json = Encoding.UTF8.GetString(RecoverySnapshotSerialization.Serialize(s));
        Assert.Contains(LoaderId.ToString("D"), json, StringComparison.Ordinal);
        Assert.DoesNotContain(LoaderId.ToString("B").ToUpperInvariant(), json, StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(s.Firmware.BootEntries[0].Raw.Bytes.Value.AsSpan()), json, StringComparison.Ordinal);
    }

    [Fact]
    public void TimeLocatorsAndDiagnosticsAreNotSemanticAuthority()
    {
        var a = Fixture();
        var b = Seal(a with { CapturedAtUtc = a.CapturedAtUtc.AddDays(1), Metadata = new(
            [new(a.Binding.Value.WindowsVolume.VolumeGuid, 99, 88, 'Z')], ["DifferentDiagnostic"]) });
        Assert.Equal(a.CanonicalHash, b.CanonicalHash);
        Assert.Equal(RecoverySnapshotMatch.ExactMatch, RecoverySnapshotRules.Compare(a, b).Result);
    }

    [Fact]
    public void BootOrderSequenceIsStateEvenThoughEntryCollectionOrderIsNot()
    {
        var a = Fixture();
        var raw = Variable([2, 0, 0, 0]);
        var b = Seal(a with { Firmware = a.Firmware with { BootOrderRaw = raw, BootOrder = EfiRecoveryParser.ParseBootOrder(raw) } });
        Assert.Equal(RecoverySnapshotMatch.Changed, RecoverySnapshotRules.Compare(a, b).Result);
    }

    [Fact]
    public void BootNextAbsentAndPresentAreDistinctExactStates()
    {
        var a = Fixture();
        var raw = Variable([0, 0]);
        var b = Seal(a with { Firmware = a.Firmware with { BootNextRaw = raw, BootNext = EfiRecoveryParser.ParseBootNext(raw) } });
        Assert.Equal(BootRecoverySupport.Exact, RecoverySnapshotRules.Assess(b).Support);
        Assert.NotEqual(a.CanonicalHash, b.CanonicalHash);
        Assert.Equal(RecoverySnapshotMatch.Changed, RecoverySnapshotRules.Compare(a, b).Result);
    }

    [Fact]
    public void UnrelatedOpaqueBcdAndUnsupportedFirmwareAreRetainedAndExplicitlyExcluded()
    {
        var a = Fixture();
        var assessment = RecoverySnapshotRules.Assess(a);
        Assert.Equal(BootRecoverySupport.Exact, assessment.Support);
        Assert.Contains(assessment.Evidence, e => e.Identity == UnrelatedId.ToString("D") && e.Relevance == RecoveryRelevance.ObservedUnrelated);
        Assert.Contains(assessment.Evidence, e => e.Identity == "Boot0002" && e.Relevance == RecoveryRelevance.ObservedUnrelated);
        var b = ReplaceObject(a, UnrelatedId, Obj(UnrelatedId, E(0x18000001, new BcdOpaqueValue("different", A<ImmutableArray<byte>>([1, 9, 8])))));
        Assert.Equal(a.CanonicalHash, b.CanonicalHash);
        Assert.NotEqual(RecoverySnapshotSerialization.Serialize(a), RecoverySnapshotSerialization.Serialize(b));
    }

    [Fact]
    public void ExplicitMutationRootMakesFormerlyUnrelatedOpaqueBcdRequired()
    {
        var a = Fixture();
        var b = Seal(a with { Scope = a.Scope with { BcdMutationObjects = [UnrelatedId] } });
        var result = RecoverySnapshotRules.Assess(b);
        Assert.Equal(BootRecoverySupport.Unsupported, result.Support);
        Assert.Contains(result.Evidence, e => e.Identity == UnrelatedId.ToString("D") && e.Relevance == RecoveryRelevance.RelevantOpaque);
    }

    [Fact]
    public void RequiredUnsupportedEfiShapeBlocksExactWithoutInventingIdentity()
    {
        var a = Fixture();
        var b = Seal(a with { Scope = a.Scope with { FirmwareMutationEntries = [2] }, Firmware = a.Firmware with { RequiredBootEntries = [0, 2] } });
        Assert.Equal(BootRecoverySupport.Unsupported, RecoverySnapshotRules.Assess(b).Support);
        Assert.Equal(ObservationAvailability.Unsupported, b.Firmware.BootEntries[1].LoadOption.Value.GptFilePath.Availability);
    }

    [Fact]
    public void ActiveBcdClosureIncludesRecoveryResumeAndDeviceAdditionalOptions()
    {
        var a = Fixture();
        var closure = BcdDependencyAnalysis.Analyze(a.BootConfiguration,
            [BcdRecoveryGraphV1.WindowsBootManagerId, BcdRecoveryGraphV1.FirmwareBootManagerId, LoaderId]);
        Assert.True(closure.CanRepresentExactly);
        Assert.Contains(RecoveryId, closure.RequiredObjectIds);
        Assert.Contains(ResumeId, closure.RequiredObjectIds);
        Assert.Contains(OptionsId, closure.RequiredObjectIds);
        Assert.Contains(UnrelatedId, closure.ExcludedObjectIds);
    }

    [Fact]
    public void QualifiedGptIdentitySupersedesTransientNativeLocatorInSemanticHash()
    {
        var a = Fixture();
        RecoverySnapshotV1 WithPath(string path) => ChangeElement(a, LoaderId, 0x11000001,
            new(0x11000001, A<BcdElementValue>(new BcdDeviceElementValue(new BcdPartitionDevice(path))),
                A<BcdDeviceValue>(Gpt(a.Binding.Value.WindowsVolume))));
        var first = WithPath(@"\Device\HarddiskVolume3");
        var second = WithPath(@"\Device\HarddiskVolume99");
        Assert.Equal(BootRecoverySupport.Exact, RecoverySnapshotRules.Assess(first).Support);
        Assert.Equal(first.CanonicalHash, second.CanonicalHash);
    }

    [Fact]
    public void RelevantOpaqueDeviceBytesRemainBlockingEvenWithQualifiedIdentity()
    {
        var a = Fixture();
        var b = ChangeElement(a, LoaderId, 0x11000001, new(0x11000001,
            A<BcdElementValue>(new BcdDeviceElementValue(new BcdOpaqueDevice(5, A<ImmutableArray<byte>>([1, 2]), "unknown"))), A<BcdDeviceValue>(Gpt(a.Binding.Value.WindowsVolume))));
        Assert.Equal(BootRecoverySupport.Unsupported, RecoverySnapshotRules.Assess(b).Support);
    }

    [Fact]
    public void FailedQualifiedRamdiskObservationIsRetainedAndCannotBecomeExact()
    {
        var a = Fixture();
        var original = a.BootConfiguration.Objects.Value.Single(o => o.Id == RecoveryId).Elements.Value[0];
        var b = ChangeElement(a, RecoveryId, original.Type, original with { QualifiedDevice = F<BcdDeviceValue>(ObservationAvailability.Unavailable) });
        Assert.Equal(RecoverySnapshotMatch.ObservationUnavailable, RecoverySnapshotRules.Compare(a, b).Result);
    }

    [Fact]
    public void OpaqueRequiredElementPreventsExact()
    {
        var a = Fixture();
        var b = AddElement(a, LoaderId, E(0x18000001, new BcdOpaqueValue("Unknown", A<ImmutableArray<byte>>([4, 5, 6]))));
        Assert.Equal(BootRecoverySupport.Unsupported, RecoverySnapshotRules.Assess(b).Support);
    }

    [Fact]
    public void WrongWinReVolumeCannotSubstituteForConfiguredImageAssociation()
    {
        var a = Fixture();
        var b = Seal(a with { WinRe = a.WinRe with { RecoveryVolume = A(a.Binding.Value.WindowsVolume) } });
        Assert.Equal(RecoverySnapshotMatch.Ambiguous, RecoverySnapshotRules.Compare(a, b).Result);
    }

    [Fact]
    public void HashIdentityOfWinReImageDoesNotClaimBootability()
    {
        var a = Fixture();
        var b = Seal(a with { WinRe = a.WinRe with { Image = A(a.WinRe.Image.Value with { Sha256 = new string('B', 64) }) } });
        Assert.Equal(WinReImageAssurance.ContentIdentityOnly, b.WinRe.ImageAssurance);
        Assert.Equal(RecoverySnapshotMatch.Changed, RecoverySnapshotRules.Compare(a, b).Result);
    }

    [Fact]
    public void DisabledKnownWinReStateCanBeExactWithoutBeingRecoveryReady()
    {
        var a = Fixture();
        var b = Seal(a with { WinRe = new(1, A(false), F<Guid>(ObservationAvailability.Absent),
            F<CanonicalVolumeIdentityV1>(ObservationAvailability.Absent), F<CanonicalFileIdentityV1>(ObservationAvailability.Absent)) });
        var loader = b.BootConfiguration.Objects.Value.Single(o => o.Id == LoaderId);
        b = ReplaceObject(b, LoaderId, loader with { Elements = A(loader.Elements.Value.Where(e => e.Type != 0x14000008).ToImmutableArray()) });
        Assert.Equal(BootRecoverySupport.Exact, RecoverySnapshotRules.Assess(b).Support);
    }

    [Fact]
    public void RtcAbsenceAndExactNativeQwordBytesRemainDistinct()
    {
        var a = Fixture();
        var b = Seal(a with { Rtc = new(A(true), A(new RegistryValueV1(11, [1, 0, 0, 0, 0, 0, 0, 0]))) });
        Assert.Equal(BootRecoverySupport.Exact, RecoverySnapshotRules.Assess(b).Support);
        Assert.Equal(RecoverySnapshotMatch.Changed, RecoverySnapshotRules.Compare(a, b).Result);
        var reopened = RecoverySnapshotSerialization.Deserialize(RecoverySnapshotSerialization.Serialize(b));
        Assert.Equal(b.Rtc!.RealTimeIsUniversal.Value.RawData.ToArray(), reopened.Rtc!.RealTimeIsUniversal.Value.RawData.ToArray());
        Assert.Equal(11u, reopened.Rtc.RealTimeIsUniversal.Value.NativeType);
    }

    [Fact]
    public void DirectInstallRequiresRtcAndResolvedMutationFootprint()
    {
        var a = Fixture();
        var unresolved = Seal(a with { Scope = RecoveryScopeV1.UnresolvedDirectInstall(0) });
        Assert.Equal(BootRecoverySupport.Partial, RecoverySnapshotRules.Assess(unresolved).Support);
        var missingRtc = Seal(a with { Scope = a.Scope with { Workflow = RecoveryWorkflow.CommunityDirectInstallBootRegistration, IncludeRtc = false }, Rtc = null });
        Assert.Equal(BootRecoverySupport.Partial, RecoverySnapshotRules.Assess(missingRtc).Support);
    }

    [Theory]
    [InlineData(ObservationAvailability.Unavailable, RecoverySnapshotMatch.ObservationUnavailable)]
    [InlineData(ObservationAvailability.AccessDenied, RecoverySnapshotMatch.ObservationUnavailable)]
    [InlineData(ObservationAvailability.Unsupported, RecoverySnapshotMatch.Unsupported)]
    [InlineData(ObservationAvailability.Ambiguous, RecoverySnapshotMatch.Ambiguous)]
    [InlineData(ObservationAvailability.Absent, RecoverySnapshotMatch.Missing)]
    public void ComparisonPreservesFailedAndMissingBindingStates(ObservationAvailability state, RecoverySnapshotMatch expected)
    {
        var a = Fixture();
        Assert.Equal(expected, RecoverySnapshotRules.Compare(a, Seal(a with { Binding = F<RecoveryTargetBindingV1>(state) })).Result);
    }

    [Fact]
    public void DifferentCanonicalTargetIsChanged()
    {
        var a = Fixture();
        var target = a.Binding.Value.TargetVolume with { VolumeGuid = Guid.NewGuid(), PartitionGuid = Guid.NewGuid() };
        var b = Seal(a with { Binding = A(a.Binding.Value with { TargetVolume = target }) });
        Assert.Equal(RecoverySnapshotMatch.Changed, RecoverySnapshotRules.Compare(a, b).Result);
    }

    [Fact]
    public void FutureSchemaRemainsUnsupported()
    {
        var a = Fixture();
        Assert.Equal(RecoverySnapshotMatch.Unsupported, RecoverySnapshotRules.Compare(a, Seal(a with { SchemaVersion = 2 })).Result);
    }

    [Fact]
    public void PartialBaselineCannotValidateAnotherPartialSnapshot()
    {
        var a = Fixture();
        var partial = Seal(a with { IndependentReadback = F<bool>(ObservationAvailability.Unavailable) });
        Assert.Equal(RecoverySnapshotMatch.ObservationUnavailable, RecoverySnapshotRules.Compare(partial, partial).Result);
    }

    [Fact]
    public void HashDoesNotSubstituteForStructuralValidation()
    {
        var a = Fixture();
        var corrupted = Seal(a with { Firmware = a.Firmware with { BootOrder = A<ImmutableArray<ushort>>([2, 0]) } });
        Assert.True(RecoverySnapshotSerialization.VerifyHash(corrupted));
        Assert.Equal(RecoverySnapshotMatch.Ambiguous, RecoverySnapshotRules.Compare(a, corrupted).Result);
        Assert.Equal(RecoverySnapshotMatch.Ambiguous, RecoverySnapshotRules.Compare(a, a with { CanonicalHash = new string('0', 64) }).Result);
    }

    [Fact]
    public void MissingNativeVariableAttributesCannotBeAssumedFromLoadOptionAttributes()
    {
        var a = Fixture();
        var b = Seal(a with { Firmware = a.Firmware with { BootOrderRaw = a.Firmware.BootOrderRaw with { VariableAttributes = F<uint>(ObservationAvailability.Unsupported) } } });
        Assert.Equal(BootRecoverySupport.Unsupported, RecoverySnapshotRules.Assess(b).Support);
    }

    [Fact]
    public void MissingRequiredBcdDependencyIsRecordedWithoutInventingAnEmptyObject()
    {
        var a = Fixture();
        var graph = a.BootConfiguration with { Objects = A(a.BootConfiguration.Objects.Value.Where(o => o.Id != ResumeId).ToImmutableArray()) };
        var closure = BcdDependencyAnalysis.Analyze(graph, [LoaderId]);
        Assert.Contains(closure.Issues, i => i.Code == BcdDependencyIssueCode.MissingObject && i.Availability == ObservationAvailability.Absent);
    }

    [Fact]
    public void DuplicateRequiredBcdObjectsAreAmbiguous()
    {
        var a = Fixture();
        var b = Seal(a with { BootConfiguration = a.BootConfiguration with { Objects = A(a.BootConfiguration.Objects.Value.Add(a.BootConfiguration.Objects.Value[0])) } });
        Assert.Equal(RecoverySnapshotMatch.Ambiguous, RecoverySnapshotRules.Compare(a, b).Result);
    }

    [Fact]
    public void EfiGuidOrGeometryMismatchCannotBindAnotherEsp()
    {
        var a = Fixture();
        var b = Seal(a with { Esp = A(a.Esp.Value with { EspVolume = a.Esp.Value.EspVolume with { OffsetBytes = 2 * 1024 * 1024 } }) });
        Assert.Equal(RecoverySnapshotMatch.Ambiguous, RecoverySnapshotRules.Compare(a, b).Result);
    }

    [Theory]
    [InlineData(ObservationAvailability.Unavailable, RecoverySnapshotMatch.ObservationUnavailable)]
    [InlineData(ObservationAvailability.AccessDenied, RecoverySnapshotMatch.ObservationUnavailable)]
    [InlineData(ObservationAvailability.Unsupported, RecoverySnapshotMatch.Unsupported)]
    [InlineData(ObservationAvailability.Ambiguous, RecoverySnapshotMatch.Ambiguous)]
    [InlineData(ObservationAvailability.Absent, RecoverySnapshotMatch.Missing)]
    public void FailedBcdReadsDoNotBecomeAssociationChangesOrInventedMissingElements(ObservationAvailability state, RecoverySnapshotMatch expected)
    {
        var a = Fixture();
        var graph = Seal(a with { BootConfiguration = a.BootConfiguration with { Objects = F<ImmutableArray<BcdObjectSnapshot>>(state) } });
        var element = ChangeElement(a, BcdRecoveryGraphV1.WindowsBootManagerId, 0x12000002, new(0x12000002, F<BcdElementValue>(state)));
        Assert.Equal(expected, RecoverySnapshotRules.Compare(a, graph).Result);
        Assert.Equal(expected, RecoverySnapshotRules.Compare(a, element).Result);
    }

    [Fact]
    public void MissingRequiredBcdPathIsMissingInsteadOfAnObservedDifferentPath()
    {
        var a = Fixture();
        var manager = a.BootConfiguration.Objects.Value.Single(o => o.Id == BcdRecoveryGraphV1.WindowsBootManagerId);
        var b = ReplaceObject(a, manager.Id, manager with { Elements = A(manager.Elements.Value.Where(e => e.Type != 0x12000002).ToImmutableArray()) });
        Assert.Equal(RecoverySnapshotMatch.Missing, RecoverySnapshotRules.Compare(a, b).Result);
    }

    [Fact]
    public void QualifiedDeviceCannotHideDifferentOrdinaryFileState()
    {
        var a = Fixture();
        var element = a.BootConfiguration.Objects.Value.Single(o => o.Id == RecoveryId).Elements.Value.First();
        var device = ((BcdDeviceElementValue)element.Value.Value).Device;
        var b = ChangeElement(a, RecoveryId, element.Type, element with { QualifiedDevice = A(device) });
        Assert.Equal(BootRecoverySupport.Exact, RecoverySnapshotRules.Assess(b).Support);
        var changed = ChangeElement(b, RecoveryId, element.Type, element with
        {
            QualifiedDevice = A(device), Value = A<BcdElementValue>(new BcdDeviceElementValue(((BcdFileDevice)device) with { Path = @"\elsewhere.wim" })),
        });
        Assert.NotEqual(b.CanonicalHash, changed.CanonicalHash);
        Assert.Equal(RecoverySnapshotMatch.Ambiguous, RecoverySnapshotRules.Compare(b, changed).Result);
    }

    [Fact]
    public void CallerCannotCertifyTheUnintegratedDirectInstallFootprintWithABoolean()
    {
        var a = Fixture();
        var b = Seal(a with { Scope = a.Scope with { Workflow = RecoveryWorkflow.CommunityDirectInstallBootRegistration, MutationFootprintResolved = true } });
        Assert.Equal(BootRecoverySupport.Partial, RecoverySnapshotRules.Assess(b).Support);
        Assert.Contains(RecoverySnapshotRules.Assess(b).Issues, i => i.Code == RecoverySnapshotIssueCode.ScopeUnresolved);
    }

    [Fact]
    public void RequiredOpaqueFirmwareOptionsRemainLosslessButCannotProveDependencyClosure()
    {
        var a = Fixture();
        var bytes = a.Firmware.BootEntries[0].Raw.Bytes.Value.AddRange(ImmutableArray.Create<byte>(9, 8, 7));
        var entry = EfiRecoveryParser.ParseBootEntry(0, Variable(bytes));
        var b = Seal(a with { Firmware = a.Firmware with { BootEntries = a.Firmware.BootEntries.SetItem(0, entry) } });
        var reopened = RecoverySnapshotSerialization.Deserialize(RecoverySnapshotSerialization.Serialize(b));
        Assert.Equal(bytes.ToArray(), reopened.Firmware.BootEntries[0].Raw.Bytes.Value.ToArray());
        Assert.Equal(new byte[] { 9, 8, 7 }, reopened.Firmware.BootEntries[0].LoadOption.Value.OptionalData.ToArray());
        Assert.Equal(BootRecoverySupport.Unsupported, RecoverySnapshotRules.Assess(reopened).Support);
        Assert.Contains(RecoverySnapshotRules.Assess(reopened).Evidence, e => e.Identity == "Boot0000" && e.Relevance == RecoveryRelevance.RelevantOpaque);
    }

    [Theory]
    [InlineData(5)]
    [InlineData(1314)]
    public void DeniedNativeStatusCannotBeSealedAsAbsentBootNext(int nativeError)
    {
        var a = Fixture();
        var b = Seal(a with { Firmware = a.Firmware with { BootNextRaw = a.Firmware.BootNextRaw with { NativeError = nativeError } } });
        Assert.Equal(RecoverySnapshotMatch.Ambiguous, RecoverySnapshotRules.Compare(a, b).Result);
    }

    [Fact]
    public void EveryRequiredBcdDeviceMustCorrelateToCapturedCanonicalStorage()
    {
        var a = Fixture();
        var b = ChangeElement(a, ResumeId, 0x11000001, E(0x11000001, new BcdDeviceElementValue(new BcdQualifiedGptPartitionDevice(Guid.NewGuid(), Guid.NewGuid()))));
        Assert.Equal(RecoverySnapshotMatch.ObservationUnavailable, RecoverySnapshotRules.Compare(a, b).Result);
    }

    [Fact]
    public void LoaderObjectRoleIsPartOfSnapshotValidation()
    {
        var a = Fixture();
        var loader = a.BootConfiguration.Objects.Value.Single(o => o.Id == LoaderId);
        var b = ReplaceObject(a, LoaderId, loader with { ObjectType = BcdRecoveryRoles.WindowsBootManagerType });
        Assert.Equal(RecoverySnapshotMatch.Ambiguous, RecoverySnapshotRules.Compare(a, b).Result);
    }

    [Fact]
    public void UnexposedOpaqueBytesRemainUnsupportedAfterReopenInsteadOfObservedEmptyData()
    {
        var a = Fixture();
        var b = AddElement(a, LoaderId, E(0x18000001, new BcdOpaqueValue("Unknown", F<ImmutableArray<byte>>(ObservationAvailability.Unsupported), "diagnostic only")));
        var reopened = RecoverySnapshotSerialization.Deserialize(RecoverySnapshotSerialization.Serialize(b));
        var opaque = (BcdOpaqueValue)reopened.BootConfiguration.Objects.Value.Single(o => o.Id == LoaderId).Elements.Value.Single(e => e.Type == 0x18000001).Value.Value;
        Assert.Equal(ObservationAvailability.Unsupported, opaque.RawData.Availability);
        Assert.Throws<InvalidOperationException>(() => opaque.RawData.Value);
        Assert.Equal(BootRecoverySupport.Unsupported, RecoverySnapshotRules.Assess(reopened).Support);
    }

    private static RecoverySnapshotV1 Fixture()
    {
        var disk = new CanonicalDiskIdentityV1("eui.test", 8, 17, new("abcdefab-1234-5678-9abc-def012345678"), 2UL * 1024 * 1024 * 1024, 512, 4096);
        var windows = new CanonicalVolumeIdentityV1(disk, new("11111111-aaaa-bbbb-cccc-111111111111"), new("aaaaaaaa-1111-2222-3333-111111111111"),
            new("ebd0a0a2-b9e5-4433-87c0-68b6b72699c7"), 128UL * 1024 * 1024, 1024UL * 1024 * 1024, "NTFS");
        var esp = new CanonicalVolumeIdentityV1(disk, new("22222222-aaaa-bbbb-cccc-222222222222"), new("bbbbbbbb-1111-2222-3333-222222222222"),
            RecoverySnapshotRules.EspPartitionType, 1024 * 1024, 100 * 1024 * 1024, "FAT32");
        var recovery = new CanonicalVolumeIdentityV1(disk, new("33333333-aaaa-bbbb-cccc-333333333333"), new("cccccccc-1111-2222-3333-333333333333"),
            new("de94bba4-06d1-4d40-a16a-bfd50179d6ac"), 1152UL * 1024 * 1024, 100 * 1024 * 1024, "NTFS");
        const string bootPath = @"\EFI\Microsoft\Boot\bootmgfw.efi";
        const string winrePath = @"\Recovery\WindowsRE\Winre.wim";
        var graph = new BcdRecoveryGraphV1(1, A<ImmutableArray<BcdObjectSnapshot>>([
            Obj(BcdRecoveryGraphV1.WindowsBootManagerId, E(0x11000001, new BcdDeviceElementValue(Gpt(esp))), E(0x12000002, new BcdStringValue(bootPath)), E(0x23000003, new BcdObjectValue(LoaderId))),
            Obj(BcdRecoveryGraphV1.FirmwareBootManagerId, E(0x24000001, new BcdObjectListValue([BcdRecoveryGraphV1.WindowsBootManagerId, UnrelatedId]))),
            Obj(LoaderId, E(0x11000001, new BcdDeviceElementValue(Gpt(windows))), E(0x21000001, new BcdDeviceElementValue(Gpt(windows))), E(0x12000002, new BcdStringValue(@"\Windows\System32\winload.efi")), E(0x14000008, new BcdObjectListValue([RecoveryId])), E(0x23000003, new BcdObjectValue(ResumeId))),
            Obj(RecoveryId, E(0x11000001, new BcdDeviceElementValue(new BcdFileDevice(4, winrePath, Gpt(recovery), OptionsId))), E(0x21000001, new BcdDeviceElementValue(new BcdFileDevice(4, winrePath, Gpt(recovery), OptionsId)))),
            Obj(ResumeId, E(0x11000001, new BcdDeviceElementValue(Gpt(windows)))),
            Obj(OptionsId, E(0x31000003, new BcdDeviceElementValue(Gpt(recovery)))),
            Obj(UnrelatedId, E(0x18000001, new BcdOpaqueValue("UnrelatedUsb", A<ImmutableArray<byte>>([0, 255, 9])))),
        ]), A(LoaderId));
        var order = Variable([0, 0, 2, 0]);
        var absent = new FirmwareVariableV1(F<ImmutableArray<byte>>(ObservationAvailability.Absent), 203, F<uint>(ObservationAvailability.Unsupported));
        var unsupported = Variable([1, 0, 0, 0, 4, 0, 0, 0, 0x7f, 0xff, 4, 0]);
        var firmware = new FirmwareSnapshotV1(1, order, EfiRecoveryParser.ParseBootOrder(order), absent, EfiRecoveryParser.ParseBootNext(absent),
            [0], [EfiRecoveryParser.ParseBootEntry(0, Variable(BootBytes(esp, bootPath))), EfiRecoveryParser.ParseBootEntry(2, unsupported)]);
        return Seal(new(1, new DateTimeOffset(2026, 9, 21, 0, 0, 0, TimeSpan.Zero), new(1, RecoveryWorkflow.WindowsBootConfiguration, true, true, 0, [], []),
            A(new RecoveryTargetBindingV1(windows, windows)), graph, firmware,
            A(new EspAssociationV1(1, esp, windows, 0, BcdRecoveryGraphV1.WindowsBootManagerId, new(esp.VolumeGuid, bootPath, 123, new string('A', 64)))),
            new(1, A(true), A(RecoveryId), A(recovery), A(new CanonicalFileIdentityV1(recovery.VolumeGuid, winrePath, 456, new string('A', 64)))),
            new(A(true), F<RegistryValueV1>(ObservationAvailability.Absent)), A(true), new([new(windows.VolumeGuid, 0, 3, 'C')], [])));
    }

    private static ImmutableArray<byte> BootBytes(CanonicalVolumeIdentityV1 esp, string path)
    {
        var hd = new byte[42]; hd[0] = 4; hd[1] = 1; hd[2] = 42;
        BinaryPrimitives.WriteUInt32LittleEndian(hd.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt64LittleEndian(hd.AsSpan(8), esp.OffsetBytes / esp.Disk.LogicalSectorSize);
        BinaryPrimitives.WriteUInt64LittleEndian(hd.AsSpan(16), esp.SizeBytes / esp.Disk.LogicalSectorSize);
        esp.PartitionGuid.TryWriteBytes(hd.AsSpan(24, 16)); hd[40] = 2; hd[41] = 2;
        var text = Encoding.Unicode.GetBytes(path + '\0');
        var file = new byte[text.Length + 4]; file[0] = 4; file[1] = 4;
        BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2), (ushort)file.Length); text.CopyTo(file, 4);
        var description = Encoding.Unicode.GetBytes("Windows Boot Manager\0");
        var bytes = new byte[6 + description.Length + hd.Length + file.Length + 4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), (ushort)(hd.Length + file.Length + 4));
        description.CopyTo(bytes, 6); hd.CopyTo(bytes, 6 + description.Length); file.CopyTo(bytes, 6 + description.Length + hd.Length);
        new byte[] { 0x7f, 0xff, 4, 0 }.CopyTo(bytes, bytes.Length - 4);
        return bytes.ToImmutableArray();
    }

    private static Observation<T> A<T>(T value) => Observations.Available(value);
    private static Observation<T> F<T>(ObservationAvailability state) => Observations.Failure<T>(state, "Fixture");
    private static FirmwareVariableV1 Variable(ImmutableArray<byte> bytes) => new(A(bytes), 0, A(7u));
    private static BcdQualifiedGptPartitionDevice Gpt(CanonicalVolumeIdentityV1 v) => new(v.Disk.GptDiskGuid, v.PartitionGuid);
    private static BcdElementSnapshot E(uint type, BcdElementValue value) => new(type, A(value));
    private static BcdObjectSnapshot Obj(Guid id, params BcdElementSnapshot[] elements) => new(id,
        id == BcdRecoveryGraphV1.WindowsBootManagerId ? BcdRecoveryRoles.WindowsBootManagerType :
        id == BcdRecoveryGraphV1.FirmwareBootManagerId ? BcdRecoveryRoles.FirmwareBootManagerType :
        id == LoaderId || id == RecoveryId ? BcdRecoveryRoles.WindowsLoaderType :
        id == ResumeId ? BcdRecoveryRoles.WindowsResumeType :
        id == OptionsId ? BcdRecoveryRoles.DeviceOptionsType : 0x1010000a, A(elements.ToImmutableArray()));
    private static RecoverySnapshotV1 Seal(RecoverySnapshotV1 value) => RecoverySnapshotSerialization.Seal(value);
    private static RecoverySnapshotV1 ReplaceObject(RecoverySnapshotV1 s, Guid id, BcdObjectSnapshot replacement) =>
        Seal(s with { BootConfiguration = s.BootConfiguration with { Objects = A(s.BootConfiguration.Objects.Value.Select(o => o.Id == id ? replacement : o).ToImmutableArray()) } });
    private static RecoverySnapshotV1 ChangeElement(RecoverySnapshotV1 s, Guid id, uint type, BcdElementSnapshot replacement)
    {
        var obj = s.BootConfiguration.Objects.Value.Single(o => o.Id == id);
        return ReplaceObject(s, id, obj with { Elements = A(obj.Elements.Value.Select(e => e.Type == type ? replacement : e).ToImmutableArray()) });
    }
    private static RecoverySnapshotV1 AddElement(RecoverySnapshotV1 s, Guid id, BcdElementSnapshot element)
    {
        var obj = s.BootConfiguration.Objects.Value.Single(o => o.Id == id);
        return ReplaceObject(s, id, obj with { Elements = A(obj.Elements.Value.Add(element)) });
    }
}
