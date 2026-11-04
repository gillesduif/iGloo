using FluentAssertions;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Iso.Tests;

public sealed class AcquisitionGuardTests : IDisposable
{
    private sealed class ExplodingHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
            => throw new InvalidOperationException(
                "Test failure: the guard should have thrown before any HTTP client was created.");
    }

    private sealed class StaticIsoFactory(byte[] bytes) : IHttpClientFactory
    {
#pragma warning disable CA2000 // The returned HttpClient owns and disposes the test handler.
        public HttpClient CreateClient(string name) => new(new StaticIsoHandler(bytes));
#pragma warning restore CA2000
    }

    private sealed class StaticIsoHandler(byte[] bytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes),
        });
    }

    private sealed class CancellingMetadataFactory(CancellationTokenSource source) : IHttpClientFactory
    {
#pragma warning disable CA2000 // The returned HttpClient owns and disposes the test handler.
        public HttpClient CreateClient(string name) => new(new CancellingMetadataHandler(source));
#pragma warning restore CA2000
    }

    private sealed class CancellingMetadataHandler(CancellationTokenSource source) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await source.CancelAsync().ConfigureAwait(false);
            return await Task.FromCanceled<HttpResponseMessage>(cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private readonly string _distroId = "igloo-test-" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        // AcquireAsync creates its cache directory before the guards run; clean it up.
        var cacheDir = Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Igloo", "iso-cache", _distroId);
        if (Directory.Exists(cacheDir))
            Directory.Delete(cacheDir, recursive: true);
    }

    private static IsoAcquisitionService Service() => new(
        new ExplodingHttpClientFactory(), NullLogger<IsoAcquisitionService>.Instance);

    [Fact]
    public async Task Plain_http_download_url_is_rejected_outright()
    {
        var spec = new IsoSpecification(
            _distroId,
            new Uri("http://example.org/x.iso"),
            "b71b64cbbd6e9d1552b48e78e197c0a9678872b0dbbea3251d38b8bab334f6d7",
            GpgSignatureUrl: null, GpgKeyUrl: null);

        var act = () => Service().AcquireAsync(spec, progress: null);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Insecure URL rejected*");
    }

    [Fact]
    public async Task Plain_http_key_url_is_rejected_even_when_download_url_is_https()
    {
        var spec = new IsoSpecification(
            _distroId,
            new Uri("https://example.org/x.iso"),
            "b71b64cbbd6e9d1552b48e78e197c0a9678872b0dbbea3251d38b8bab334f6d7",
            GpgSignatureUrl: new Uri("https://example.org/CHECKSUM"),
            GpgKeyUrl: new Uri("http://example.org/key.asc"));

        var act = () => Service().AcquireAsync(spec, progress: null);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*Insecure URL rejected*");
    }

    [Fact]
    public async Task Missing_sha256_with_no_signed_checksum_refuses_to_download()
    {
        var spec = new IsoSpecification(
            _distroId,
            new Uri("https://example.org/x.iso"),
            ExpectedSha256: "REPLACE_WITH_REAL_HASH",
            GpgSignatureUrl: null, GpgKeyUrl: null);

        var act = () => Service().AcquireAsync(spec, progress: null);

        (await act.Should().ThrowAsync<InvalidOperationException>())
            .WithMessage("*No SHA-256 hash available*");
    }

    [Fact]
    public async Task Declared_signature_without_signing_key_refuses_pinned_hash_fallback()
    {
        var spec = new IsoSpecification(_distroId, new Uri("https://example.org/x.iso"),
            new string('A', 64), new Uri("https://example.org/CHECKSUM"), GpgKeyUrl: null);

        var act = () => Service().AcquireAsync(spec, progress: null);

        var failure = await act.Should().ThrowAsync<IsoAcquisitionFailureException>();
        failure.Which.Kind.Should().Be(IsoAcquisitionFailureKind.SourceMetadataUnavailable);
        failure.WithMessage("*requires a signature and a signing key*");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("AABBCC")]
    [InlineData("ZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZZ")]
    public async Task Declared_signature_without_valid_pinned_fingerprint_refuses_download(
        string? fingerprint)
    {
        var spec = new IsoSpecification(_distroId, new Uri("https://example.org/x.iso"),
            new string('A', 64), new Uri("https://example.org/CHECKSUM"),
            new Uri("https://example.org/signing-key.asc"), GpgKeyFingerprint: fingerprint);

        var act = () => Service().AcquireAsync(spec, progress: null);

        var failure = await act.Should().ThrowAsync<IsoAcquisitionFailureException>();
        failure.Which.Kind.Should().Be(IsoAcquisitionFailureKind.SourceMetadataUnavailable);
        failure.WithMessage("*requires a pinned signing-key fingerprint*");
    }

    [Fact]
    public async Task Cancelling_during_signed_checksum_fetch_remains_cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var spec = new IsoSpecification(_distroId, new Uri("https://example.org/x.iso"),
            new string('A', 64), new Uri("https://example.org/SHA256SUMS.sign"),
            new Uri("https://example.org/key.asc"),
            new Uri("https://example.org/SHA256SUMS"),
            GpgKeyFingerprint: new string('A', 40));
        var service = new IsoAcquisitionService(new CancellingMetadataFactory(cancellation),
            NullLogger<IsoAcquisitionService>.Instance);

        var act = () => service.AcquireAsync(spec, progress: null, cancellation.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Redirected_regular_cache_slot_is_rejected_before_network_use()
    {
        var target = Path.Join(Path.GetTempPath(), "igloo-iso-target-" + Guid.NewGuid().ToString("N"));
        var cache = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Igloo", "iso-cache", _distroId);
        Directory.CreateDirectory(cache);
        var link = Path.Join(cache, "x.iso");
        var regular = Path.Join(cache, "regular-downloads");
        Directory.CreateDirectory(regular);
        var fallbackLink = Path.Join(regular, "x.iso");
        try
        {
            await File.WriteAllTextAsync(target, "verified bytes");
            File.CreateSymbolicLink(link, target);
            File.CreateSymbolicLink(fallbackLink, target);
            var sha = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(target)));
            var spec = new IsoSpecification(_distroId, new Uri("https://example.org/x.iso"),
                sha, GpgSignatureUrl: null, GpgKeyUrl: null);

            var act = () => Service().AcquireAsync(spec, progress: null);

            (await act.Should().ThrowAsync<IsoAcquisitionFailureException>())
                .WithMessage("*redirected entry*");
        }
        finally
        {
            if (File.Exists(fallbackLink)) File.Delete(fallbackLink);
            if (File.Exists(link)) File.Delete(link);
            if (File.Exists(target)) File.Delete(target);
        }
    }

    [Fact]
    public async Task Redirected_regular_cache_directory_is_rejected_before_network_use()
    {
        var cache = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Igloo", "iso-cache", _distroId);
        Directory.CreateDirectory(cache);
        var external = Path.Join(Path.GetTempPath(), "igloo-iso-outside-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(external);
        var conventionalLink = Path.Join(cache, "x.iso");
        var regularLink = Path.Join(cache, "regular-downloads");
        try
        {
            File.CreateSymbolicLink(conventionalLink, Path.Join(external, "lab.iso"));
            Directory.CreateSymbolicLink(regularLink, external);
            var spec = new IsoSpecification(_distroId, new Uri("https://example.org/x.iso"),
                new string('A', 64), GpgSignatureUrl: null, GpgKeyUrl: null);

            var act = () => Service().AcquireAsync(spec, progress: null);
            (await act.Should().ThrowAsync<IsoAcquisitionFailureException>())
                .WithMessage("*redirected entry*");
            Directory.GetFileSystemEntries(external).Should().BeEmpty();
        }
        finally
        {
            Directory.Delete(regularLink);
            File.Delete(conventionalLink);
            Directory.Delete(external);
        }
    }

    [Fact]
    public async Task Empty_cached_iso_is_replaced_during_the_same_acquisition()
    {
        var cache = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Igloo", "iso-cache", _distroId);
        Directory.CreateDirectory(cache);
        var empty = Path.Join(cache, "x.iso");
        await File.WriteAllBytesAsync(empty, []);
        var contents = Encoding.UTF8.GetBytes("recovered installer bytes");
        var expected = Convert.ToHexString(SHA256.HashData(contents));
        var spec = new IsoSpecification(_distroId, new Uri("https://example.org/x.iso"),
            expected, GpgSignatureUrl: null, GpgKeyUrl: null);

        var service = new IsoAcquisitionService(new StaticIsoFactory(contents),
            NullLogger<IsoAcquisitionService>.Instance);
        var result = await service.AcquireAsync(spec, progress: null);

        result.Sha256Verified.Should().BeTrue();
        string.Equals(result.VerifiedSha256, expected, StringComparison.OrdinalIgnoreCase)
            .Should().BeTrue();
        (await File.ReadAllBytesAsync(empty)).Should().Equal(contents);
    }

    [Fact]
    public async Task Redirected_conventional_cache_entry_is_preserved_and_regular_slot_is_reused()
    {
        var cache = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Igloo", "iso-cache", _distroId);
        Directory.CreateDirectory(cache);
        var link = Path.Join(cache, "x.iso");
        var target = Path.Join(Path.GetTempPath(), "igloo-iso-lab-" + Guid.NewGuid().ToString("N"));
        var original = Encoding.UTF8.GetBytes("existing lab image");
        var contents = Encoding.UTF8.GetBytes("verified downloaded image");
        try
        {
            await File.WriteAllBytesAsync(target, original);
            File.CreateSymbolicLink(link, target);
            var sha = Convert.ToHexString(SHA256.HashData(contents));
            var spec = new IsoSpecification(_distroId, new Uri("https://example.org/x.iso"),
                sha, GpgSignatureUrl: null, GpgKeyUrl: null);

            var first = await new IsoAcquisitionService(new StaticIsoFactory(contents),
                NullLogger<IsoAcquisitionService>.Instance).AcquireAsync(spec, progress: null);
            var second = await Service().AcquireAsync(spec, progress: null);

            first.LocalPath.Should().Be(Path.Join(cache, "regular-downloads", "x.iso"));
            second.LocalPath.Should().Be(first.LocalPath);
            first.SizeBytes.Should().Be(contents.Length);
            second.Sha256Verified.Should().BeTrue();
            (await File.ReadAllBytesAsync(first.LocalPath)).Should().Equal(contents);
            (await File.ReadAllBytesAsync(target)).Should().Equal(original);
            (File.GetAttributes(link) & FileAttributes.ReparsePoint)
                .Should().Be(FileAttributes.ReparsePoint);
        }
        finally
        {
            if (File.Exists(link)) File.Delete(link);
            if (File.Exists(target)) File.Delete(target);
        }
    }

    [Fact]
    public async Task Dangling_cache_link_never_creates_its_external_target()
    {
        var cache = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Igloo", "iso-cache", _distroId);
        Directory.CreateDirectory(cache);
        var link = Path.Join(cache, "x.iso");
        var external = Path.Join(Path.GetTempPath(), "igloo-iso-absent-" + Guid.NewGuid().ToString("N"));
        var contents = Encoding.UTF8.GetBytes("verified downloaded image");
        try
        {
            File.CreateSymbolicLink(link, external);
            var spec = new IsoSpecification(_distroId, new Uri("https://example.org/x.iso"),
                Convert.ToHexString(SHA256.HashData(contents)),
                GpgSignatureUrl: null, GpgKeyUrl: null);

            var result = await new IsoAcquisitionService(new StaticIsoFactory(contents),
                NullLogger<IsoAcquisitionService>.Instance).AcquireAsync(spec, progress: null);

            result.LocalPath.Should().Be(Path.Join(cache, "regular-downloads", "x.iso"));
            File.Exists(external).Should().BeFalse();
        }
        finally
        {
            File.Delete(link);
            if (File.Exists(external)) File.Delete(external);
        }
    }
}
