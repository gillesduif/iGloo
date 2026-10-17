using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Igloo.Command.Contracts;

namespace Igloo.One.App;

internal sealed record OneChatMessage(string Speaker, string Text, bool IsTux);

/// <summary>Read-only local conversation until One has a reviewed reasoning backend.</summary>
internal sealed class OneChatViewModel(Func<CancellationToken, Task<CommandResult>> inspect)
    : INotifyPropertyChanged
{
    private bool _isChecking;
    private string _status = "Starting system check…";
    private string? _lastSystemSummary;
    public bool HasBlockers { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<bool>? CheckFinished;
    public ObservableCollection<OneChatMessage> Messages { get; } = [];
    public string? LastAssistantText { get; private set; }
    public bool CanCheck => !IsChecking;
    public bool IsChecking
    {
        get => _isChecking;
        private set { if (_isChecking == value) return; _isChecking = value; Changed(); Changed(nameof(CanCheck)); }
    }
    public string Status
    {
        get => _status;
        private set { if (_status == value) return; _status = value; Changed(); }
    }

    public void Welcome() => AddTux("Welcome to iGloo One. I am Tux. I am currently performing a system check.");

    public async Task SubmitAsync(string text, CancellationToken cancellationToken)
    {
        string message = text.Trim();
        if (message.Length == 0) return;
        Messages.Add(new("You", message, false));

        string upper = message.ToUpperInvariant();
        if (upper.Contains("PREFLIGHT", StringComparison.Ordinal) ||
            upper.Contains("SYSTEM CHECK", StringComparison.Ordinal) ||
            upper.Contains("CHECK MY SYSTEM", StringComparison.Ordinal) ||
            upper.Contains("SCAN", StringComparison.Ordinal))
        {
            await RunSystemCheckAsync(cancellationToken).ConfigureAwait(true);
        }
        else if (upper.Contains("RESULT", StringComparison.Ordinal) ||
                 upper.Contains("FIND", StringComparison.Ordinal) ||
                 upper.Contains("STATUS", StringComparison.Ordinal))
        {
            AddTux(_lastSystemSummary ?? "The system check has not completed yet.");
        }
        else
        {
            AddTux("I can run a read-only system check and explain its summary. " +
                "Ask me to run the system check again or show its results. " +
                "Broader conversation is not connected yet.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The chat boundary must report an unavailable check without crashing the WPF dispatcher or claiming success.")]
    public async Task RunSystemCheckAsync(CancellationToken cancellationToken)
    {
        if (IsChecking)
        {
            AddTux("I am already checking this system. I will show the result when it finishes.");
            return;
        }
        IsChecking = true;
        HasBlockers = false;
        Status = "Checking this system…";
        try
        {
            CommandResult result = await inspect(cancellationToken).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                string code = result.Error?.Code.ToString() ?? "UnknownError";
                Status = "System check unavailable";
                AddTux($"The system check could not complete ({code}). No changes were made.");
                return;
            }
            if (result.CapabilityId != "system.inspect" || result.Data is null)
                throw new JsonException("System inspection returned an unexpected result.");
            SystemInspection report = result.Data.Value.Deserialize<SystemInspection>(CommandProtocol.JsonOptions)
                ?? throw new JsonException("System inspection returned no report.");
            _lastSystemSummary = Summarize(report);
            HasBlockers = report.Findings.Any(f =>
                string.Equals(f.Severity, "Blocker", StringComparison.OrdinalIgnoreCase));
            Status = HasBlockers ? "Check complete · findings need review" : "System check complete";
            AddTux(_lastSystemSummary);
            CheckFinished?.Invoke(HasBlockers);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Status = "System check cancelled";
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            Status = "System check unavailable";
            AddTux("I could not complete the system check. Please try again. No changes were made.");
        }
        finally
        {
            IsChecking = false;
        }
    }

    private static string Summarize(SystemInspection report)
    {
        double ramGiB = Math.Max(0, report.TotalRamBytes) / 1073741824d;
        string summary = $"System check complete. UEFI reported: {YesNo(report.IsUefi)}; " +
            $"Secure Boot: {OnOff(report.SecureBootEnabled)}; TPM reported: {YesNo(report.TpmPresent)}; " +
            $"memory: {ramGiB:0.#} GiB; disks observed: {report.DiskCount}; " +
            $"BitLocker state: {report.BitLockerState}.";
        if (report.Findings.Count > 0)
        {
            string findings = string.Join(", ", report.Findings.Take(4)
                .Select(f => $"{f.Code} ({f.Severity})"));
            if (report.Findings.Count > 4) findings += $", and {report.Findings.Count - 4} more";
            summary += $" Findings to review: {findings}.";
        }
        else
        {
            summary += " The check returned no findings.";
        }
        return summary + " This is a read-only overview; it does not establish installation readiness.";
    }

    private static string YesNo(bool value) => value ? "yes" : "no";
    private static string OnOff(bool value) => value ? "on" : "off";

    private void AddTux(string text)
    {
        LastAssistantText = text;
        Messages.Add(new("Tux", text, true));
    }

    private void Changed([CallerMemberName] string? property = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
