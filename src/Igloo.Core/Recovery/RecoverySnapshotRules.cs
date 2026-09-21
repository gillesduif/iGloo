using System.Collections.Immutable;
using System.Text.Json;
using Igloo.Core.Abstractions;
using Igloo.Core.Execution;

namespace Igloo.Core.Recovery;

public static class RecoverySnapshotRules
{
    public static readonly Guid EspPartitionType = new("c12a7328-f81f-11d2-ba4b-00a0c93ec93b");

    public static RecoverySnapshotAssessment Assess(RecoverySnapshotV1 snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        try { return AssessCore(snapshot); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NullReferenceException or JsonException or OverflowException)
        { return new(BootRecoverySupport.Partial, [new(RecoverySnapshotIssueCode.InvalidStructure, ObservationAvailability.Ambiguous)], []); }
    }

    public static RecoverySnapshotComparison Compare(RecoverySnapshotV1 expected, RecoverySnapshotV1 actual)
    {
        var baseline = Assess(expected);
        if (baseline.Support != BootRecoverySupport.Exact) return Blocked(baseline);
        var observed = Assess(actual);
        if (observed.Support != BootRecoverySupport.Exact) return Blocked(observed);
        return new(expected.CanonicalHash == actual.CanonicalHash ? RecoverySnapshotMatch.ExactMatch : RecoverySnapshotMatch.Changed, []);
    }

    private static RecoverySnapshotComparison Blocked(RecoverySnapshotAssessment result)
    {
        var states = result.Issues.Select(i => i.Availability).ToArray();
        var match = states.Contains(ObservationAvailability.Ambiguous) ? RecoverySnapshotMatch.Ambiguous :
            states.Any(s => s is ObservationAvailability.Unavailable or ObservationAvailability.AccessDenied) ? RecoverySnapshotMatch.ObservationUnavailable :
            states.Contains(ObservationAvailability.Unsupported) ? RecoverySnapshotMatch.Unsupported :
            states.Contains(ObservationAvailability.Absent) ? RecoverySnapshotMatch.Missing : RecoverySnapshotMatch.ObservationUnavailable;
        return new(match, result.Issues);
    }

