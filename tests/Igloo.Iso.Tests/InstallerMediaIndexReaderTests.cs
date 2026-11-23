using System.Net;
using System.Text;
using FluentAssertions;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Org.BouncyCastle.Bcpg;
using Org.BouncyCastle.Bcpg.OpenPgp;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Security;
using Xunit;

namespace Igloo.Iso.Tests;

public sealed class InstallerMediaIndexReaderTests
{
    private const string Index = "https://deb.debian.org/debian/dists/trixie/InRelease";
    private const string Key = "https://ftp-master.debian.org/keys/archive-key-13.asc";
    private const string Checksums = "https://deb.debian.org/debian/dists/trixie/main/installer-amd64/current/images/SHA256SUMS";
    private const string Kernel = "https://deb.debian.org/debian/dists/trixie/main/installer-amd64/current/images/hd-media/vmlinuz";
    private const string Initrd = "https://deb.debian.org/debian/dists/trixie/main/installer-amd64/current/images/hd-media/gtk/initrd.gz";
    private const string ArchiveKeyFingerprint = "04B54C3CDCA79751B16BC6B5225629DF75B188BD";
    private const string ChecksumEntry = "main/installer-amd64/current/images/SHA256SUMS";

    private static byte[] Recorded(string name) =>
        File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Fixtures", "debian-13.8", name));

    private static InstallerBootSpec Declared(string fingerprint = ArchiveKeyFingerprint) => new()
    {
        MenuTitle = "Install Debian (Igloo)",
        KernelCmdline = "auto=true ---",
        KernelIsoPaths = ["live/vmlinuz"],
        KernelUrl = new Uri(Kernel),
        InitrdUrl = new Uri(Initrd),
        MediaIndex = new InstallerMediaIndex
        {
            SignedIndexUrl = new Uri(Index),
            IndexIdentityLine = "Codename: trixie",
            SigningKeyUrl = new Uri(Key),
            SigningKeyFingerprint = fingerprint,
            ChecksumFileUrl = new Uri(Checksums),
            ImageKernelPath = "install/vmlinuz",
        },
    };

    private static InstallerBootSpec WithIndex(InstallerBootSpec declared, Func<InstallerMediaIndex, InstallerMediaIndex> change) =>
        declared with { MediaIndex = change(declared.MediaIndex!) };

    private static Dictionary<string, byte[]> RecordedServer() => new(StringComparer.Ordinal)
    {
        [Index] = Recorded("InRelease"),
        [Key] = Recorded("archive-key-13.asc"),
        [Checksums] = Recorded("SHA256SUMS"),
    };

    private static InstallerMediaIndexReader Reader(Server server) =>
        new(server, NullLogger<InstallerMediaIndexReader>.Instance);

    [Fact]
    public async Task The_index_Debian_published_for_13_8_gives_the_hashes_of_its_installer_pair()
    {
        using var server = new Server(RecordedServer());

        var resolved = await Reader(server).ResolveAsync(Declared());

        resolved.KernelSha256.Should().BeEquivalentTo("14159c607ad9762133db41e479dafecde1d6a5fe715a8d65d292a3262e4b6bed");
        resolved.InitrdSha256.Should().BeEquivalentTo("d8a2949326c7f59caf84182ddab13e92fe0ad3938d37ead21beec9186d80a18e");
        // The index stays on the description: the preparation compares the kernels because of it.
        resolved.MediaIndex.Should().NotBeNull();
        server.Requested.Should().Equal(Index, Key, Checksums);
    }

    [Fact]
    public async Task One_changed_character_in_the_recorded_index_is_refused()
    {
        var files = RecordedServer();
        var text = Encoding.ASCII.GetString(files[Index]);
        var line = text.IndexOf(" cf5f674acf58a239", StringComparison.Ordinal);
        line.Should().BeGreaterThan(0);
        files[Index] = Encoding.ASCII.GetBytes(text[..(line + 1)] + "d" + text[(line + 2)..]);
        using var server = new Server(files);

        var refusal = await Assert.ThrowsAsync<InstallationRefusedException>(() => Reader(server).ResolveAsync(Declared()));

        refusal.Heading.Should().Be("Installer files could not be verified");
        server.Requested.Should().NotContain(Checksums);
    }

