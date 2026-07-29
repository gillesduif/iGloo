using FluentAssertions;
using Igloo.Iso;
using Xunit;

namespace Igloo.Iso.Tests;

/// <summary>
/// Pins <see cref="IsoAcquisitionService.ResolveIsoFileName"/>: the current ISO filename is read
/// from the (GPG-verified) checksum file, so a distribution that rotates its filename each point
/// release keeps downloading without a manifest edit. Debian's live SHA256SUMS lists one image per
/// desktop; only the amd64 GNOME live image must be picked.
/// </summary>
public class IsoFileNameResolutionTests
{
    private const string DebianLiveGnomePattern = @"^debian-live-\d[\d.]*-amd64-gnome\.iso$";

    private static string DebianSums(string version) =>
        $"""
        aaaa  debian-live-{version}-amd64-kde.iso
        bbbb  debian-live-{version}-amd64-cinnamon.iso
        cccc  debian-live-{version}-amd64-standard.iso
        dddd  debian-live-{version}-amd64-gnome.iso
        """;

    [Fact]
    public void Picks_the_amd64_gnome_live_image_and_ignores_the_other_desktops()
    {
        var file = IsoAcquisitionService.ResolveIsoFileName(DebianSums("13.6.0"), DebianLiveGnomePattern);

        file.Should().Be("debian-live-13.6.0-amd64-gnome.iso");
    }

    [Fact]
    public void A_newer_point_release_resolves_with_no_change()
    {
        var file = IsoAcquisitionService.ResolveIsoFileName(DebianSums("13.7.0"), DebianLiveGnomePattern);

        file.Should().Be("debian-live-13.7.0-amd64-gnome.iso");
    }

    [Fact]
    public void Returns_null_when_nothing_matches()
    {
        const string sums = "aaaa  debian-live-13.6.0-amd64-kde.iso\n";

        IsoAcquisitionService.ResolveIsoFileName(sums, DebianLiveGnomePattern).Should().BeNull();
    }

    [Fact]
    public void The_pattern_never_matches_a_hash_token()
    {
        // A 64-char hex hash sits in the same column; the anchored pattern must not match it.
        const string sums = "debian-live-13-amd64-gnome-lookalike  0123456789.iso\n";

        IsoAcquisitionService.ResolveIsoFileName(sums, DebianLiveGnomePattern).Should().BeNull();
    }
}