    private static RecoverySnapshotAssessment AssessCore(RecoverySnapshotV1 s)
    {
        var issues = ImmutableArray.CreateBuilder<RecoverySnapshotIssue>();
        var evidence = ImmutableArray.CreateBuilder<RecoveryEvidenceClassification>();
        void Issue(RecoverySnapshotIssueCode code, ObservationAvailability state, string? id = null) => issues.Add(new(code, state, id));
        if (s.SchemaVersion != 1 || s.Scope.SchemaVersion != 1 || s.Firmware.SectionVersion != 1 || s.WinRe.SectionVersion != 1)
            Issue(RecoverySnapshotIssueCode.SchemaUnsupported, ObservationAvailability.Unsupported);
        if (!Enum.IsDefined(s.Scope.Workflow) || s.Scope.BcdMutationObjects.IsDefault || s.Scope.FirmwareMutationEntries.IsDefault ||
            s.Scope.BcdMutationObjects.Contains(Guid.Empty) || s.Scope.BcdMutationObjects.Distinct().Count() != s.Scope.BcdMutationObjects.Length ||
            s.Scope.FirmwareMutationEntries.Distinct().Count() != s.Scope.FirmwareMutationEntries.Length ||
            s.CapturedAtUtc.Offset != TimeSpan.Zero || s.Metadata.VolumeLocators.IsDefault || s.Metadata.DiagnosticCodes.IsDefault)
            Issue(RecoverySnapshotIssueCode.InvalidStructure, ObservationAvailability.Ambiguous);
        if (!s.Scope.MutationFootprintResolved)
            Issue(RecoverySnapshotIssueCode.ScopeUnresolved, ObservationAvailability.Unavailable);
        if (s.Scope.Workflow == RecoveryWorkflow.CommunityDirectInstallBootRegistration && !s.Scope.IncludeRtc)
            Issue(RecoverySnapshotIssueCode.InvalidStructure, ObservationAvailability.Ambiguous, "RtcRequiredForDirectInstall");
        if (s.Scope.Workflow == RecoveryWorkflow.CommunityDirectInstallBootRegistration)
            Issue(RecoverySnapshotIssueCode.ScopeUnresolved, ObservationAvailability.Unavailable, "DirectInstallMutationFootprintNotIntegrated");
        if (!RecoverySnapshotSerialization.VerifyHash(s)) Issue(RecoverySnapshotIssueCode.HashMismatch, ObservationAvailability.Ambiguous);
        if (s.Binding.Availability != ObservationAvailability.Available)
            Issue(RecoverySnapshotIssueCode.BindingUnavailable, s.Binding.Availability);
        else if (!CanonicalRecoveryIdentity.IsValid(s.Binding.Value.WindowsVolume) || !CanonicalRecoveryIdentity.IsValid(s.Binding.Value.TargetVolume))
            Issue(RecoverySnapshotIssueCode.InvalidBinding, ObservationAvailability.Ambiguous);

        var closure = BcdDependencyAnalysis.Analyze(s.BootConfiguration, RecoverySnapshotSerialization.Roots(s));
        foreach (var problem in closure.Issues)
            Issue(RecoverySnapshotIssueCode.BcdDependency, problem.Availability, $"{problem.Code}:{problem.ObjectId:D}:{problem.ElementType:x8}");
        foreach (var problem in BcdRecoveryRoles.Validate(s.BootConfiguration, closure.RequiredObjectIds, s.WinRe.RecoveryLoaderId))
            Issue(RecoverySnapshotIssueCode.BcdAssociationMismatch, problem.Availability, $"{problem.Role}:{problem.ObjectId:D}:{problem.ObservedType:x8}");
        if (s.BootConfiguration.CurrentLoaderId.Availability != ObservationAvailability.Available)
            Issue(RecoverySnapshotIssueCode.BcdDependency, s.BootConfiguration.CurrentLoaderId.Availability, "CurrentLoader");
        foreach (var id in closure.RequiredObjectIds)
        {
            var problems = closure.Issues.Where(i => i.ObjectId == id).ToArray();
            var relevance = problems.Any(i => i.Code == BcdDependencyIssueCode.OpaqueRelevantElement) ? RecoveryRelevance.RelevantOpaque :
                problems.Any(i => i.Availability == ObservationAvailability.Unsupported) ? RecoveryRelevance.UnsupportedRelevant : RecoveryRelevance.Required;
            evidence.Add(new(RecoveryEvidenceKind.BcdObject, id.ToString("D"), relevance, RecoveryExclusionReason.RequiredDependency));
        }
        foreach (var id in closure.ExcludedObjectIds)
            evidence.Add(new(RecoveryEvidenceKind.BcdObject, id.ToString("D"), RecoveryRelevance.ObservedUnrelated, RecoveryExclusionReason.OutsideActiveDependencyClosure));
        foreach (var id in s.Scope.BcdMutationObjects.Except(closure.RequiredObjectIds))
            evidence.Add(new(RecoveryEvidenceKind.BcdObject, id.ToString("D"), RecoveryRelevance.Required, RecoveryExclusionReason.MutationSlot));

        CheckFirmware(s, issues, evidence);
        CheckEspAndBcd(s, issues);
        CheckWinRe(s, issues);
        CheckBcdStorageBindings(s, closure.RequiredObjectIds, issues);
        if (s.Scope.IncludeRtc)
        {
            if (s.Rtc is null) Issue(RecoverySnapshotIssueCode.RtcUnavailable, ObservationAvailability.Unavailable);
            else
            {
                if (s.Rtc.SectionVersion != 1) Issue(RecoverySnapshotIssueCode.SchemaUnsupported, ObservationAvailability.Unsupported, "Rtc");
                CheckFact(s.Rtc.KeyPresent, RecoverySnapshotIssueCode.RtcUnavailable, issues);
                CheckFact(s.Rtc.RealTimeIsUniversal, RecoverySnapshotIssueCode.RtcUnavailable, issues, allowAbsent: true);
                if (s.Rtc.KeyPresent.Availability == ObservationAvailability.Available && !s.Rtc.KeyPresent.Value &&
                    s.Rtc.RealTimeIsUniversal.Availability != ObservationAvailability.Absent)
                    Issue(RecoverySnapshotIssueCode.InvalidStructure, ObservationAvailability.Ambiguous, "RtcValueWithoutKey");
                if (s.Rtc.RealTimeIsUniversal.Availability == ObservationAvailability.Available)
                {
                    var value = s.Rtc.RealTimeIsUniversal.Value;
                    if (value.RawData.IsDefault) Issue(RecoverySnapshotIssueCode.InvalidStructure, ObservationAvailability.Ambiguous, "RtcRawBytes");
                    if (value.NativeType > 11) Issue(RecoverySnapshotIssueCode.RtcUnavailable, ObservationAvailability.Unsupported, "RtcNativeType");
                }
            }
        }
        CheckFact(s.IndependentReadback, RecoverySnapshotIssueCode.ReadbackUnavailable, issues);
        if (s.IndependentReadback.Availability == ObservationAvailability.Available && !s.IndependentReadback.Value)
            Issue(RecoverySnapshotIssueCode.ReadbackUnavailable, ObservationAvailability.Ambiguous);
        var support = issues.Count == 0 ? BootRecoverySupport.Exact :
            issues.Any(i => i.Availability == ObservationAvailability.Unsupported) ? BootRecoverySupport.Unsupported : BootRecoverySupport.Partial;
        return new(support, issues.ToImmutable(), evidence.ToImmutable());
    }

