using Microsoft.FluentUI.AspNetCore.Components;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace Igloo.Fleet.Web.Presentation;

public enum ModuleFieldKind { Text, Enrollment, Status, DateTime, Count, Size, Percent, YesNo }

/// <summary>One column of a module page. <see cref="Key"/> is the ModuleStrings key suffix (Field_{Key}) and the CSV header.</summary>
public sealed record ModuleField(
    string Key,
    ModuleFieldKind Kind = ModuleFieldKind.Text,
    string Width = "160px",
    bool Optional = false,
    bool Editable = false,
    IReadOnlyList<string>? Choices = null,
    IReadOnlyList<string>? Statuses = null);

/// <summary>One module page: a list with optional details and an optional create dialog. Labels come from ShellStrings (Key) and ModuleStrings.</summary>
public sealed record ModulePageDefinition(
    string Module,
    string Slug,
    string Key,
    Func<Icon> Icon,
    IReadOnlyList<ModuleField> Fields,
    string? CreateKey = null,
    int SampleCount = 24)
{
    public string Href => Slug.Length == 0 ? "/" + Module : $"/{Module}/{Slug}";

    public string ExportPath => $"/export/modules/{Module}/{(Slug.Length == 0 ? "all" : Slug)}.csv";

    public ModuleField? StatusField =>
        Fields.FirstOrDefault(static f => f.Key == "Status") ?? Fields.FirstOrDefault(static f => f.Kind == ModuleFieldKind.Status);

    public IEnumerable<ModuleField> EditableFields => Fields.Where(static f => f.Editable);

    public ModuleField TitleField => Fields[0];
}

public sealed record ModuleGroup(string Key, Func<Icon> Icon, IReadOnlyList<ModulePageDefinition> Pages)
{
    public bool Standalone => Pages.Count == 1 && Pages[0].Slug.Length == 0;
}

/// <summary>Every module group and page in the left navigation after Audit events (console-design.md, section 3).</summary>
internal static class ModuleCatalog
{
    private static ModuleField T(string key, string width = "160px", bool editable = false, bool optional = false) =>
        new(key, ModuleFieldKind.Text, width, optional, editable);

    private static ModuleField C(string key, string width, params string[] choices) =>
        new(key, ModuleFieldKind.Text, width, Editable: true, Choices: choices);

    private static ModuleField E() => new("Enrollment", ModuleFieldKind.Enrollment, "150px");

    private static ModuleField S(string key, params string[] statuses) =>
        new(key, ModuleFieldKind.Status, "150px", Statuses: statuses);

    private static ModuleField D(string key, bool optional = false) => new(key, ModuleFieldKind.DateTime, "230px", optional);

    private static ModuleField N(string key, bool optional = false) => new(key, ModuleFieldKind.Count, "110px", optional);

    private static ModuleField Z(string key, bool optional = false) => new(key, ModuleFieldKind.Size, "110px", optional);

    private static ModuleField P(string key, bool optional = false) => new(key, ModuleFieldKind.Percent, "110px", optional);

    private static ModuleField B(string key, bool optional = false) => new(key, ModuleFieldKind.YesNo, "100px", optional);

