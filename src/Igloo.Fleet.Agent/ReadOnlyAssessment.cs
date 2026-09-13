using System.Collections.Immutable;
using System.Security;
using Igloo.Core.Abstractions;
using Igloo.Fleet.Contracts;
using Igloo.Fleet.Domain;

namespace Igloo.Fleet.Agent;

public sealed class ReadOnlyAssessment(IPreflightChecker checker)
{
    public const string AgentVersion = "0.1.0";
    public static string IglooVersion => typeof(IPreflightChecker).Assembly.GetName().Version!.ToString();

    public async Task<AssessmentResult> RunAsync(DeviceIdentity identity, CancellationToken ct = default)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            var report = await checker.RunAsync(ct).ConfigureAwait(false);
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
        var checks = new List<PreflightCheckResult>
        {
            new(CheckId.Firmware, report.IsUefi ? CheckStatus.Passed : CheckStatus.Blocked),
            new(CheckId.SecureBoot, report.SecureBootEnabled ? CheckStatus.Information : CheckStatus.Passed),
            new(CheckId.Tpm, report.TpmPresent ? CheckStatus.Passed : CheckStatus.Information),
            new(CheckId.BitLocker, report.BitLocker switch
            {
                BitLockerState.NotEncrypted => CheckStatus.Passed,
                BitLockerState.Unknown => CheckStatus.Unknown,
                BitLockerState.DecryptionInProgress => CheckStatus.Warning,
                _ => CheckStatus.Blocked,
            }),
            new(CheckId.Memory, report.TotalRamBytes <= 0 ? CheckStatus.Unknown :
                report.TotalRamBytes < 2L * 1024 * 1024 * 1024 ? CheckStatus.Warning : CheckStatus.Passed),
            new(CheckId.Disks, report.Disks.Count == 0 ? CheckStatus.Unknown : CheckStatus.Passed),
        };
        foreach (var finding in report.Findings)
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
                FindingSeverity.Blocker => CheckStatus.Blocked,
                FindingSeverity.Warning => CheckStatus.Warning,
                _ => id == CheckId.UnknownFinding ? CheckStatus.Unknown : CheckStatus.Information,
            };
            var index = checks.FindIndex(c => c.Id == id);
            if (index < 0)
                checks.Add(new(id, status));
            else if (Rank(status) > Rank(checks[index].Status))
                checks[index] = new(id, status);
        }
        return new(FleetProtocolVersion.Current, Guid.NewGuid(), identity, started, DateTimeOffset.UtcNow,
            AgentVersion, IglooVersion, 1, new(Math.Max(0, report.TotalRamBytes), report.Disks.Count),
            checks.ToImmutableArray(), EligibilityDecision.Evaluate(checks, AssessmentOutcome.Assessed), AssessmentOutcome.Assessed);
    }

    private static int Rank(CheckStatus status) => status switch
    {
        CheckStatus.Blocked => 4, CheckStatus.Unknown => 3, CheckStatus.Warning => 2,
        CheckStatus.Information => 1, _ => 0,
    };
}