    [Fact]
    public async Task The_recorded_index_under_another_pinned_key_is_refused()
    {
        using var server = new Server(RecordedServer());

        var refusal = await Assert.ThrowsAsync<InstallationRefusedException>(() =>
            Reader(server).ResolveAsync(Declared(fingerprint: "41587F7DB8C774BCCF131416762F67A0B2C39DE4")));

        refusal.Heading.Should().Be("Installer files could not be verified");
    }

    [Fact]
    public async Task A_genuine_index_of_another_release_series_is_refused_before_anything_is_read_from_it()
    {
        // The key that signs trixie signs the lists of other series too, each with the same path to an installer.
        using var server = new Server(RecordedServer());

        var refusal = await Assert.ThrowsAsync<InstallationRefusedException>(() => Reader(server).ResolveAsync(
            WithIndex(Declared(), index => index with { IndexIdentityLine = "Codename: forky" })));

        refusal.Heading.Should().Be("Installer files could not be verified");
        server.Requested.Should().NotContain(Checksums);
    }

    [Fact]
    public async Task The_identity_line_has_to_be_a_whole_line_of_the_signed_text()
    {
        using var server = new Server(RecordedServer());

        await Assert.ThrowsAsync<InstallationRefusedException>(() => Reader(server).ResolveAsync(
            WithIndex(Declared(), index => index with { IndexIdentityLine = "Codename: trix" })));
        await Assert.ThrowsAsync<InstallationRefusedException>(() => Reader(server).ResolveAsync(
            WithIndex(Declared(), index => index with { IndexIdentityLine = "odename: trixie" })));
    }

    [Fact]
    public async Task A_download_that_ends_on_plain_http_is_refused()
    {
        using var server = new Server(RecordedServer()) { AnswersFrom = new Uri("http://mirror.example/InRelease") };

        var refusal = await Assert.ThrowsAsync<InstallationRefusedException>(() => Reader(server).ResolveAsync(Declared()));

        refusal.InnerException.Should().BeOfType<InvalidDataException>();
    }

    [Fact]
    public async Task A_key_file_larger_than_a_key_file_can_be_is_refused()
    {
        var files = RecordedServer();
        files[Key] = new byte[1024 * 1024 + 1];
        using var server = new Server(files);

        var refusal = await Assert.ThrowsAsync<InstallationRefusedException>(() => Reader(server).ResolveAsync(Declared()));

        refusal.InnerException.Should().BeOfType<InvalidDataException>();
    }

    [Fact]
    public async Task A_key_that_did_not_sign_the_index_is_refused_whatever_fingerprint_is_pinned()
    {
        var other = Signed("unrelated");
        var files = RecordedServer();
        files[Key] = other.Key;
        using var server = new Server(files);

        await Assert.ThrowsAsync<InstallationRefusedException>(() => Reader(server).ResolveAsync(Declared()));
        await Assert.ThrowsAsync<InstallationRefusedException>(() =>
            Reader(server).ResolveAsync(Declared(fingerprint: other.Fingerprint)));
    }

    [Fact]
    public async Task A_checksum_file_other_than_the_one_the_index_names_is_refused()
    {
        var files = RecordedServer();
        var changed = (byte[])files[Checksums].Clone();
        changed[0] = changed[0] == (byte)'0' ? (byte)'1' : (byte)'0';
        files[Checksums] = changed;
        using var server = new Server(files);

        var refusal = await Assert.ThrowsAsync<InstallationRefusedException>(() => Reader(server).ResolveAsync(Declared()));

        refusal.Heading.Should().Be("Installer files do not match the signed list");
    }