    private static void CheckFirmware(RecoverySnapshotV1 s, ImmutableArray<RecoverySnapshotIssue>.Builder issues,
        ImmutableArray<RecoveryEvidenceClassification>.Builder evidence)
    {
        var firmware = s.Firmware;
        if (firmware.BootEntries.IsDefault || firmware.RequiredBootEntries.IsDefault ||
            firmware.BootEntries.GroupBy(e => e.Index).Any(g => g.Count() != 1) ||
            !firmware.RequiredBootEntries.Order().SequenceEqual(s.Scope.FirmwareMutationEntries.Append(s.Scope.WindowsBootEntry).Distinct().Order()))
            issues.Add(new(RecoverySnapshotIssueCode.FirmwareMalformed, ObservationAvailability.Ambiguous));
        CheckVariable(firmware.BootOrderRaw, false, issues);
        CheckVariable(firmware.BootNextRaw, true, issues);
        if (!FactEqual(EfiRecoveryParser.ParseBootOrder(firmware.BootOrderRaw), firmware.BootOrder) ||
            !FactEqual(EfiRecoveryParser.ParseBootNext(firmware.BootNextRaw), firmware.BootNext))
            issues.Add(new(RecoverySnapshotIssueCode.FirmwareMalformed, ObservationAvailability.Ambiguous, "RawParsedMismatch"));
        CheckFact(firmware.BootOrder, RecoverySnapshotIssueCode.FirmwareUnavailable, issues);
        CheckFact(firmware.BootNext, RecoverySnapshotIssueCode.FirmwareUnavailable, issues, allowAbsent: true);
        var required = RecoverySnapshotSerialization.RequiredFirmware(s).ToHashSet();
        foreach (var index in required)
        {
            var entries = firmware.BootEntries.Where(e => e.Index == index).ToArray();
            if (entries.Length != 1)
            { issues.Add(new(RecoverySnapshotIssueCode.FirmwareUnavailable, entries.Length == 0 ? ObservationAvailability.Unavailable : ObservationAvailability.Ambiguous, $"Boot{index:X4}")); continue; }
            var entry = entries[0];
            var allowAbsent = index != s.Scope.WindowsBootEntry && !(firmware.BootNext.Availability == ObservationAvailability.Available && firmware.BootNext.Value == index);
            CheckVariable(entry.Raw, allowAbsent, issues);
            if (!FactEqual(EfiRecoveryParser.ParseBootEntry(index, entry.Raw).LoadOption, entry.LoadOption))
                issues.Add(new(RecoverySnapshotIssueCode.FirmwareMalformed, ObservationAvailability.Ambiguous, $"Boot{index:X4}"));
            CheckFact(entry.LoadOption, RecoverySnapshotIssueCode.FirmwareUnavailable, issues, allowAbsent);
            var relevance = RecoveryRelevance.Required;
            if (entry.LoadOption.Availability == ObservationAvailability.Available)
            {
                // Pass-through bytes are lossless, but unknown options can introduce further boot dependencies.
                // Do not infer the undocumented Windows optional-data format from a BCDOBJECT substring.
                if (!entry.LoadOption.Value.OptionalData.IsEmpty)
                {
                    issues.Add(new(RecoverySnapshotIssueCode.FirmwareIdentityMismatch, ObservationAvailability.Unsupported, $"OpaqueOptionalData:Boot{index:X4}"));
                    relevance = RecoveryRelevance.RelevantOpaque;
                }
                if ((entry.LoadOption.Value.Attributes & ~0xbu) != 0)
                {
                    issues.Add(new(RecoverySnapshotIssueCode.FirmwareIdentityMismatch, ObservationAvailability.Unsupported, $"LoadOptionAttributes:Boot{index:X4}"));
                    relevance = RecoveryRelevance.UnsupportedRelevant;
                }
                var path = entry.LoadOption.Value.GptFilePath;
                CheckFact(path, RecoverySnapshotIssueCode.FirmwareIdentityMismatch, issues);
                if (path.Availability == ObservationAvailability.Unsupported) relevance = RecoveryRelevance.UnsupportedRelevant;
                if (path.Availability == ObservationAvailability.Available && s.Binding.Availability == ObservationAvailability.Available)
                {
                    var volumes = new[] { s.Binding.Value.WindowsVolume, s.Binding.Value.TargetVolume }
                        .Concat(s.Esp.Availability == ObservationAvailability.Available ? [s.Esp.Value.EspVolume] : Array.Empty<CanonicalVolumeIdentityV1>());
                    if (!volumes.Any(v => PathMatchesVolume(path.Value, v)))
                        issues.Add(new(RecoverySnapshotIssueCode.FirmwareIdentityMismatch, ObservationAvailability.Ambiguous, $"Boot{index:X4}"));
                }
            }
            evidence.Add(new(RecoveryEvidenceKind.FirmwareEntry, $"Boot{index:X4}", relevance,
                s.Scope.FirmwareMutationEntries.Contains(index) ? RecoveryExclusionReason.MutationSlot : RecoveryExclusionReason.RequiredDependency));
        }
        foreach (var entry in firmware.BootEntries.Where(e => !required.Contains(e.Index)))
            evidence.Add(new(RecoveryEvidenceKind.FirmwareEntry, $"Boot{entry.Index:X4}", RecoveryRelevance.ObservedUnrelated, RecoveryExclusionReason.UnmodifiedSelectionListEntry));
    }

