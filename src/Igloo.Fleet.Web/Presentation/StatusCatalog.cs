using Igloo.Fleet.Contracts;
using Igloo.Fleet.Web.Services;
using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Igloo.Fleet.Web.Presentation;

/// <summary>One status, as the console shows it: a label key in StatusStrings, an icon shape and a colour role. Public because components take it as a parameter.</summary>
public sealed record StatusPresentation(string LabelKey, string? MeaningKey, Func<Icon> Icon, BadgeColor Color, IReadOnlyList<object>? LabelArguments = null);

/// <summary>
/// Every status scale the console renders, defined once (console-design.md, section 5).
/// Colour never carries meaning alone: each value has a distinct icon shape and a text label.
/// </summary>
internal static class StatusCatalog
{
    public static StatusPresentation Trust(AgentTrustStatus status) => status switch
    {
        AgentTrustStatus.Active => new("Trust_Active", "Trust_Active_Meaning", static () => new Icons.Regular.Size16.CheckmarkCircle(), BadgeColor.Success),
        AgentTrustStatus.Disabled => new("Trust_Disabled", "Trust_Disabled_Meaning", static () => new Icons.Regular.Size16.PauseCircle(), BadgeColor.Warning),
        AgentTrustStatus.Revoked => new("Trust_Revoked", "Trust_Revoked_Meaning", static () => new Icons.Regular.Size16.Prohibited(), BadgeColor.Danger),
        _ => Unknown("Trust_" + status),
    };

    public static StatusPresentation Certificate(DateTimeOffset expiresAtUtc, DateTimeOffset now, int warningDays)
    {
        if (expiresAtUtc <= now)
        {
            return new("Certificate_Expired", null, static () => new Icons.Regular.Size16.ErrorCircle(), BadgeColor.Danger);
        }

        var days = (int)Math.Floor((expiresAtUtc - now).TotalDays);
        if (days == 0)
        {
            return new("Certificate_ExpiresToday", null, static () => new Icons.Regular.Size16.Warning(), BadgeColor.Warning);
        }

        return days <= warningDays
            ? new("Certificate_ExpiresSoon", null, static () => new Icons.Regular.Size16.Warning(), BadgeColor.Warning, [days])
            : new("Certificate_Valid", null, static () => new Icons.Regular.Size16.Certificate(), BadgeColor.Subtle, [ConsoleFormat.UtcDate(expiresAtUtc)]);
    }

    public static StatusPresentation Token(EnrollmentTokenInfo token, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Consumed)
        {
            return new("Token_Used", null, static () => new Icons.Regular.Size16.CheckmarkCircle(), BadgeColor.Subtle);
        }

        if (token.Revoked)
        {
            return new("Token_Revoked", null, static () => new Icons.Regular.Size16.Prohibited(), BadgeColor.Subtle);
        }