    [Fact]
    public async Task A_signed_index_that_does_not_name_the_checksum_file_is_refused()
    {
        var signed = Signed("Suite: stable\nCodename: trixie\nSHA256:\n " + new string('a', 64) + " 10 main/other/SHA256SUMS");
        using var server = new Server(new(StringComparer.Ordinal)
        {
            [Index] = Encoding.ASCII.GetBytes(signed.Message),
            [Key] = signed.Key,
        });

        var refusal = await Assert.ThrowsAsync<InstallationRefusedException>(() =>
            Reader(server).ResolveAsync(Declared(signed.Fingerprint)));

        refusal.Heading.Should().Be("Installer files not found");
        server.Requested.Should().NotContain(Checksums);
    }

    [Theory]
    [InlineData("hd-media/vmlinuz")]
    [InlineData("hd-media/gtk/initrd.gz")]
    public async Task A_checksum_file_that_does_not_name_one_of_the_pair_is_refused(string missing)
    {
        var lines = new[] { "hd-media/vmlinuz", "hd-media/gtk/initrd.gz", "udeb.list" }
            .Where(name => name != missing)
            .Select((name, index) => new string((char)('a' + index), 64) + "  ./" + name);
        var checksums = Encoding.ASCII.GetBytes(string.Join('\n', lines) + "\n");
        var signed = Signed(IndexNaming(checksums));
        using var server = new Server(new(StringComparer.Ordinal)
        {
            [Index] = Encoding.ASCII.GetBytes(signed.Message),
            [Key] = signed.Key,
            [Checksums] = checksums,
        });

        var refusal = await Assert.ThrowsAsync<InstallationRefusedException>(() =>
            Reader(server).ResolveAsync(Declared(signed.Fingerprint)));

        refusal.Heading.Should().Be("Installer files not found");
    }

    [Fact]
    public async Task A_signed_index_and_checksum_file_made_for_the_test_give_the_hashes_they_name()
    {
        // The refusals around this one are for what each leaves out, not for the way the test signs.
        var checksums = Encoding.ASCII.GetBytes(
            new string('a', 64) + "  ./hd-media/vmlinuz\n" + new string('c', 64) + "  ./hd-media/gtk/initrd.gz\n");
        var signed = Signed(IndexNaming(checksums));
        using var server = new Server(new(StringComparer.Ordinal)
        {
            [Index] = Encoding.ASCII.GetBytes(signed.Message),
            [Key] = signed.Key,
            [Checksums] = checksums,
        });

        var resolved = await Reader(server).ResolveAsync(Declared(signed.Fingerprint));

        resolved.KernelSha256.Should().BeEquivalentTo(new string('a', 64));
        resolved.InitrdSha256.Should().BeEquivalentTo(new string('c', 64));
    }

    [Fact]
    public async Task A_pair_named_twice_with_different_hashes_is_refused()
    {
        var checksums = Encoding.ASCII.GetBytes(
            new string('a', 64) + "  ./hd-media/vmlinuz\n" + new string('b', 64) + "  ./hd-media/vmlinuz\n" +
            new string('c', 64) + "  ./hd-media/gtk/initrd.gz\n");
        var signed = Signed(IndexNaming(checksums));
        using var server = new Server(new(StringComparer.Ordinal)
        {
            [Index] = Encoding.ASCII.GetBytes(signed.Message),
            [Key] = signed.Key,
            [Checksums] = checksums,
        });

        await Assert.ThrowsAsync<InstallationRefusedException>(() => Reader(server).ResolveAsync(Declared(signed.Fingerprint)));
    }

    [Fact]
    public async Task A_declaration_without_an_index_comes_back_as_it_is_and_asks_the_network_nothing()
    {
        using var server = new Server(new(StringComparer.Ordinal));
        var pinned = Declared() with { MediaIndex = null, KernelSha256 = new string('a', 64), InitrdSha256 = new string('b', 64) };

        (await Reader(server).ResolveAsync(pinned)).Should().BeSameAs(pinned);

        server.Requested.Should().BeEmpty();
    }

