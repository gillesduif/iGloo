using Igloo.Core.Abstractions;

namespace Igloo.Community.App.ViewModels;

/// <summary>Design-time stand-in for <see cref="DiskSelectionViewModel"/>.</summary>
/// <remarks>
/// Builds real DiskListItem instances so the item templates bind against the same model
/// properties they will at run time; a parallel stub would drift from PartitionInfo.
/// </remarks>
public sealed class DiskSelectionDesignData
{
    private static readonly DiskInfo SystemDisk = new(
        @"\\.\PHYSICALDRIVE0", "Samsung SSD 990 PRO 2TB", 2_000_398_934_016, 412_316_860_416,
        "GPT",
        [
            new PartitionInfo(1, "FAT32", 104_857_600, "EFI System", true, false,
                GptType: "{c12a7328-f81f-11d2-ba4b-00a0c93ec93b}"),
            new PartitionInfo(2, "NTFS", 16_777_216, null, false, false),
            new PartitionInfo(3, "NTFS", 1_400_000_000_000, "Windows", false, true,
                ShrinkableBytes: 700_000_000_000),
            new PartitionInfo(4, "NTFS", 838_860_800, "Recovery", false, false),
        ]);

    private static readonly DiskInfo SecondDisk = new(
        @"\\.\PHYSICALDRIVE1", "WD Blue SN570 1TB", 1_000_204_886_016, 999_000_000_000,
        "GPT",
        [new PartitionInfo(1, "NTFS", 1_000_000_000_000, "Data", false, false)]);

    public IReadOnlyList<DiskListItem> DiskItems { get; } =
        [new DiskListItem(SystemDisk), new DiskListItem(SecondDisk)];

    // Settable: the ListBox binds SelectedItem TwoWay, which a read-only
    // property rejects at design time.
    public DiskListItem SelectedItem { get; set; }

    public bool CanDualBoot { get; set; } = true;
    public bool HasEligibleWindowsDisk { get; } = true;
    public bool ShowPartitionSizer => CanDualBoot;

    public int LinuxSizeGb { get; set; } = 120;
    public int MinLinuxGb { get; } = 20;
    public int MaxShrinkableGb { get; } = 651;
    public int WindowsKeepsGb { get; } = 1183;

    public DiskSelectionDesignData() => SelectedItem = DiskItems[0];

}
