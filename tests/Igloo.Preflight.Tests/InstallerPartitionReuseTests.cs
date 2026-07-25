using FluentAssertions;
using Xunit;

namespace Igloo.Preflight.Tests;

public class InstallerPartitionReuseTests
{
    private const long MiB = 1024L * 1024;

    // The exact regression: a ~1.5 GB OEMDRV left from a Fedora install (install.img layout)
    // cannot host a Mint (casper) install that needs the whole ~2.9 GB ISO on the volume.
    [Fact]
    public void A_fedora_sized_leftover_is_rejected_for_a_casper_install()
    {
        long fedoraLeftover = 1_500 * MiB;
        long mintNeeds = 3_500 * MiB;

        DirectInstallService.InstallerPartitionFits(fedoraLeftover, mintNeeds).Should().BeFalse();
    }

    [Fact]
    public void A_large_enough_partition_is_reused()
    {
        DirectInstallService.InstallerPartitionFits(capacityBytes: 4_000 * MiB, requiredBytes: 3_500 * MiB)
            .Should().BeTrue();
    }

    [Fact]
    public void An_exactly_fitting_partition_is_reused()
    {
        DirectInstallService.InstallerPartitionFits(3_500 * MiB, 3_500 * MiB).Should().BeTrue();
    }
}
