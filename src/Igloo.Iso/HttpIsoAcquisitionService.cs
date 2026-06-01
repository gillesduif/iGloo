using Igloo.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Igloo.Iso;

/// <summary>
/// Resumable HTTP download + SHA256/GPG verification + on-disk caching. Implementation in M3.
/// </summary>
public sealed class HttpIsoAcquisitionService : IIsoAcquisitionService
{
    private readonly HttpClient _http;
    private readonly ILogger<HttpIsoAcquisitionService> _logger;

    public HttpIsoAcquisitionService(HttpClient http, ILogger<HttpIsoAcquisitionService> logger)
    {
        _http = http;
        _logger = logger;
    }

    public Task<IsoAcquisitionResult> AcquireAsync(IsoSpecification spec,
        IProgress<IsoAcquisitionProgress>? progress, CancellationToken ct = default)
    {
        _logger.LogWarning("ISO acquisition not yet implemented.");
        // TODO M3:
        //   - Resolve mirror (geographic, or user-pinned)
        //   - Resume from partial via HTTP Range
        //   - Stream SHA256 during download
        //   - Verify detached GPG signature against pinned key
        //   - Cache by SHA256, not URL
        throw new NotImplementedException("ISO acquisition is scheduled for M3.");
    }
}