    private static void CheckVariable(FirmwareVariableV1 variable, bool allowAbsent, ImmutableArray<RecoverySnapshotIssue>.Builder issues)
    {
        if (variable.Bytes.Availability == ObservationAvailability.Absent && variable.NativeError is not (null or 203) ||
            variable.Bytes.Availability == ObservationAvailability.Available && variable.NativeError is not (null or 0))
            issues.Add(new(RecoverySnapshotIssueCode.FirmwareMalformed, ObservationAvailability.Ambiguous, "ContradictoryNativeStatus"));
        CheckFact(variable.Bytes, RecoverySnapshotIssueCode.FirmwareUnavailable, issues, allowAbsent);
        if (variable.Bytes.Availability != ObservationAvailability.Available) return;
        CheckFact(variable.VariableAttributes, RecoverySnapshotIssueCode.NativeAttributesUnavailable, issues);
        // Authenticated/time-based variable restoration requires a different contract; no guessed attributes.
        if (variable.VariableAttributes.Availability == ObservationAvailability.Available && variable.VariableAttributes.Value != 7)
            issues.Add(new(RecoverySnapshotIssueCode.NativeAttributesUnavailable, ObservationAvailability.Unsupported));
    }

    private static void CheckEspAndBcd(RecoverySnapshotV1 s, ImmutableArray<RecoverySnapshotIssue>.Builder issues)
    {
        CheckFact(s.Esp, RecoverySnapshotIssueCode.EspUnavailable, issues);
        if (s.Esp.Availability != ObservationAvailability.Available || s.Binding.Availability != ObservationAvailability.Available) return;
        var esp = s.Esp.Value;
        if (esp.SectionVersion != 1) issues.Add(new(RecoverySnapshotIssueCode.SchemaUnsupported, ObservationAvailability.Unsupported, "Esp"));
        if (!CanonicalRecoveryIdentity.IsValid(esp.EspVolume) || esp.EspVolume.PartitionType != EspPartitionType ||
            !string.Equals(esp.EspVolume.FileSystem, "FAT32", StringComparison.OrdinalIgnoreCase) ||
            esp.WindowsVolume != s.Binding.Value.WindowsVolume || esp.BootEntryIndex != s.Scope.WindowsBootEntry ||
            esp.BcdBootManagerId != BcdRecoveryGraphV1.WindowsBootManagerId || !ValidFile(esp.BootManagerExecutable) ||
            esp.BootManagerExecutable.VolumeGuid != esp.EspVolume.VolumeGuid ||
            !string.Equals(esp.BootManagerExecutable.RelativePath, @"\EFI\Microsoft\Boot\bootmgfw.efi", StringComparison.OrdinalIgnoreCase))
            issues.Add(new(RecoverySnapshotIssueCode.EspAssociationMismatch, ObservationAvailability.Ambiguous));
        var entry = s.Firmware.BootEntries.SingleOrDefault(e => e.Index == s.Scope.WindowsBootEntry);
        if (entry?.LoadOption.Availability == ObservationAvailability.Available && entry.LoadOption.Value.GptFilePath.Availability == ObservationAvailability.Available)
        {
            var path = entry.LoadOption.Value.GptFilePath.Value;
            if (!PathMatchesVolume(path, esp.EspVolume) || !string.Equals(path.FilePath, esp.BootManagerExecutable.RelativePath, StringComparison.OrdinalIgnoreCase))
                issues.Add(new(RecoverySnapshotIssueCode.EspAssociationMismatch, ObservationAvailability.Ambiguous, "EfiPath"));
        }
        CheckBcdDevice(s, BcdRecoveryGraphV1.WindowsBootManagerId, 0x11000001, esp.EspVolume, issues);
        var bootPath = ReadRequiredElement(s, BcdRecoveryGraphV1.WindowsBootManagerId, 0x12000002, issues);
        if (bootPath is not null && (bootPath.Value.Value is not BcdStringValue text ||
            !string.Equals(text.Text, esp.BootManagerExecutable.RelativePath, StringComparison.OrdinalIgnoreCase))
            )
            issues.Add(new(RecoverySnapshotIssueCode.BcdAssociationMismatch, ObservationAvailability.Ambiguous, "BootManagerPath"));
        RequireElement(s, BcdRecoveryGraphV1.WindowsBootManagerId, 0x23000003, issues);
        RequireElement(s, BcdRecoveryGraphV1.FirmwareBootManagerId, 0x24000001, issues);
        if (s.BootConfiguration.CurrentLoaderId.Availability == ObservationAvailability.Available)
        {
            var loader = s.BootConfiguration.CurrentLoaderId.Value;
            CheckBcdDevice(s, loader, 0x11000001, esp.WindowsVolume, issues);
            CheckBcdDevice(s, loader, 0x21000001, esp.WindowsVolume, issues);
            RequireElement(s, loader, 0x12000002, issues);
        }
    }

