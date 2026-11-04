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
internal sealed class OneChatViewModel(Func<CancellationToken, Task<CommandResult>> inspect,
    Func<CancellationToken, Task<CommandResult>> listDistros)
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
        else if (upper.Contains("DISTRIBUTION", StringComparison.Ordinal) ||
                 upper.Contains("DISTRO", StringComparison.Ordinal) ||
                 upper.Contains("LINUX OPTIONS", StringComparison.Ordinal) ||
                 (upper.Contains("LINUX", StringComparison.Ordinal) &&
                  (upper.Contains("AVAILABLE", StringComparison.Ordinal) ||
                   upper.Contains("SUPPORT", StringComparison.Ordinal) ||
                   upper.Contains("INSTALL", StringComparison.Ordinal))))
        {
            await ListDistributionsAsync(cancellationToken).ConfigureAwait(true);
        }
        else if (upper.Contains("RESULT", StringComparison.Ordinal) ||
                 upper.Contains("FIND", StringComparison.Ordinal) ||
                 upper.Contains("STATUS", StringComparison.Ordinal))
        {
            AddTux(_lastSystemSummary ?? "The system check has not completed yet.");
        }
        else
        {
            AddTux("I can run a read-only system check and list packaged Linux distributions. " +
                "Ask me to check this system, list distributions, or show the check results. " +
                "Broader conversation is not connected yet.");
        }
    }

    [SuppressMessage("Design", "CA1031:Do not catch general exception types",
        Justification = "The chat must report an unavailable read-only catalogue without crashing the WPF dispatcher.")]
    private async Task ListDistributionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            CommandResult result = await listDistros(cancellationToken).ConfigureAwait(true);
            if (!result.Succeeded)
            {
                AddTux("I could not load the distribution list. Restart iGloo One and try again.");
                return;
            }
            if (result.CapabilityId != "distro.list" || result.Data is null)
                throw new JsonException("Distribution listing returned an unexpected result.");
            DistroList catalogue = result.Data.Value.Deserialize<DistroList>(CommandProtocol.JsonOptions)
                ?? throw new JsonException("Distribution listing returned no catalogue.");
            if (catalogue.Distros is null || catalogue.Distros.Any(d =>
                    string.IsNullOrWhiteSpace(d.Id) || string.IsNullOrWhiteSpace(d.DisplayName)))
                throw new JsonException("Distribution listing returned an invalid catalogue.");
            var available = catalogue.Distros.Where(d =>
                    string.Equals(d.Status, "available", StringComparison.OrdinalIgnoreCase))
                .Select(d => d.DisplayName).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var developing = catalogue.Distros.Where(d =>
                    string.Equals(d.Status, "in-development", StringComparison.OrdinalIgnoreCase))
                .Select(d => d.DisplayName).Order(StringComparer.OrdinalIgnoreCase).ToArray();
            int comingSoon = catalogue.Distros.Count(d =>
                string.Equals(d.Status, "coming-soon", StringComparison.OrdinalIgnoreCase));
            var other = catalogue.Distros.Where(d =>
                    !string.Equals(d.Status, "available", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(d.Status, "in-development", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(d.Status, "coming-soon", StringComparison.OrdinalIgnoreCase))
                .Select(d => $"{d.DisplayName} ({(string.IsNullOrWhiteSpace(d.Status) ?
                    "status unavailable" : d.Status)})")
                .Order(StringComparer.OrdinalIgnoreCase).ToArray();
            var groups = new List<string>();
            if (available.Length > 0) groups.Add("Listed as available: " + string.Join(", ", available) + ".");
            if (developing.Length > 0) groups.Add("In development: " + string.Join(", ", developing) + ".");
            if (comingSoon > 0)
                groups.Add($"Coming soon: {comingSoon} provider{(comingSoon == 1 ? "" : "s")}.");
            if (other.Length > 0) groups.Add("Other provider statuses: " + string.Join(", ", other) + ".");
            if (groups.Count == 0)
            {
                AddTux("The CLI returned no distribution providers. Repair the iGloo One installation and try again.");
                return;
            }
            AddTux(string.Join(" ", groups) +
                " This is the packaged provider catalogue, not installation readiness for this device.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AddTux("Distribution lookup cancelled.");
        }
        catch (Exception error)
        {
            Debug.WriteLine(error);
            AddTux("I could not load the distribution list. Restart iGloo One and try again.");
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
        _lastSystemSummary = null;
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
