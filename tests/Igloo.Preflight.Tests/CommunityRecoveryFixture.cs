using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using Igloo.Core.Abstractions;
using Igloo.Core.Recovery;

namespace Igloo.Preflight.Tests;

// Synthetic configuration scope only; it does not certify the current direct installer.
internal static class CommunityRecoveryFixture
{
    private static readonly Guid LoaderId = new("aaaaaaaa-bbbb-cccc-dddd-111111111111");
    private static readonly Guid RecoveryId = new("aaaaaaaa-bbbb-cccc-dddd-222222222222");
    private static readonly Guid ResumeId = new("aaaaaaaa-bbbb-cccc-dddd-333333333333");
    private static readonly Guid OptionsId = new("aaaaaaaa-bbbb-cccc-dddd-444444444444");
    private static readonly Guid UnrelatedId = new("aaaaaaaa-bbbb-cccc-dddd-555555555555");

    public static RecoverySnapshotV1 Exact()
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
}
