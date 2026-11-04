using System.Globalization;
using System.Text;
using Igloo.Fleet.Contracts;
using Igloo.Fleet.Web.Services;

namespace Igloo.Fleet.Web;

/// <summary>CSV export of whole collections, as the Server returns them. Filtering happens in the browser and is not applied here.</summary>
internal static class ExportEndpoints
{
    internal static IEndpointRouteBuilder MapExportEndpoints(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        endpoints.MapGet("/export/enrollments.csv", ExportEnrollmentsAsync).RequireAuthorization();
        endpoints.MapGet("/export/enrollment-tokens.csv", ExportTokensAsync).RequireAuthorization();
        return endpoints;
    }

    private static async Task<IResult> ExportEnrollmentsAsync(IFleetOperatorClient client, CancellationToken cancellationToken)
    {
        var result = await client.GetEnrollmentsAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var rows = result.Value!.Select(d => new[]
        {
            d.Identity.DeviceId.ToString("D"), d.Identity.AgentId.ToString("D"), d.EnrollmentId.ToString("D"), d.Status.ToString(),
            Iso(d.LastHeartbeatUtc), d.Heartbeat?.AgentVersion ?? string.Empty, d.Heartbeat?.IglooVersion ?? string.Empty,
            d.Heartbeat is { } h ? $"{h.Protocol.Major}.{h.Protocol.Minor}" : string.Empty,
            d.Heartbeat is null || d.Heartbeat.Capabilities.IsDefaultOrEmpty ? string.Empty : string.Join(";", d.Heartbeat.Capabilities),
            d.CertificateHash, Iso(d.CertificateExpiresAtUtc),
        });
        return Csv("enrollments.csv",
            ["EnrollmentId", "AgentId", "ServerEnrollmentId", "TrustStatus", "LastContactUtc", "AgentVersion", "IglooVersion", "Protocol", "Capabilities", "CertificateSha256", "CertificateExpiresUtc"],
            rows);
    }

    private static async Task<IResult> ExportTokensAsync(IFleetOperatorClient client, CancellationToken cancellationToken)
    {
        var result = await client.GetEnrollmentTokensAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        // Token plaintext never exists on the Server, so it cannot leak through this export.
        var rows = result.Value!.Select(t => new[]
        {
            t.TokenId.ToString("D"), Iso(t.CreatedAtUtc), Iso(t.ExpiresAtUtc), t.CreatedBy,
            t.Consumed.ToString(), t.Revoked.ToString(),
        });
        return Csv("enrollment-tokens.csv", ["TokenId", "CreatedUtc", "ExpiresUtc", "CreatedBy", "Consumed", "Revoked"], rows);
    }

    internal static string Iso(DateTimeOffset? value) =>
        value?.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) ?? string.Empty;

    internal static IResult Csv(string fileName, string[] header, IEnumerable<string[]> rows)
    {
        var builder = new StringBuilder();
        builder.Append('﻿');
        builder.AppendLine(string.Join(',', header.Select(Quote)));
        foreach (var row in rows)
        {
            builder.AppendLine(string.Join(',', row.Select(Quote)));
        }

        return Results.File(Encoding.UTF8.GetBytes(builder.ToString()), "text/csv; charset=utf-8", fileName);
    }

    private static string Quote(string value)
    {
        // Leading formula characters are neutralised so a spreadsheet never executes a cell.
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@')
        {
            value = "'" + value;
        }

        var needsQuotes = value.Contains(',', StringComparison.Ordinal)
            || value.Contains('"', StringComparison.Ordinal)
            || value.Contains('\n', StringComparison.Ordinal)
            || value.Contains('\r', StringComparison.Ordinal);
        return needsQuotes
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }
}
