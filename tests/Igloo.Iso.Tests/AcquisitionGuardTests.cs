using FluentAssertions;
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

    private readonly string _distroId = "igloo-test-" + Guid.NewGuid().ToString("N");

    public void Dispose()
    {
        // AcquireAsync creates its cache directory before the guards run; clean it up.
        var cacheDir = Path.Combine(
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
}