    private static void CheckWinRe(RecoverySnapshotV1 s, ImmutableArray<RecoverySnapshotIssue>.Builder issues)
    {
        var winre = s.WinRe;
        CheckFact(winre.Enabled, RecoverySnapshotIssueCode.WinReUnavailable, issues);
        var enabled = winre.Enabled.Availability == ObservationAvailability.Available && winre.Enabled.Value;
        CheckFact(winre.RecoveryLoaderId, RecoverySnapshotIssueCode.WinReUnavailable, issues, !enabled);
        CheckFact(winre.RecoveryVolume, RecoverySnapshotIssueCode.WinReUnavailable, issues, !enabled);
        CheckFact(winre.Image, RecoverySnapshotIssueCode.WinReUnavailable, issues, !enabled);
        if (winre.RecoveryVolume.Availability == ObservationAvailability.Available && !CanonicalRecoveryIdentity.IsValid(winre.RecoveryVolume.Value))
            issues.Add(new(RecoverySnapshotIssueCode.WinReAssociationMismatch, ObservationAvailability.Ambiguous));
        if (winre.Image.Availability == ObservationAvailability.Available && (!ValidFile(winre.Image.Value) ||
            winre.RecoveryVolume.Availability == ObservationAvailability.Available && winre.Image.Value.VolumeGuid != winre.RecoveryVolume.Value.VolumeGuid))
            issues.Add(new(RecoverySnapshotIssueCode.WinReAssociationMismatch, ObservationAvailability.Ambiguous, "Image"));
        if (winre.ImageAssurance != WinReImageAssurance.ContentIdentityOnly)
            issues.Add(new(RecoverySnapshotIssueCode.InvalidStructure, ObservationAvailability.Ambiguous, "ImageAssurance"));
        if (enabled && winre.RecoveryLoaderId.Availability == ObservationAvailability.Available &&
            s.BootConfiguration.CurrentLoaderId.Availability == ObservationAvailability.Available && winre.RecoveryVolume.Availability == ObservationAvailability.Available)
        {
            var sequence = ReadRequiredElement(s, s.BootConfiguration.CurrentLoaderId.Value, 0x14000008, issues);
            if (sequence is not null && (sequence.Value.Value is not BcdObjectListValue ids ||
                !ids.ObjectIds.Contains(winre.RecoveryLoaderId.Value)))
                issues.Add(new(RecoverySnapshotIssueCode.WinReAssociationMismatch, ObservationAvailability.Ambiguous, "RecoverySequence"));
            foreach (var type in new uint[] { 0x11000001, 0x21000001 })
            {
                var element = ReadRequiredElement(s, winre.RecoveryLoaderId.Value, type, issues);
                if (element is null || element.QualifiedDevice is { Availability: not ObservationAvailability.Available } ||
                    winre.Image.Availability != ObservationAvailability.Available) continue;
                var device = Device(element);
                if (device is not BcdFileDevice file || file.Kind != 4 || !GptMatches(file.Parent, winre.RecoveryVolume.Value) ||
                    !string.Equals(file.Path, winre.Image.Value.RelativePath, StringComparison.OrdinalIgnoreCase))
                    issues.Add(new(RecoverySnapshotIssueCode.WinReAssociationMismatch, ObservationAvailability.Ambiguous, "RamDiskDevice"));
            }
        }
    }

