using FluentAssertions;
using Igloo.Iso;
using Xunit;

namespace Igloo.Iso.Tests;

/// <summary>
/// Pins <see cref="IsoAcquisitionService.ResolveIsoFileName"/>: the current ISO filename is read
/// from the (GPG-verified) checksum file, so a distribution that rotates its filename each point
/// release keeps downloading without a manifest edit. Debian's SHA256SUMS lists several images;
/// only the amd64 netinst must be picked.
/// </summary>
public class IsoFileNameResolutionTests
{
    private const string DebianNetinstPattern = @"^debian-\d[\d.]*-amd64-netinst\.iso$";

    private static string DebianSums(string version) =>
        $"""
        aaaa  debian-{version}-amd64-DVD-1.iso
        bbbb  debian-edu-{version}-amd64-netinst.iso
        cccc  debian-mac-{version}-amd64-netinst.iso
        dddd  debian-{version}-amd64-netinst.iso
        """;

    [Fact]
    public void Picks_the_amd64_netinst_and_ignores_dvd_edu_and_mac()
    {
        var file = IsoAcquisitionService.ResolveIsoFileName(DebianSums("13.6.0"), DebianNetinstPattern);

        file.Should().Be("debian-13.6.0-amd64-netinst.iso");
    }

    [Fact]
    public void A_newer_point_release_resolves_with_no_change()
    {
        var file = IsoAcquisitionService.ResolveIsoFileName(DebianSums("13.7.0"), DebianNetinstPattern);

        file.Should().Be("debian-13.7.0-amd64-netinst.iso");
    }

    [Fact]
    public void Returns_null_when_nothing_matches()
    {
        const string sums = "aaaa  debian-13.6.0-amd64-DVD-1.iso\n";

        IsoAcquisitionService.ResolveIsoFileName(sums, DebianNetinstPattern).Should().BeNull();
    }

    [Fact]
    public void The_pattern_never_matches_a_hash_token()
    {
        // A 64-char hex hash sits in the same column; the anchored pattern must not match it.
        const string sums = "debian-13-amd64-netinst-lookalike  0123456789.iso\n";

        IsoAcquisitionService.ResolveIsoFileName(sums, DebianNetinstPattern).Should().BeNull();
    }
}
