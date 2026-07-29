using FluentAssertions;
using Xunit;

namespace Igloo.Iso.Tests;

public class ChecksumParsingTests
{
    private const string Hash = "b71b64cbbd6e9d1552b48e78e197c0a9678872b0dbbea3251d38b8bab334f6d7";

    [Fact]
    public void Parses_fedora_bsd_style_lines()
    {
        var content = $"""
            # Fedora-KDE-Live-x86_64-44 CHECKSUM
            SHA256 (Fedora-KDE-Live-x86_64-44-1.4.iso) = {Hash}
            """;

        IsoAcquisitionService.ParseSha256FromChecksum(content, "Fedora-KDE-Live-x86_64-44-1.4.iso")
            .Should().Be(Hash);
    }

    [Fact]
    public void Parses_debian_coreutils_style_lines()
    {
        var content = $"""
            {Hash}  debian-live-13.6.0-amd64-gnome.iso
            0000000000000000000000000000000000000000000000000000000000000000  other.iso
            """;

        IsoAcquisitionService.ParseSha256FromChecksum(content, "debian-live-13.6.0-amd64-gnome.iso")
            .Should().Be(Hash);
    }

    [Fact]
    public void Filename_match_is_case_insensitive_and_hash_is_lowercased()
    {
        var content = $"SHA256 (My-Distro.ISO) = {Hash.ToUpperInvariant()}";

        IsoAcquisitionService.ParseSha256FromChecksum(content, "my-distro.iso")
            .Should().Be(Hash);
    }

    [Fact]
    public void Lines_for_other_files_are_ignored()
    {
        var content = $"{Hash}  some-other-file.iso";

        IsoAcquisitionService.ParseSha256FromChecksum(content, "wanted.iso")
            .Should().BeNull();
    }

    [Fact]
    public void Malformed_hashes_are_rejected()
    {
        var content = """
            SHA256 (wanted.iso) = tooshort
            deadbeef  wanted.iso
            """;

        IsoAcquisitionService.ParseSha256FromChecksum(content, "wanted.iso")
            .Should().BeNull();
    }

    [Fact]
    public void Empty_content_yields_null()
    {
        IsoAcquisitionService.ParseSha256FromChecksum("", "x.iso").Should().BeNull();
    }
}
