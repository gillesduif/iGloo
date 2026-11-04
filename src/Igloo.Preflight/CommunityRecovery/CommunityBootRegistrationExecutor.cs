using System.Buffers.Binary;
using System.Collections.Immutable;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;
using Microsoft.Win32;

namespace Igloo.Preflight.CommunityRecovery;

internal interface ICommunityBootRegistrationExecutor
{
    CommunityRecoveryReceipt Execute(CommunityBootRegistrationPlan plan, CancellationToken cancellationToken = default);
}

// The only production entry point. It accepts no caller scope, command list, mutation callback or
// native target. A candidate cannot enter the native program before its durable boundary succeeds.
internal sealed class CommunityBootRegistrationExecutor(CommunityRecoveryBoundary boundary, Func<BootRegistrationEvidence> readCurrent)
    : ICommunityBootRegistrationExecutor
{
    public CommunityRecoveryReceipt Execute(CommunityBootRegistrationPlan plan, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        return boundary.ExecutePlan(plan, plan.Mutations, readCurrent, reopened =>
        {
            plan.RequireScopeSlots(plan.Declaration.Value.Scope);
            new BootRegistrationProgram(plan, new WindowsRegistrationNative()).Run(plan.Mutations, reopened, cancellationToken);
        }, cancellationToken);
    }

    // Temporary Community path while durable recovery capture is completed. The program
    // still enforces the planned operation sequence and native readback, but does not
    // create or require a recovery artifact.
    internal static void ExecuteWithoutRecovery(CommunityBootRegistrationPlan plan,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        new BootRegistrationProgram(plan, new WindowsRegistrationNative())
            .Run(plan.Mutations, reconcileFirmwareProjection: true, cancellationToken: cancellationToken);
    }

    // Not exposed to deterministic tests: they exercise the program using an in-memory backend.
    // Construction does not open handles or mutate state; no native call precedes verification.
    private sealed class WindowsRegistrationNative : IBootRegistrationRecoveryNative
    {
        private const string GlobalGuid = "{8be4df61-93ca-11d2-aa0d-00e098032b8c}";
        private readonly WindowsBcdMutationWriter _bcd = new();
        public BcdRecoveryGraphV1 ReadBcd() => new WindowsBcdReader().ReadRecoveryGraph();
        public void CreateBcd(Guid id, uint type) => _bcd.Create(id, type);
        public void DeleteBcd(Guid id) => _bcd.Delete(id);
        public void SetBcd(Guid id, uint type, BcdElementValue value) => _bcd.Set(id, type, value);
        public void WriteFirmware(string name, ImmutableArray<byte> bytes, uint attributes)
        {
            var error = FirmwareNative.EnableReadPrivilege();
            if (error != 0) throw new Win32Exception(error, "Firmware privilege could not be enabled; execution stopped.");
            if (!FirmwareNative.SetFirmwareEnvironmentVariableExW(name, GlobalGuid, bytes.ToArray(), checked((uint)bytes.Length), attributes))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Planned firmware write failed; execution stopped.");
        }
        public FirmwareVariableV1 ReadFirmware(string name) =>
            EfiRecoveryParser.Observe(WindowsFirmwareReader.ReadVariable(name, 65536));
        public void DeleteFirmware(string name)
        {
            var error = FirmwareNative.EnableReadPrivilege();
            if (error != 0) throw new Win32Exception(error, "Firmware privilege could not be enabled; restoration stopped.");
            // Win32 deletes an unauthenticated variable when nSize is zero.
            if (!FirmwareNative.SetFirmwareEnvironmentVariableExW(name, GlobalGuid, [], 0, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Firmware restoration could not delete the planned variable.");
        }
        public void WriteRtc(ImmutableArray<byte> bytes)
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(RtcRegistryStateV1.KeyPath, writable: true)
                ?? throw new InvalidOperationException("RTC key disappeared; execution stopped without creating it.");
            key.SetValue(RtcRegistryStateV1.ValueName, BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan()), RegistryValueKind.QWord);
        }
        public RtcRegistryStateV1 ReadRtc() => new WindowsRtcRecoveryReader().Capture();
        public void DeleteRtc()
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = hive.OpenSubKey(RtcRegistryStateV1.KeyPath, writable: true)
                ?? throw new InvalidOperationException("RTC key disappeared; restoration stopped without creating it.");
            key.DeleteValue(RtcRegistryStateV1.ValueName, throwOnMissingValue: true);
        }
    }
}

internal interface IBootRegistrationNative
{
    BcdRecoveryGraphV1 ReadBcd();
    void CreateBcd(Guid id, uint type);
    void DeleteBcd(Guid id);
    void SetBcd(Guid id, uint type, BcdElementValue value);
    void WriteFirmware(string name, ImmutableArray<byte> bytes, uint attributes);
    void WriteRtc(ImmutableArray<byte> bytes);
}

internal interface IBootRegistrationRecoveryNative : IBootRegistrationNative
{
    FirmwareVariableV1 ReadFirmware(string name);
    void DeleteFirmware(string name);
    RtcRegistryStateV1 ReadRtc();
    void DeleteRtc();
}