    private static void CheckBcdStorageBindings(RecoverySnapshotV1 s, ImmutableArray<Guid> required,
        ImmutableArray<RecoverySnapshotIssue>.Builder issues)
    {
        // Other validators retain these failed facts. Do not relabel their unavailable identity as a new mismatch.
        if (s.Binding.Availability != ObservationAvailability.Available || s.Esp.Availability != ObservationAvailability.Available ||
            s.WinRe.RecoveryVolume.Availability is not (ObservationAvailability.Available or ObservationAvailability.Absent)) return;
        var volumes = new List<CanonicalVolumeIdentityV1>
        { s.Binding.Value.WindowsVolume, s.Binding.Value.TargetVolume, s.Esp.Value.EspVolume };
        if (s.WinRe.RecoveryVolume.Availability == ObservationAvailability.Available) volumes.Add(s.WinRe.RecoveryVolume.Value);
        if (volumes.GroupBy(v => v.VolumeGuid).Any(g => g.Distinct().Count() != 1) ||
            volumes.GroupBy(v => v.PartitionGuid).Any(g => g.Distinct().Count() != 1) ||
            volumes.Select(v => v.Disk).GroupBy(d => d.GptDiskGuid).Any(g => g.Distinct().Count() != 1) ||
            volumes.Select(v => v.Disk).GroupBy(d => (d.UniqueId, d.UniqueIdFormat)).Any(g => g.Distinct().Count() != 1))
            issues.Add(new(RecoverySnapshotIssueCode.InvalidBinding, ObservationAvailability.Ambiguous, "ConflictingCanonicalIdentity"));
        if (s.BootConfiguration.Objects.Availability != ObservationAvailability.Available) return;
        foreach (var obj in s.BootConfiguration.Objects.Value.Where(o => required.Contains(o.Id) && o.Elements.Availability == ObservationAvailability.Available))
            foreach (var element in obj.Elements.Value)
                if (element.Value.Availability == ObservationAvailability.Available && element.Value.Value is BcdDeviceElementValue &&
                    element.QualifiedDevice is not { Availability: not ObservationAvailability.Available })
                    CheckDevice(Device(element), obj.Id, 0);

        void CheckDevice(BcdDeviceValue? device, Guid id, int depth)
        {
            if (depth > 32) return; // Closure reports malformed nesting.
            if (device is BcdQualifiedGptPartitionDevice gpt &&
                !volumes.Any(v => CanonicalRecoveryIdentity.IsValid(v) && GptMatches(gpt, v)))
                issues.Add(new(RecoverySnapshotIssueCode.BcdAssociationMismatch, ObservationAvailability.Unavailable, $"DeviceVolumeNotCaptured:{id:D}"));
            if (device is BcdFileDevice file) CheckDevice(file.Parent, id, depth + 1);
        }
    }

