using System.Collections.Immutable;
using System.Security;
using System.Text.Json;
using Igloo.Cli.Client;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Igloo.Fleet.Contracts;
using Igloo.Fleet.Domain;

namespace Igloo.Fleet.Agent;

public sealed class ReadOnlyAssessment
{
    private readonly IEndpointInspection? _inspection;
    private readonly CliCommandClient? _commands;

    public ReadOnlyAssessment(IEndpointInspection inspection) => _inspection = inspection;
    public ReadOnlyAssessment(CliCommandClient commands) => _commands = commands;

    public const string AgentVersion = "0.1.0";
    public static string IglooVersion => typeof(PreflightReport).Assembly.GetName().Version!.ToString();

    public async Task<AssessmentResult> RunAsync(DeviceIdentity identity, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            if (_commands is not null)
            {
                var result = await _commands.ExecuteAsync(CliCommandClient.Request("system.inspect", new { }),
                    cancellationToken: ct).ConfigureAwait(false);
                if (!result.Succeeded) throw new IOException("CLI inspection was unsuccessful.");
                var summary = result.Data?.Deserialize<SystemInspection>(CommandProtocol.JsonOptions)
                    ?? throw new IOException("CLI inspection returned no typed data.");
                if (summary.Findings is null || summary.DiskCount < 0 ||
                    string.IsNullOrWhiteSpace(summary.BitLockerState))
                    throw new IOException("CLI inspection returned incomplete typed data.");
                return Map(summary, identity, started);
            }
            var report = await _inspection!.InspectAsync(ct).ConfigureAwait(false);
            return Map(report, identity, started);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or SecurityException)
        {
            // Never transmit exception messages: they can contain endpoint paths or user data.
            return new(FleetProtocolVersion.Current, Guid.NewGuid(), identity, started, DateTimeOffset.UtcNow,
                AgentVersion, IglooVersion, 1, new(0, 0), [], EligibilityStatus.NeedsReview, AssessmentOutcome.LocalExecutionFailed);
        }
    }

    public static AssessmentResult Map(PreflightReport report, DeviceIdentity identity, DateTimeOffset started)
    {
        ArgumentNullException.ThrowIfNull(report);
        return MapFacts(report.IsUefi, report.SecureBootEnabled, report.TpmPresent,
            report.BitLocker.ToString(), report.TotalRamBytes, report.Disks.Count,
            report.Findings.Select(f => new InspectionFinding(f.Code, f.Severity.ToString())),
            identity, started);
    }

    public static AssessmentResult Map(SystemInspection report, DeviceIdentity identity, DateTimeOffset started)
    {
        ArgumentNullException.ThrowIfNull(report);
        return MapFacts(report.IsUefi, report.SecureBootEnabled, report.TpmPresent,
            report.BitLockerState, report.TotalRamBytes, report.DiskCount, report.Findings,
            identity, started);
    }

    private static AssessmentResult MapFacts(bool isUefi, bool secureBootEnabled, bool tpmPresent,
        string bitLockerState, long totalRamBytes, int diskCount,
        IEnumerable<InspectionFinding> findings, DeviceIdentity identity, DateTimeOffset started)
    {
        var checks = new List<PreflightCheckResult>
        {
            new(CheckId.Firmware, isUefi ? CheckStatus.Passed : CheckStatus.Blocked),
            new(CheckId.SecureBoot, secureBootEnabled ? CheckStatus.Information : CheckStatus.Passed),
            new(CheckId.Tpm, tpmPresent ? CheckStatus.Passed : CheckStatus.Information),
            new(CheckId.BitLocker, bitLockerState switch
            {
                nameof(BitLockerState.NotEncrypted) => CheckStatus.Passed,
                nameof(BitLockerState.Unknown) => CheckStatus.Unknown,
                nameof(BitLockerState.DecryptionInProgress) => CheckStatus.Warning,
                null => CheckStatus.Unknown,
                _ => CheckStatus.Blocked,
            }),
            new(CheckId.Memory, totalRamBytes <= 0 ? CheckStatus.Unknown :
                totalRamBytes < 2L * 1024 * 1024 * 1024 ? CheckStatus.Warning : CheckStatus.Passed),
            new(CheckId.Disks, diskCount == 0 ? CheckStatus.Unknown : CheckStatus.Passed),
        };
        foreach (var finding in findings)
        {
            var id = finding.Code switch
            {
                "BIOS_LEGACY" => CheckId.Firmware,
                "SECURE_BOOT_ON" => CheckId.SecureBoot,
                "NO_TPM" => CheckId.Tpm,
                "BITLOCKER_ACTIVE" or "BITLOCKER_DECRYPTING" => CheckId.BitLocker,
                "LOW_RAM" => CheckId.Memory,
                _ => CheckId.UnknownFinding,
            };
            var status = finding.Severity switch
            {
                nameof(FindingSeverity.Blocker) => CheckStatus.Blocked,
                nameof(FindingSeverity.Warning) => CheckStatus.Warning,
                _ => id == CheckId.UnknownFinding ? CheckStatus.Unknown : CheckStatus.Information,
            };
            var index = checks.FindIndex(c => c.Id == id);
            if (index < 0)
                checks.Add(new(id, status));
            else if (Rank(status) > Rank(checks[index].Status))
                checks[index] = new(id, status);
        }
        return new(FleetProtocolVersion.Current, Guid.NewGuid(), identity, started, DateTimeOffset.UtcNow,
            AgentVersion, IglooVersion, 1, new(Math.Max(0, totalRamBytes), diskCount),
            checks.ToImmutableArray(), EligibilityDecision.Evaluate(checks, AssessmentOutcome.Assessed), AssessmentOutcome.Assessed);
    }

    private static int Rank(CheckStatus status) => status switch
    {
        CheckStatus.Blocked => 4, CheckStatus.Unknown => 3, CheckStatus.Warning => 2,
        CheckStatus.Information => 1, _ => 0,
    };
}
