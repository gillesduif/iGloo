using Igloo.App.ViewModels;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Services;
using Xunit;

namespace Igloo.App.Tests;

public sealed class OwnedTargetSelectionViewModelTests
{
    [Fact]
    public async Task Requires_explicit_extent_and_esp_and_preserves_selected_guids()
    {
        var layout = Layout();
        var vm = new OwnedTargetSelectionViewModel(new Reader(layout));
        await vm.LoadAsync(Disk(layout), 32L << 30);
        Assert.False(vm.CanProceed);
        Assert.Null(vm.SelectedEsp);
        Assert.Null(vm.SelectedExtent);
        vm.SelectedExtent = Assert.Single(vm.FreeExtents);
        vm.SelectedEsp = vm.EspChoices[1];
        Assert.True(vm.CanProceed);
        var request = vm.Authorize(Guid.NewGuid());
        Assert.Equal(layout.DiskGuid, request.ExpectedLayout.DiskGuid);
        Assert.Equal(layout.Partitions[1].PartitionGuid, request.EspPartitionGuid);
        vm.Clear();
        Assert.False(vm.CanProceed);
        Assert.Throws<InvalidOperationException>(() => vm.Authorize(Guid.NewGuid()));
    }

    [Fact]
    public async Task Changed_disk_identity_cannot_be_selected()
    {
        var layout = Layout();
        var vm = new OwnedTargetSelectionViewModel(new Reader(layout with { DiskGuid = Guid.NewGuid() }));
        await vm.LoadAsync(Disk(layout), 32L << 30);
        Assert.False(vm.CanProceed);
        Assert.Empty(vm.FreeExtents);
        Assert.NotNull(vm.Error);
    }

    [Fact]
    public async Task Late_read_cannot_restore_a_cleared_selection()
    {
        var layout = Layout();
        var completion = new TaskCompletionSource<GptDiskLayout>();
        var vm = new OwnedTargetSelectionViewModel(new Reader(layout) { Pending = completion.Task });
        var loading = vm.LoadAsync(Disk(layout), 32L << 30);
        vm.Clear();
        completion.SetResult(layout);
        await loading;
        Assert.Empty(vm.FreeExtents);
        Assert.False(vm.CanProceed);
        Assert.False(vm.IsLoading);
    }

    [Theory]
    [InlineData("missing-guid")]
    [InlineData("size")]
    [InlineData("sector-size")]
    public async Task Incomplete_or_changed_discovery_fails_closed(string fault)
    {
        var layout = Layout();
        var disk = fault switch
        {
            "missing-guid" => Disk(layout) with { GptDiskGuid = null },
            "size" => Disk(layout) with { TotalBytes = layout.DiskSizeBytes + 512 },
            _ => Disk(layout) with { LogicalSectorSize = 4096 },
        };
        var vm = new OwnedTargetSelectionViewModel(new Reader(layout));
        await vm.LoadAsync(disk, 32L << 30);
        Assert.False(vm.CanProceed);
        Assert.NotNull(vm.Error);
        Assert.Throws<InvalidOperationException>(() => vm.Authorize(Guid.NewGuid()));
    }

    [Fact]
    public async Task Fabricated_choices_and_oversized_allocation_are_not_authorization()
    {
        var layout = Layout();
        var vm = new OwnedTargetSelectionViewModel(new Reader(layout));
        await vm.LoadAsync(Disk(layout), 32L << 30);
        vm.SelectedExtent = vm.FreeExtents[0];
        vm.SelectedEsp = vm.EspChoices[0] with { PartitionGuid = Guid.NewGuid() };
        Assert.False(vm.CanProceed);
        vm.SelectedEsp = vm.EspChoices[0];
        vm.RootSizeGiB = 129;
        Assert.False(vm.CanProceed);
        vm.RootSizeGiB = 32;
        vm.SelectedExtent = vm.SelectedExtent with { OffsetBytes = 1L << 30 };
        Assert.False(vm.CanProceed);
    }

    private static DiskInfo Disk(GptDiskLayout layout) => new("runtime-location", "same model", layout.DiskSizeBytes,
        0, "GPT", []) { GptDiskGuid = layout.DiskGuid, LogicalSectorSize = layout.LogicalSectorSize };

    private static GptDiskLayout Layout() => new()
    {
        DiskGuid = Guid.NewGuid(), LogicalSectorSize = 512, DiskSizeBytes = 128L << 30,
        Partitions = Enumerable.Range(0, 2).Select(i => new GptPartitionIdentity
        {
            PartitionGuid = Guid.NewGuid(), GptType = InstallationTargetValidation.EfiSystemType,
            OffsetBytes = (1L + i * 256) << 20, LengthBytes = 256L << 20,
        }).ToArray(),
    };

    private sealed class Reader(GptDiskLayout layout) : IInstallationTargetPreparer
    {
        public Task<GptDiskLayout>? Pending { get; init; }
        public Task<GptDiskLayout> ReadLayoutAsync(Guid diskGuid, CancellationToken ct = default) =>
            Pending ?? Task.FromResult(layout);
        public Task<InstallationTargetClaim> PrepareAsync(InstallationTargetRequest request, string manifestPath,
            CancellationToken ct = default) => throw new InvalidOperationException("Selection must never mutate storage.");
    }
}