    public static readonly IReadOnlyList<ModuleGroup> Groups =
    [
        new("Inventory", static () => new Icons.Regular.Size20.Board(),
        [
            new("inventory", "hardware", "InventoryHardware", static () => new Icons.Regular.Size20.DeveloperBoard(),
                [E(), T("Vendor", "140px"), T("Model", "200px"), T("Cpu", "220px"), N("Cores"), Z("Memory"), T("Firmware", "140px", optional: true), S("Tpm", "Present", "Absent"), S("SecureBoot", "Enabled", "Disabled", "Unknown")], SampleCount: 8),
            new("inventory", "os", "InventoryOperatingSystems", static () => new Icons.Regular.Size20.DesktopTower(),
                [E(), T("Distribution", "190px"), T("Version", "110px"), T("Kernel", "170px"), T("Architecture", "120px"), T("Boot", "100px", optional: true), D("Updated"), S("Status", "Supported", "EndOfLife", "Unknown")], SampleCount: 8),
            new("inventory", "network", "InventoryNetwork", static () => new Icons.Regular.Size20.PlugConnected(),
                [E(), T("Interface", "120px"), T("Mac", "170px"), T("Address", "170px"), T("Speed", "110px"), B("Dhcp"), S("Status", "Up", "Down")]),
            new("inventory", "agent-versions", "InventoryAgentVersions", static () => new Icons.Regular.Size20.Box(),
                [T("AgentVersion", "140px"), T("Channel", "120px"), N("Enrollments"), D("ReleasedOn"), S("Status", "Current", "Outdated", "Unsupported")], SampleCount: 6),
        ]),
        new("Cohorts", static () => new Icons.Regular.Size20.PeopleTeam(),
        [
            new("cohorts", "", "Cohorts", static () => new Icons.Regular.Size20.PeopleTeam(),
                [T("Name", "220px", editable: true), T("Description", "300px", editable: true), N("Members"), T("Rule", "260px", editable: true), T("Owner", "160px"), D("Updated"), S("Status", "Active", "Draft")], "CreateCohort", 10),
        ]),
        new("Software", static () => new Icons.Regular.Size20.AppsList(),
        [
            new("software", "applications", "SoftwareApplications", static () => new Icons.Regular.Size20.Apps(),
                [T("Name", "220px", editable: true), T("Version", "110px", editable: true), T("Publisher", "180px", editable: true), C("Category", "150px", "Productivity", "Development", "Browser", "Communication", "Utilities"), N("Enrollments"), S("Status", "Installed", "Available", "Blocked")], "AddApplication"),
            new("software", "packages", "SoftwarePackages", static () => new Icons.Regular.Size20.Box(),
                [T("Package", "220px"), T("Version", "140px"), T("Architecture", "110px"), T("Repository", "160px"), Z("Size"), N("Enrollments"), S("Status", "Installed", "Upgradable", "Held")], SampleCount: 40),
            new("software", "repositories", "SoftwareRepositories", static () => new Icons.Regular.Size20.Server(),
                [T("Name", "200px", editable: true), T("Address", "320px", editable: true), C("Distribution", "160px", "Debian", "Fedora KDE", "Linux Mint Cinnamon"), T("Suite", "120px"), B("Signed"), D("Updated"), S("Status", "Enabled", "Disabled")], "AddRepository", 8),
            new("software", "deployment", "SoftwareDeployments", static () => new Icons.Regular.Size20.ArrowDownload(),
                [T("Name", "220px", editable: true), T("Package", "180px", editable: true), T("Target", "180px", editable: true), T("Schedule", "160px"), N("Enrollments"), P("Progress"), S("Status", "Scheduled", "InProgress", "Succeeded", "Failed")], "CreateDeployment", 12),
            new("software", "installed", "SoftwareInstalled", static () => new Icons.Regular.Size20.TaskListSquareLtr(),
                [E(), T("Package", "220px"), T("Version", "140px"), D("Installed"), T("Source", "160px"), Z("Size")], SampleCount: 40),
        ]),
        new("Updates", static () => new Icons.Regular.Size20.ArrowSync(),
        [
            new("updates", "available", "UpdatesAvailable", static () => new Icons.Regular.Size20.ArrowDownload(),
                [T("Package", "220px"), T("Version", "140px"), S("Severity", "Critical", "Important", "Moderate", "Low"), N("Enrollments"), D("ReleasedOn"), B("Reboot"), S("Status", "Pending", "Approved", "Deferred")], SampleCount: 30),
            new("updates", "security", "UpdatesSecurity", static () => new Icons.Regular.Size20.ShieldError(),
                [T("Cve", "160px"), T("Package", "200px"), S("Severity", "Critical", "Important", "Moderate", "Low"), N("Enrollments"), D("ReleasedOn"), S("Status", "Pending", "Approved", "Installed")], SampleCount: 20),
            new("updates", "rings", "UpdatesRings", static () => new Icons.Regular.Size20.Layer(),
                [T("Name", "200px", editable: true), N("Members"), C("Deferral", "120px", "0 days", "3 days", "7 days", "14 days", "30 days"), T("Window", "200px"), D("Updated"), S("Status", "Active", "Paused")], "CreateRing", 5),
            new("updates", "deployment", "UpdatesDeploymentStatus", static () => new Icons.Regular.Size20.DataBarVertical(),
                [E(), T("Ring", "140px"), N("Pending"), N("Installed"), N("Failed"), D("LastRun"), S("Status", "UpToDate", "Pending", "Failed")], SampleCount: 8),
            new("updates", "history", "UpdatesHistory", static () => new Icons.Regular.Size20.History(),
                [E(), T("Package", "220px"), T("Version", "140px"), D("Installed"), S("Result", "Succeeded", "Failed"), T("Duration", "100px")], SampleCount: 40),
        ]),
        new("Configuration", static () => new Icons.Regular.Size20.Options(),
        [
            new("configuration", "policies", "ConfigurationPolicies", static () => new Icons.Regular.Size20.ClipboardTextLtr(),
                [T("Name", "240px", editable: true), C("Category", "150px", "Baseline", "Hardening", "Logging", "Updates", "Access"), C("Scope", "150px", "All enrollments", "Pilot ring", "Broad ring", "Servers"), N("Assigned"), D("Updated"), T("Owner", "160px"), S("Status", "Assigned", "Draft", "Disabled")], "CreatePolicy", 14),
            new("configuration", "profiles", "ConfigurationProfiles", static () => new Icons.Regular.Size20.DocumentSettings(),
                [T("Name", "240px", editable: true), C("Type", "150px", "Desktop", "Kiosk", "Developer", "Server"), N("Assigned"), P("Compliance"), D("Updated"), S("Status", "Assigned", "Draft")], "CreateProfile", 10),
            new("configuration", "scripts", "ConfigurationScripts", static () => new Icons.Regular.Size20.Code(),
                [T("Name", "240px", editable: true), C("Runtime", "120px", "bash", "python3", "sh"), T("Target", "160px"), D("LastRun"), S("Result", "Succeeded", "Failed", "NotRun"), T("Owner", "160px")], "CreateScript", 12),
            new("configuration", "environment", "ConfigurationEnvironment", static () => new Icons.Regular.Size20.BracesVariable(),
                [E(), T("Setting", "220px"), T("Value", "260px"), T("Scope", "120px"), D("Updated")], SampleCount: 30),
            new("configuration", "services", "ConfigurationServices", static () => new Icons.Regular.Size20.Settings(),
                [E(), T("Unit", "200px"), T("Description", "260px"), S("Status", "Running", "Stopped", "Failed"), T("Uptime", "110px"), T("Mode", "110px")], SampleCount: 36),
            new("configuration", "scheduled-jobs", "ConfigurationScheduledJobs", static () => new Icons.Regular.Size20.CalendarClock(),
                [T("Name", "220px"), E(), T("Schedule", "160px"), D("LastRun"), D("NextRun"), S("Status", "Scheduled", "Running", "Failed")], SampleCount: 18),
            new("configuration", "ssh", "ConfigurationSsh", static () => new Icons.Regular.Size20.Key(),
                [E(), T("Setting", "220px"), T("Value", "140px"), T("Baseline", "140px"), S("Status", "Compliant", "NonCompliant")], SampleCount: 30),
        ]),
        new("Security", static () => new Icons.Regular.Size20.ShieldLock(),
        [
            new("security", "posture", "SecurityPosture", static () => new Icons.Regular.Size20.ShieldCheckmark(),
                [E(), N("Checks"), N("Passed"), N("Failed"), P("Score"), D("Updated"), S("Status", "Healthy", "AtRisk", "Critical")], SampleCount: 8),
            new("security", "disk-encryption", "SecurityDiskEncryption", static () => new Icons.Regular.Size20.HardDrive(),
                [E(), T("Device", "160px"), T("Cipher", "180px"), T("Mode", "100px"), S("Status", "Encrypted", "NotEncrypted", "Unknown")], SampleCount: 8),
            new("security", "secure-boot", "SecuritySecureBoot", static () => new Icons.Regular.Size20.ShieldTask(),
                [E(), T("Firmware", "160px"), S("Status", "Enabled", "Disabled", "Unknown"), S("Tpm", "Present", "Absent"), D("Updated")], SampleCount: 8),
            new("security", "firewall", "SecurityFirewall", static () => new Icons.Regular.Size20.Shield(),
                [E(), T("Zone", "130px"), N("Rules"), T("Mode", "120px"), D("Updated"), S("Status", "Active", "Inactive")], SampleCount: 8),
            new("security", "ssh", "SecuritySsh", static () => new Icons.Regular.Size20.WindowConsole(),
                [E(), N("Port"), B("PasswordAuth"), T("RootLogin", "160px"), N("Keys"), S("Status", "Compliant", "NonCompliant")], SampleCount: 8),
            new("security", "accounts", "SecurityAccounts", static () => new Icons.Regular.Size20.People(),
                [E(), T("Account", "160px"), N("Uid"), T("Shell", "140px"), D("LastSignIn"), S("Status", "Active", "Locked")], SampleCount: 30),
            new("security", "sudo", "SecuritySudo", static () => new Icons.Regular.Size20.PersonLock(),
                [E(), T("Rule", "280px"), T("Account", "160px"), T("Scope", "120px"), S("Status", "Compliant", "NonCompliant", "Review")], SampleCount: 20),
            new("security", "certificates", "SecurityCertificates", static () => new Icons.Regular.Size20.Certificate(),
                [T("Subject", "240px"), T("Issuer", "200px"), E(), D("Expires"), T("Fingerprint", "180px", optional: true), S("Status", "Valid", "ExpiresSoon", "Expired")], SampleCount: 20),
            new("security", "compliance", "SecurityCompliance", static () => new Icons.Regular.Size20.ClipboardCheckmark(),
                [T("Baseline", "240px"), N("Enrollments"), P("Compliance"), D("Updated"), S("Status", "Compliant", "NonCompliant", "NotEvaluated")], SampleCount: 8),
        ]),
        new("Storage", static () => new Icons.Regular.Size20.HardDrive(),
        [
            new("storage", "disks", "StorageDisks", static () => new Icons.Regular.Size20.HardDrive(),
                [E(), T("Device", "150px"), T("Model", "220px"), Z("Capacity"), T("Type", "100px"), S("Status", "Healthy", "Warning", "Failed")]),
            new("storage", "partitions", "StoragePartitions", static () => new Icons.Regular.Size20.DataBarHorizontal(),
                [E(), T("Device", "170px"), T("Type", "130px"), Z("Size"), T("Filesystem", "110px"), T("MountPoint", "160px")], SampleCount: 36),
            new("storage", "filesystems", "StorageFilesystems", static () => new Icons.Regular.Size20.FolderOpen(),
                [E(), T("Filesystem", "110px"), T("Device", "170px"), Z("Capacity"), P("Used"), S("Status", "Healthy", "Full", "ReadOnly")], SampleCount: 30),
            new("storage", "mounts", "StorageMounts", static () => new Icons.Regular.Size20.Pin(),
                [E(), T("MountPoint", "160px"), T("Device", "170px"), T("Filesystem", "110px"), T("Options", "220px"), S("Status", "Mounted", "NotMounted")], SampleCount: 30),
            new("storage", "lvm", "StorageLvm", static () => new Icons.Regular.Size20.Stack(),
                [E(), T("VolumeGroup", "160px"), T("Name", "160px"), Z("Size"), P("Used"), S("Status", "Active", "Inactive")], SampleCount: 16),
            new("storage", "raid", "StorageRaid", static () => new Icons.Regular.Size20.ServerMultiple(),
                [E(), T("Device", "120px"), T("Level", "100px"), N("Members"), Z("Capacity"), S("Status", "Clean", "Degraded", "Rebuilding")], SampleCount: 6),
            new("storage", "encryption", "StorageEncryption", static () => new Icons.Regular.Size20.LockClosed(),
                [E(), T("Device", "170px"), T("Cipher", "180px"), T("Unlock", "140px"), S("Status", "Encrypted", "NotEncrypted")], SampleCount: 8),
        ]),
        new("Networking", static () => new Icons.Regular.Size20.NetworkCheck(),
        [
            new("networking", "interfaces", "NetworkingInterfaces", static () => new Icons.Regular.Size20.PlugConnected(),
                [E(), T("Interface", "120px"), T("Type", "110px"), T("Mac", "170px"), T("Speed", "110px"), S("Status", "Up", "Down")]),
            new("networking", "addresses", "NetworkingAddresses", static () => new Icons.Regular.Size20.LocationRipple(),
                [E(), T("Interface", "120px"), T("Address", "170px"), T("Gateway", "150px"), B("Dhcp"), S("Status", "Assigned", "Conflict")]),
            new("networking", "routes", "NetworkingRoutes", static () => new Icons.Regular.Size20.ArrowRouting(),
                [E(), T("Destination", "170px"), T("Gateway", "150px"), T("Interface", "120px"), N("Metric"), T("Protocol", "100px")], SampleCount: 30),
            new("networking", "dns", "NetworkingDns", static () => new Icons.Regular.Size20.Globe(),
                [E(), T("Server", "150px"), T("Domain", "200px"), T("Source", "120px"), T("Latency", "100px"), S("Status", "Reachable", "Unreachable")]),
            new("networking", "firewall", "NetworkingFirewall", static () => new Icons.Regular.Size20.Shield(),
                [E(), T("Zone", "130px"), N("Port"), T("Protocol", "100px"), T("Action", "100px"), S("Status", "Active", "Inactive")], SampleCount: 30),
            new("networking", "connectivity", "NetworkingConnectivity", static () => new Icons.Regular.Size20.Wifi1(),
                [E(), T("Target", "220px"), T("Latency", "100px"), P("PacketLoss"), D("Checked"), S("Status", "Reachable", "Degraded", "Unreachable")]),
        ]),
        new("Monitoring", static () => new Icons.Regular.Size20.DataTrending(),
        [
            new("monitoring", "health", "MonitoringHealth", static () => new Icons.Regular.Size20.HeartPulse(),
                [E(), T("Load", "130px"), P("Cpu"), P("Memory"), P("Disk"), T("Uptime", "110px"), S("Status", "Healthy", "Degraded", "Critical")], SampleCount: 8),
            new("monitoring", "cpu-memory", "MonitoringCpuMemory", static () => new Icons.Regular.Size20.Gauge(),
                [E(), P("Cpu"), P("Memory"), P("Swap"), T("Load", "130px"), D("Updated")], SampleCount: 8),
            new("monitoring", "storage", "MonitoringStorage", static () => new Icons.Regular.Size20.HardDrive(),
                [E(), T("MountPoint", "160px"), Z("Capacity"), P("Used"), T("Growth", "120px"), S("Status", "Healthy", "Warning", "Critical")], SampleCount: 30),
            new("monitoring", "network", "MonitoringNetwork", static () => new Icons.Regular.Size20.Router(),
                [E(), T("Interface", "120px"), Z("Received"), Z("Sent"), N("Errors"), S("Status", "Healthy", "Degraded")]),
            new("monitoring", "services", "MonitoringServices", static () => new Icons.Regular.Size20.Settings(),
                [E(), T("Unit", "200px"), S("Status", "Running", "Stopped", "Failed"), N("Restarts"), T("Uptime", "110px")], SampleCount: 36),
            new("monitoring", "events", "MonitoringEvents", static () => new Icons.Regular.Size20.Flash(),
                [D("Occurred"), E(), T("Source", "160px"), S("Severity", "Critical", "Warning", "Information"), T("Message", "360px")], SampleCount: 40),
            new("monitoring", "logs", "MonitoringLogs", static () => new Icons.Regular.Size20.DocumentText(),
                [D("Occurred"), E(), T("Unit", "180px"), S("Level", "Error", "Warning", "Info"), T("Message", "360px")], SampleCount: 40),
            new("monitoring", "alerts", "MonitoringAlerts", static () => new Icons.Regular.Size20.Alert(),
                [T("Name", "240px"), E(), S("Severity", "Critical", "Warning", "Information"), D("Raised"), T("Owner", "160px"), S("Status", "Open", "Acknowledged", "Resolved")], SampleCount: 14),
        ]),
        new("Automation", static () => new Icons.Regular.Size20.Flash(),
        [
            new("automation", "scripts", "AutomationScripts", static () => new Icons.Regular.Size20.Code(),
                [T("Name", "240px", editable: true), C("Runtime", "120px", "bash", "python3", "sh"), T("Author", "160px"), D("Updated"), N("Runs"), S("Status", "Published", "Draft")], "CreateScript", 12),
            new("automation", "jobs", "AutomationJobs", static () => new Icons.Regular.Size20.Play(),
                [T("Name", "240px", editable: true), T("Script", "200px", editable: true), T("Target", "160px", editable: true), T("Schedule", "160px"), D("LastRun"), S("Status", "Enabled", "Disabled")], "CreateJob", 12),
            new("automation", "schedules", "AutomationSchedules", static () => new Icons.Regular.Size20.CalendarLtr(),
                [T("Name", "220px", editable: true), T("Schedule", "180px", editable: true), D("NextRun"), N("Jobs"), S("Status", "Enabled", "Paused")], "CreateSchedule", 8),
            new("automation", "history", "AutomationRunHistory", static () => new Icons.Regular.Size20.History(),
                [T("Job", "220px"), E(), D("Start"), T("Duration", "100px"), N("ExitCode"), S("Result", "Succeeded", "Failed", "Cancelled")], SampleCount: 40),
            new("automation", "templates", "AutomationTemplates", static () => new Icons.Regular.Size20.DocumentCopy(),
                [T("Name", "240px", editable: true), C("Category", "150px", "Baseline", "Hardening", "Housekeeping", "Diagnostics"), T("Author", "160px"), D("Updated"), N("Uses")], "CreateTemplate", 10),
            new("automation", "maintenance-windows", "AutomationMaintenanceWindows", static () => new Icons.Regular.Size20.CalendarClock(),
                [T("Name", "220px", editable: true), T("Schedule", "180px", editable: true), T("Duration", "100px"), N("Members"), D("NextRun"), S("Status", "Active", "Paused")], "CreateMaintenanceWindow", 6),
        ]),
        new("Reports", static () => new Icons.Regular.Size20.DocumentTable(),
        [
            new("reports", "inventory", "ReportsInventory", static () => new Icons.Regular.Size20.Board(), ReportFields(), SampleCount: 8),
            new("reports", "compliance", "ReportsCompliance", static () => new Icons.Regular.Size20.ClipboardCheckmark(), ReportFields(), SampleCount: 8),
            new("reports", "updates", "ReportsUpdates", static () => new Icons.Regular.Size20.ArrowSync(), ReportFields(), SampleCount: 8),
            new("reports", "security", "ReportsSecurity", static () => new Icons.Regular.Size20.ShieldLock(), ReportFields(), SampleCount: 8),
            new("reports", "migrations", "ReportsMigrations", static () => new Icons.Regular.Size20.ArrowSwap(), ReportFields(), SampleCount: 8),
            new("reports", "custom", "ReportsCustom", static () => new Icons.Regular.Size20.DocumentAdd(),
                [T("Name", "240px", editable: true), C("Source", "160px", "Enrollments", "Evidence", "Updates", "Compliance", "Audit events"), T("Schedule", "160px", editable: true), D("Generated"), T("Format", "100px"), T("Owner", "160px"), S("Status", "Scheduled", "Draft")], "CreateReport", 6),
        ]),
    ];