    private static bool ValidFile(CanonicalFileIdentityV1 file) => file.VolumeGuid != Guid.Empty && file.Length > 0 &&
        file.Sha256.Length == 64 && file.Sha256.All(Uri.IsHexDigit) && file.RelativePath.StartsWith('\\') &&
        !file.RelativePath.Contains(':', StringComparison.Ordinal) && !file.RelativePath.Contains('/', StringComparison.Ordinal) &&
        !file.RelativePath.Contains('\0', StringComparison.Ordinal) && !file.RelativePath.Split('\\').Any(p => p is "." or "..");
    private static bool PathMatchesVolume(EfiGptFilePathV1 path, CanonicalVolumeIdentityV1 volume) =>
        CanonicalRecoveryIdentity.IsValid(volume) && path.PartitionGuid == volume.PartitionGuid &&
        path.StartLba == volume.OffsetBytes / volume.Disk.LogicalSectorSize && path.SizeLba == volume.SizeBytes / volume.Disk.LogicalSectorSize;
    private static bool GptMatches(BcdDeviceValue? device, CanonicalVolumeIdentityV1 volume) =>
        device is BcdQualifiedGptPartitionDevice gpt && gpt.DiskId == volume.Disk.GptDiskGuid && gpt.PartitionId == volume.PartitionGuid;
    private static BcdDeviceValue? Device(BcdElementSnapshot? element) => element?.QualifiedDevice?.Availability == ObservationAvailability.Available
        ? element.QualifiedDevice.Value : element?.Value.Availability == ObservationAvailability.Available && element.Value.Value is BcdDeviceElementValue device ? device.Device : null;
    private static BcdElementSnapshot? ReadRequiredElement(RecoverySnapshotV1 s, Guid id, uint type, ImmutableArray<RecoverySnapshotIssue>.Builder issues)
    {
        var graph = s.BootConfiguration.Objects;
        if (graph.Availability != ObservationAvailability.Available) return null; // Closure already retains the actual failure.
        var obj = graph.Value.SingleOrDefault(o => o.Id == id);
        if (obj is null || obj.Elements.Availability != ObservationAvailability.Available) return null; // Likewise, absence vs failed enumeration.
        var element = obj.Elements.Value.SingleOrDefault(e => e.Type == type);
        if (element is null)
            issues.Add(new(RecoverySnapshotIssueCode.RequiredBcdElementMissing, ObservationAvailability.Absent, $"{id:D}:{type:x8}"));
        return element?.Value.Availability == ObservationAvailability.Available ? element : null;
    }
    private static void RequireElement(RecoverySnapshotV1 s, Guid id, uint type, ImmutableArray<RecoverySnapshotIssue>.Builder issues)
    {
        ReadRequiredElement(s, id, type, issues);
    }
    private static void CheckBcdDevice(RecoverySnapshotV1 s, Guid id, uint type, CanonicalVolumeIdentityV1 volume, ImmutableArray<RecoverySnapshotIssue>.Builder issues)
    {
        var element = ReadRequiredElement(s, id, type, issues);
        if (element is null || element.QualifiedDevice is { Availability: not ObservationAvailability.Available }) return;
        if (!GptMatches(Device(element), volume)) issues.Add(new(RecoverySnapshotIssueCode.BcdAssociationMismatch, ObservationAvailability.Ambiguous, $"{id:D}:{type:x8}"));
    }
    private static void CheckFact<T>(Observation<T> fact, RecoverySnapshotIssueCode code, ImmutableArray<RecoverySnapshotIssue>.Builder issues, bool allowAbsent = false)
    {
        if (fact.Availability != ObservationAvailability.Available && !(allowAbsent && fact.Availability == ObservationAvailability.Absent)) issues.Add(new(code, fact.Availability));
    }
    private static bool FactEqual<T>(Observation<T> first, Observation<T> second) => first.Availability == second.Availability &&
        (first.Availability != ObservationAvailability.Available || RecoverySnapshotSerialization.ValueEqual(first.Value, second.Value));
}