// Lower-level deterministic interpreter. Only the boundary-owning executor can supply the private
// Windows backend. Tests can supply a fake; passing these tests never certifies provider side effects.
internal sealed class BootRegistrationProgram(CommunityBootRegistrationPlan plan, IBootRegistrationNative native)
{
    private bool _started;

    public void Run(ImmutableArray<BootRegistrationMutation> proposed, RecoverySnapshotV1? reopenedBefore = null,
        CancellationToken cancellationToken = default) =>
        Run(proposed, reconcileFirmwareProjection: false, reopenedBefore, cancellationToken);

    public void Run(ImmutableArray<BootRegistrationMutation> proposed, bool reconcileFirmwareProjection,
        RecoverySnapshotV1? reopenedBefore = null, CancellationToken cancellationToken = default)
    {
        if (_started) throw new InvalidOperationException("Boot-registration programs cannot be replayed.");
        plan.RequireExactMutations(proposed); // Missing operations must fail before any native call.
        foreach (var mutation in proposed) plan.RequireDeclaredMutation(mutation);
        var recovery = reopenedBefore is null ? null : new BootRegistrationRestorer(plan,
            native as IBootRegistrationRecoveryNative ?? throw new InvalidOperationException("Native recovery readback is unavailable."), reopenedBefore);
        if (reconcileFirmwareProjection && reopenedBefore is not null)
            throw new InvalidOperationException("Firmware projection reconciliation does not qualify a recovery snapshot.");
        var projection = reconcileFirmwareProjection ? new BootRegistrationBcdProjection(plan) : null;
        var readback = reconcileFirmwareProjection
            ? native as IBootRegistrationRecoveryNative ?? throw new InvalidOperationException("Native firmware readback is unavailable.")
            : null;
        var applied = new List<BootRegistrationMutation>();
        BootRegistrationMutation? lastFirmwareWrite = null;
        recovery?.RequireBefore();
        var expected = plan.BeforeBcd;
        RequireGraph(expected, native.ReadBcd());
        RequireFirmwareAndRtc();
        _started = true;
        try
        {
            foreach (var mutation in proposed)
            {
                cancellationToken.ThrowIfCancellationRequested();
                plan.RequireDeclaredMutation(mutation);
                RequireFirmwareAndRtc();
                if (recovery is not null || projection is not null || mutation.Kind is BootRegistrationMutationKind.CreateBcdObject or BootRegistrationMutationKind.DeleteBcdObject or BootRegistrationMutationKind.SetBcdElement)
                    RequireCurrentGraph(); // Fresh provider handles, including references.
                switch (mutation.Kind)
                {
                    case BootRegistrationMutationKind.Firmware:
                        recovery?.Attempt(mutation);
                        native.WriteFirmware(mutation.Slot, mutation.Bytes, mutation.FirmwareAttributes ?? throw new InvalidOperationException("Firmware attributes were not planned."));
                        lastFirmwareWrite = mutation;
                        break;
                    case BootRegistrationMutationKind.Rtc:
                        if (mutation.Slot != CommunityBootRegistrationPlan.RtcSlot || mutation.Bytes.Length != 8) throw new InvalidOperationException("RTC slot mismatch.");
                        recovery?.Attempt(mutation);
                        native.WriteRtc(mutation.Bytes);
                        break;
                    case BootRegistrationMutationKind.CreateBcdObject:
                    {
                        var id = Guid.ParseExact(mutation.Slot, "D");
                        if (id != plan.NewBcdObject || expected.Objects.Value.Any(o => o.Id == id)) throw new InvalidOperationException("Planned BCD destination is occupied or substituted.");
                        var type = checked((uint)((BcdIntegerValue)mutation.BcdValue!).Number);
                        recovery?.Attempt(mutation);
                        native.CreateBcd(id, type);
                        expected = expected with { Objects = Observations.Available(expected.Objects.Value.Add(new(id, type, Observations.Available<ImmutableArray<BcdElementSnapshot>>([])))) };
                        break;
                    }
                    case BootRegistrationMutationKind.DeleteBcdObject:
                    {
                        var id = Guid.ParseExact(mutation.Slot, "D");
                        if (!plan.StaleBcdObjects.Contains(id)) throw new InvalidOperationException("Deletion identity was not planned.");
                        var actual = native.ReadBcd();
                        RequireGraph(expected, actual);
                        var original = plan.BeforeBcd.Objects.Value.Single(o => o.Id == id);
                        var current = actual.Objects.Value.SingleOrDefault(o => o.Id == id);
                        if (current is null || !BcdMutationPlanning.SameObject(original, current)) throw new InvalidOperationException("Stale object no longer matches its planned before-state.");
                        var inbound = BcdMutationPlanning.InboundReferences(actual, id);
                        if (inbound.Availability != ObservationAvailability.Available || !inbound.Value.IsEmpty)
                            throw new InvalidOperationException("Stale deletion would require an unplanned reference rewrite.");
                        recovery?.Attempt(mutation);
                        native.DeleteBcd(id);
                        expected = expected with { Objects = Observations.Available(expected.Objects.Value.Where(o => o.Id != id).ToImmutableArray()) };
                        break;
                    }
                    case BootRegistrationMutationKind.SetBcdElement:
                    {
                        var id = Guid.ParseExact(mutation.Slot[..36], "D");
                        var type = uint.Parse(mutation.Slot.AsSpan(37), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                        if (!plan.BcdSlots.Contains(id) || mutation.BcdValue is null || !BcdMutationPlanning.CanSet(type, mutation.BcdValue))
                            throw new InvalidOperationException("Unsupported or unplanned BCD setter.");
                        recovery?.Attempt(mutation);
                        native.SetBcd(id, type, mutation.BcdValue);
                        expected = expected with { Objects = Observations.Available(expected.Objects.Value.Select(o => o.Id == id ? o with
                        { Elements = Observations.Available(o.Elements.Value.Where(e => e.Type != type).Append(new(type, Observations.Available(mutation.BcdValue))).OrderBy(e => e.Type).ToImmutableArray()) } : o).ToImmutableArray()) };
                        break;
                    }
                    default: throw new InvalidOperationException("Unplanned mutation kind.");
                }
                // A native BCD setter can report success without producing its
                // planned graph. Verify that effect before later firmware or RTC
                // writes can commit a boot path to an incomplete BCD object.
                applied.Add(mutation);
                RequireFirmwareAndRtc();
                if (projection is not null || mutation.Kind is BootRegistrationMutationKind.CreateBcdObject or
                    BootRegistrationMutationKind.DeleteBcdObject or
                    BootRegistrationMutationKind.SetBcdElement)
                    RequireCurrentGraph();
                recovery?.RequireWritten(mutation);
            }
            RequireCurrentGraph();
            RequireFirmwareAndRtc();
            recovery?.RequireFinal();
        }
        catch (Exception operationFailure) when (recovery is { Attempted: true })
        {
            try { recovery.Restore(expected); }
            catch (Exception restoreFailure)
            {
                throw new InvalidOperationException("Boot registration failed and its prior state could not be verified. The recovery artifact was retained.",
                    new AggregateException(operationFailure, restoreFailure));
            }
            throw;
        }

        void RequireCurrentGraph()
        {
            var actual = native.ReadBcd();
            if (projection is not null && lastFirmwareWrite is not null)
                expected = projection.Reconcile(expected, actual, lastFirmwareWrite);
            else
                RequireGraph(expected, actual);
        }

        void RequireFirmwareAndRtc()
        {
            if (readback is null) return;
            foreach (var planned in proposed.Where(m => m.Kind == BootRegistrationMutationKind.Firmware))
            {
                var written = applied.LastOrDefault(m =>
                    m.Kind == BootRegistrationMutationKind.Firmware && m.Slot == planned.Slot);
                var before = planned.Slot switch
                {
                    "BootOrder" => plan.BeforeFirmware.BootOrderRaw,
                    "BootNext" => plan.BeforeFirmware.BootNextRaw,
                    _ => plan.BeforeFirmware.BootEntries.Single(e => $"Boot{e.Index:X4}" == planned.Slot).Raw,
                };
                var actual = readback.ReadFirmware(planned.Slot);
                if (written is null ? !BootRegistrationRestorer.SameFirmware(before, actual)
                    : !BootRegistrationRestorer.MatchesWritten(written, actual))
                    throw new InvalidOperationException($"Firmware {planned.Slot} differs from the planned state; restart was not requested.");
            }
            var rtcWrite = applied.LastOrDefault(m => m.Kind == BootRegistrationMutationKind.Rtc);
            var rtc = readback.ReadRtc();
            if (rtcWrite is null ? !BootRegistrationRestorer.SameRtc(plan.BeforeRtc, rtc)
                : !BootRegistrationRestorer.MatchesRtcWrite(rtcWrite, rtc))
                throw new InvalidOperationException("RTC differs from the planned state; restart was not requested.");
        }
    }

    internal static void RequireGraph(BcdRecoveryGraphV1 expected, BcdRecoveryGraphV1 actual)
    {
        if (actual.SchemaVersion != expected.SchemaVersion || actual.Objects.Availability != ObservationAvailability.Available || actual.Objects.Value.IsDefault ||
            !RecoverySnapshotSerialization.ValueEqual(expected.CurrentLoaderId, actual.CurrentLoaderId) ||
            actual.Objects.Value.Length != expected.Objects.Value.Length || actual.Objects.Value.Select(o => o.Id).Distinct().Count() != actual.Objects.Value.Length ||
            expected.Objects.Value.Any(o => actual.Objects.Value.SingleOrDefault(a => a.Id == o.Id) is not { } observed || !BcdMutationPlanning.SameObject(o, observed)))
            throw new InvalidOperationException("BCD state or reference graph changed; execution stopped. Replanning is required.");
    }
}