    [Fact]
    public async Task A_server_that_cannot_be_reached_is_a_refusal_that_names_the_connection()
    {
        using var server = new Server(RecordedServer()) { Unreachable = true };

        var refusal = await Assert.ThrowsAsync<InstallationRefusedException>(() => Reader(server).ResolveAsync(Declared()));

        refusal.Message.Should().Contain("Check the network connection");
        refusal.InnerException.Should().BeOfType<HttpRequestException>();
    }

    [Fact]
    public async Task A_file_the_server_no_longer_has_is_a_refusal_to_update()
    {
        var files = RecordedServer();
        files.Remove(Checksums);
        using var server = new Server(files);

        var refusal = await Assert.ThrowsAsync<InstallationRefusedException>(() => Reader(server).ResolveAsync(Declared()));

        refusal.Message.Should().Contain("Update iGloo Community");
    }

    [Fact]
    public async Task A_cancelled_read_stays_a_cancellation()
    {
        using var server = new Server(RecordedServer());
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reader(server).ResolveAsync(Declared(), cancelled.Token));
    }

    [Fact]
    public async Task A_declaration_with_hashes_beside_the_index_or_a_file_outside_its_folder_is_invalid()
    {
        using var server = new Server(RecordedServer());
        var reader = Reader(server);
        var declared = Declared();
        var index = declared.MediaIndex!;
        InstallerBootSpec[] invalid =
        [
            declared with { KernelSha256 = new string('a', 64) },
            declared with { InitrdSha256 = new string('b', 64) },
            declared with { InitrdUrl = declared.KernelUrl },
            declared with { MediaIndex = index with { IndexIdentityLine = " " } },
            declared with { MediaIndex = index with { IndexIdentityLine = "Codename: trixie\nSuite: stable" } },
            declared with { MediaIndex = index with { ImageKernelPath = "../install/vmlinuz" } },
            declared with { MediaIndex = index with { ImageKernelPath = "/install/vmlinuz" } },
            declared with { MediaIndex = index with { ImageKernelPath = "install\\vmlinuz" } },
            declared with { KernelUrl = new Uri("https://deb.debian.org/debian/dists/trixie/main/installer-amd64/vmlinuz") },
            declared with { KernelUrl = new Uri("https://example.org/debian/dists/trixie/main/installer-amd64/current/images/hd-media/vmlinuz") },
            declared with { MediaIndex = index with { SigningKeyFingerprint = "04B54C3C" } },
            declared with { MediaIndex = index with { SigningKeyUrl = new Uri("http://ftp-master.debian.org/keys/archive-key-13.asc") } },
            declared with { MediaIndex = index with { SignedIndexUrl = new Uri("http://deb.debian.org/debian/dists/trixie/InRelease") } },
            declared with { MediaIndex = index with { ChecksumFileUrl = new Uri("https://deb.debian.org/debian/dists/bookworm/main/SHA256SUMS") } },
        ];

        foreach (var spec in invalid)
            await Assert.ThrowsAsync<InvalidDataException>(() => reader.ResolveAsync(spec));

        server.Requested.Should().BeEmpty();
    }

    [Theory]
    [InlineData("https://h/a/InRelease", "https://h/a/b/SHA256SUMS", "b/SHA256SUMS")]
    [InlineData("https://h/a/InRelease", "https://h/a/SHA256SUMS", "SHA256SUMS")]
    [InlineData("https://h/a/InRelease", "https://h/a/b/../../c/SHA256SUMS", null)]
    [InlineData("https://h/a/InRelease", "https://h/a/b/%2e%2e/SHA256SUMS", "SHA256SUMS")]
    [InlineData("https://h/a/InRelease", "https://h/a/b/%2e%2e/%2e%2e/SHA256SUMS", null)]
    [InlineData("https://h/a/InRelease", "https://h/a/b/SHA256SUMS?x=1", null)]
    [InlineData("https://h/a/InRelease", "https://other/a/b/SHA256SUMS", null)]
    [InlineData("https://h/a/InRelease", "https://h/ab/SHA256SUMS", null)]
    [InlineData("https://h/a/InRelease", "https://h/a/", null)]
    [InlineData("http://h/a/InRelease", "http://h/a/b/SHA256SUMS", null)]
    public void A_file_is_below_the_folder_of_another_only_by_a_plain_path(string folderFile, string file, string? expected)
    {
        // A URL is judged as it is requested: its dot segments are already resolved.
        InstallerMediaIndexReader.PathBelow(new Uri(folderFile), new Uri(file)).Should().Be(expected);
    }

    [Fact]
    public void The_checksum_file_is_read_from_the_sha256_section_of_the_recorded_index()
    {
        var text = Encoding.ASCII.GetString(Recorded("InRelease"));
        // The same path stands in the MD5 section too, with a shorter digest.
        text.Split('\n').Count(line => line.EndsWith(" " + ChecksumEntry, StringComparison.Ordinal)).Should().BeGreaterThan(1);

        InstallerMediaIndexReader.FindIndexEntry(text, ChecksumEntry).Should().Be(
            ("CF5F674ACF58A239E6273527CF61AA863E31E5444B048AE6A9BA3DC69A15C4C1", 80699L));
        InstallerMediaIndexReader.FindIndexEntry(text, "main/installer-amd64/current/images/absent").Should().BeNull();
    }

    private static string IndexNaming(byte[] checksums) =>
        "Suite: stable\nCodename: trixie\nMD5Sum:\n " + new string('1', 32) + " " + checksums.Length + " " + ChecksumEntry +
        "\nSHA256:\n " + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(checksums)) +
        " " + checksums.Length + " " + ChecksumEntry;

    private static (byte[] Key, string Fingerprint, string Message) Signed(string body)
    {
        var generator = new RsaKeyPairGenerator();
        generator.Init(new KeyGenerationParameters(new SecureRandom(), 2048));
        var key = new PgpKeyPair(PublicKeyAlgorithmTag.RsaGeneral, generator.GenerateKeyPair(), DateTime.UtcNow);
        var signer = new PgpSignatureGenerator(PublicKeyAlgorithmTag.RsaGeneral, HashAlgorithmTag.Sha256);
        signer.InitSign(PgpSignature.CanonicalTextDocument, key.PrivateKey);
        // A clear-signed text is signed with its lines ended by CR LF.
        signer.Update(Encoding.UTF8.GetBytes(body.Replace("\n", "\r\n", StringComparison.Ordinal)));
        using var armoredBuffer = new MemoryStream();
        using (var armor = new ArmoredOutputStream(armoredBuffer))
        {
            var signature = signer.Generate().GetEncoded();
            armor.Write(signature, 0, signature.Length);
        }
        return (key.PublicKey.GetEncoded(), Convert.ToHexString(key.PublicKey.GetFingerprint()),
            $"-----BEGIN PGP SIGNED MESSAGE-----\nHash: SHA256\n\n{body}\n{Encoding.ASCII.GetString(armoredBuffer.ToArray())}");
    }

    private sealed class Server(Dictionary<string, byte[]> files) : HttpMessageHandler, IHttpClientFactory
    {
        public List<string> Requested { get; } = [];

        public bool Unreachable { get; init; }

        // Where the answer says it came from, as after a redirect.
        public Uri? AnswersFrom { get; init; }

        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var url = request.RequestUri!.AbsoluteUri;
            Requested.Add(url);
            if (Unreachable)
                throw new HttpRequestException("No such host is known.");
            if (AnswersFrom is not null)
                request.RequestUri = AnswersFrom;
            return Task.FromResult(files.TryGetValue(url, out var bytes)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes), RequestMessage = request }
                : new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        }
    }
}