    private static ModuleField[] ReportFields() =>
        [T("Name", "260px"), T("Scope", "180px"), D("Generated"), N("Rows"), T("Format", "100px"), T("Author", "160px")];

    public static IEnumerable<ModulePageDefinition> AllPages() => Groups.SelectMany(static g => g.Pages);

    public static ModulePageDefinition? Find(string module, string? slug) =>
        AllPages().FirstOrDefault(p =>
            string.Equals(p.Module, module, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(p.Slug, slug ?? string.Empty, StringComparison.OrdinalIgnoreCase));

    public static ModuleGroup? GroupOf(ModulePageDefinition page) => Groups.FirstOrDefault(g => g.Pages.Contains(page));

    public static ModuleGroup? GroupFor(string relativePath)
    {
        var path = "/" + relativePath.TrimStart('/');
        return Groups.FirstOrDefault(g => g.Pages.Any(p => path.StartsWith(p.Href, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>Every status token any module page can show. StatusStrings holds Module_{token}.</summary>
    public static IEnumerable<string> AllStatusTokens() =>
        AllPages().SelectMany(static p => p.Fields).Where(static f => f.Statuses is not null).SelectMany(static f => f.Statuses!).Distinct(StringComparer.Ordinal);

    public static IEnumerable<string> AllFieldKeys() =>
        AllPages().SelectMany(static p => p.Fields).Select(static f => f.Key).Distinct(StringComparer.Ordinal);

    public static IEnumerable<string> AllCreateKeys() =>
        AllPages().Where(static p => p.CreateKey is not null).Select(static p => p.CreateKey!).Distinct(StringComparer.Ordinal);
}