        return token.ExpiresAtUtc <= now
            ? new("Token_Expired", null, static () => new Icons.Regular.Size16.Clock(), BadgeColor.Subtle)
            : new("Token_Available", null, static () => new Icons.Regular.Size16.Key(), BadgeColor.Informative);
    }

    public static bool TokenIsAvailable(EnrollmentTokenInfo token, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(token);
        return !token.Consumed && !token.Revoked && token.ExpiresAtUtc > now;
    }

    public static StatusPresentation Work(ReadOnlyWorkStatus status) => status switch
    {
        ReadOnlyWorkStatus.Pending => new("Work_Pending", null, static () => new Icons.Regular.Size16.Clock(), BadgeColor.Informative),
        ReadOnlyWorkStatus.Leased => new("Work_Leased", null, static () => new Icons.Regular.Size16.ArrowSync(), BadgeColor.Informative),
        ReadOnlyWorkStatus.Completed => new("Work_Completed", null, static () => new Icons.Regular.Size16.CheckmarkCircle(), BadgeColor.Success),
        ReadOnlyWorkStatus.Expired => new("Work_Expired", null, static () => new Icons.Regular.Size16.Clock(), BadgeColor.Subtle),
        _ => Unknown("Work_" + status),
    };

    public static StatusPresentation Check(CheckStatus status) => status switch
    {
        CheckStatus.Passed => new("Check_Passed", null, static () => new Icons.Regular.Size16.CheckmarkCircle(), BadgeColor.Success),
        CheckStatus.Information => new("Check_Information", null, static () => new Icons.Regular.Size16.Info(), BadgeColor.Informative),
        CheckStatus.Warning => new("Check_Warning", null, static () => new Icons.Regular.Size16.Warning(), BadgeColor.Warning),
        CheckStatus.Blocked => new("Check_Blocked", null, static () => new Icons.Regular.Size16.Prohibited(), BadgeColor.Danger),
        CheckStatus.Unknown => new("Check_Unknown", "Check_Unknown_Meaning", static () => new Icons.Regular.Size16.QuestionCircle(), BadgeColor.Subtle),
        _ => Unknown("Check_" + status),
    };

    public static StatusPresentation Eligibility(PlanningEligibility status) => status switch
    {
        PlanningEligibility.Eligible => new("Eligibility_Eligible", "Eligibility_Eligible_Meaning", static () => new Icons.Regular.Size16.CheckmarkCircle(), BadgeColor.Success),
        PlanningEligibility.NeedsReview => new("Eligibility_NeedsReview", "Eligibility_NeedsReview_Meaning", static () => new Icons.Regular.Size16.Warning(), BadgeColor.Warning),
        PlanningEligibility.Blocked => new("Eligibility_Blocked", "Eligibility_Blocked_Meaning", static () => new Icons.Regular.Size16.Prohibited(), BadgeColor.Danger),
        PlanningEligibility.Unknown => new("Eligibility_Unknown", "Eligibility_Unknown_Meaning", static () => new Icons.Regular.Size16.QuestionCircle(), BadgeColor.Subtle),
        _ => Unknown("Eligibility_" + status),
    };

    public static StatusPresentation Assessment(EligibilityStatus status) => status switch
    {
        EligibilityStatus.ReadyForReview => new("Assessment_ReadyForReview", "Assessment_ReadyForReview_Meaning", static () => new Icons.Regular.Size16.CheckmarkCircle(), BadgeColor.Success),
        EligibilityStatus.NeedsReview => new("Assessment_NeedsReview", "Assessment_NeedsReview_Meaning", static () => new Icons.Regular.Size16.Warning(), BadgeColor.Warning),
        EligibilityStatus.Blocked => new("Assessment_Blocked", "Assessment_Blocked_Meaning", static () => new Icons.Regular.Size16.Prohibited(), BadgeColor.Danger),
        _ => Unknown("Assessment_" + status),
    };

    public static StatusPresentation Approval(ApprovalState status) => status switch
    {
        ApprovalState.Approved => new("Approval_Approved", null, static () => new Icons.Regular.Size16.CheckmarkCircle(), BadgeColor.Success),
        ApprovalState.Revoked => new("Approval_Revoked", null, static () => new Icons.Regular.Size16.Prohibited(), BadgeColor.Subtle),
        ApprovalState.Expired => new("Approval_Expired", null, static () => new Icons.Regular.Size16.Clock(), BadgeColor.Subtle),
        ApprovalState.Superseded => new("Approval_Superseded", null, static () => new Icons.Regular.Size16.ArrowSwap(), BadgeColor.Subtle),
        _ => Unknown("Approval_" + status),
    };

    public static StatusPresentation Plan(PreparedPlanState status) => status switch
    {
        PreparedPlanState.Prepared => new("Plan_Prepared", null, static () => new Icons.Regular.Size16.DocumentCheckmark(), BadgeColor.Success),
        PreparedPlanState.Expired => new("Plan_Expired", null, static () => new Icons.Regular.Size16.Clock(), BadgeColor.Subtle),
        PreparedPlanState.Invalidated => new("Plan_Invalidated", null, static () => new Icons.Regular.Size16.Prohibited(), BadgeColor.Warning),
        PreparedPlanState.Superseded => new("Plan_Superseded", null, static () => new Icons.Regular.Size16.ArrowSwap(), BadgeColor.Subtle),
        _ => Unknown("Plan_" + status),
    };

    public static StatusPresentation Connection(OperatorFailureKind? failure) => failure switch
    {
        null => new("Connection_Connected", null, static () => new Icons.Regular.Size16.CheckmarkCircle(), BadgeColor.Success),
        OperatorFailureKind.NotConfigured => new("Connection_NotConfigured", null, static () => new Icons.Regular.Size16.Info(), BadgeColor.Subtle),
        OperatorFailureKind.NotAuthorized => new("Connection_NotAuthorized", null, static () => new Icons.Regular.Size16.LockClosed(), BadgeColor.Danger),
        OperatorFailureKind.CertificateNotTrusted => new("Connection_CertificateNotTrusted", null, static () => new Icons.Regular.Size16.ShieldError(), BadgeColor.Danger),
        OperatorFailureKind.Unreachable => new("Connection_Unreachable", null, static () => new Icons.Regular.Size16.PlugDisconnected(), BadgeColor.Danger),
        OperatorFailureKind.Rejected => new("Connection_Rejected", null, static () => new Icons.Regular.Size16.DismissCircle(), BadgeColor.Warning),
        _ => new("Connection_ServerError", null, static () => new Icons.Regular.Size16.ErrorCircle(), BadgeColor.Danger),
    };

    /// <summary>Status tokens on module pages (ModuleCatalog). Label key is Module_{token}; colour and shape follow the same rules as the other scales.</summary>
    public static StatusPresentation Module(string token) => token switch
    {
        "Present" or "Enabled" or "Supported" or "Up" or "Current" or "Active" or "Installed" or "Succeeded" or "UpToDate" or "Assigned" or "Running"
            or "Compliant" or "Healthy" or "Encrypted" or "Valid" or "Mounted" or "Clean" or "Reachable" or "Published" or "Resolved" or "Approved"
            => new("Module_" + token, null, static () => new Icons.Regular.Size16.CheckmarkCircle(), BadgeColor.Success),
        "EndOfLife" or "Down" or "Unsupported" or "Failed" or "Critical" or "NonCompliant" or "Expired" or "Full" or "Unreachable" or "Error"
            => new("Module_" + token, null, static () => new Icons.Regular.Size16.ErrorCircle(), BadgeColor.Danger),
        "Blocked" => new("Module_" + token, null, static () => new Icons.Regular.Size16.Prohibited(), BadgeColor.Danger),
        "Absent" or "Outdated" or "Held" or "Important" or "Deferred" or "AtRisk" or "Locked" or "Review" or "ExpiresSoon" or "Warning" or "ReadOnly"
            or "Conflict" or "Moderate" or "Degraded" or "Acknowledged" or "NotMounted" or "NotEncrypted" or "Open"
            => new("Module_" + token, null, static () => new Icons.Regular.Size16.Warning(), BadgeColor.Warning),
        "Paused" => new("Module_" + token, null, static () => new Icons.Regular.Size16.PauseCircle(), BadgeColor.Warning),
        "InProgress" or "Rebuilding" => new("Module_" + token, null, static () => new Icons.Regular.Size16.ArrowSync(), BadgeColor.Informative),
        "Pending" or "Scheduled" => new("Module_" + token, null, static () => new Icons.Regular.Size16.Clock(), BadgeColor.Informative),
        "Information" or "Info" or "Available" or "Upgradable" => new("Module_" + token, null, static () => new Icons.Regular.Size16.Info(), BadgeColor.Informative),
        "Unknown" or "NotEvaluated" => new("Module_" + token, null, static () => new Icons.Regular.Size16.QuestionCircle(), BadgeColor.Subtle),
        "Draft" or "Disabled" or "Inactive" or "NotRun" or "Low" or "Cancelled" or "Stopped" => new("Module_" + token, null, static () => new Icons.Regular.Size16.Circle(), BadgeColor.Subtle),
        _ => Unknown("Module_" + token),
    };

    public static string WorkTypeKey(ReadOnlyWorkType type) => "WorkType_" + type;

    public static string CheckIdKey(CheckId id) => "CheckId_" + id;

    public static string ReasonKey(PlanningReason reason) => "Reason_" + reason;

    public static string ReasonMeaningKey(PlanningReason reason) => "Reason_" + reason + "_Meaning";

    public static string CapabilityKey(PlanningCapability capability) => "Capability_" + capability;

    public static string CapabilityMeaningKey(PlanningCapability capability) => "Capability_" + capability + "_Meaning";

    public static string ActorTypeKey(string actorType) => "Actor_" + actorType;

    public static string ActionKey(string action) => "Action_" + action;

    private static StatusPresentation Unknown(string key) =>
        new(key, null, static () => new Icons.Regular.Size16.QuestionCircle(), BadgeColor.Subtle);
}
