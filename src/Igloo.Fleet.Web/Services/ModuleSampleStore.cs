#pragma warning disable CA5394 // Sample rows are illustrative; the generator is seeded for determinism, not for security.
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Igloo.Fleet.Web.Presentation;

namespace Igloo.Fleet.Web.Services;

/// <summary>One row of a module page. Values are typed by the field kind: string, Guid (enrollment), DateTimeOffset, int, long (bytes), double (percent), bool.</summary>
public sealed record ModuleRow(Guid Id, IReadOnlyDictionary<string, object?> Values)
{
    public object? this[string key] => Values.GetValueOrDefault(key);

    public string SortKey(string key) => Values.GetValueOrDefault(key) switch
    {
        null => string.Empty,
        DateTimeOffset d => d.ToString("o", CultureInfo.InvariantCulture),
        long l => l.ToString("D19", CultureInfo.InvariantCulture),
        int i => i.ToString("D10", CultureInfo.InvariantCulture),
        double p => ((long)(p * 100)).ToString("D10", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        Guid g => g.ToString("D"),
        var o => o.ToString() ?? string.Empty,
    };

    public string Export(string key) => Values.GetValueOrDefault(key) switch
    {
        null => string.Empty,
        DateTimeOffset d => ExportEndpoints.Iso(d),
        double p => p.ToString("0.0", CultureInfo.InvariantCulture),
        Guid g => g.ToString("D"),
        var o => Convert.ToString(o, CultureInfo.InvariantCulture) ?? string.Empty,
    };
}

/// <summary>Rows for module pages. Null means this Fleet Server provides no data for the page.</summary>
internal interface IModuleDataSource
{
    bool IsSampleData { get; }

    IReadOnlyList<ModuleRow>? Rows(ModulePageDefinition page);

    ModuleRow? Find(ModulePageDefinition page, Guid id);

    ModuleRow Add(ModulePageDefinition page, IReadOnlyDictionary<string, string> values, string createdBy);
}

/// <summary>Outside Development there is no module data source yet; pages state that instead of showing an empty grid.</summary>
internal sealed class UnavailableModuleDataSource : IModuleDataSource
{
    public bool IsSampleData => false;

    public IReadOnlyList<ModuleRow>? Rows(ModulePageDefinition page) => null;

    public ModuleRow? Find(ModulePageDefinition page, Guid id) => null;

    public ModuleRow Add(ModulePageDefinition page, IReadOnlyDictionary<string, string> values, string createdBy) =>
        throw new InvalidOperationException("Module data is not available on this Fleet Server.");
}

/// <summary>Deterministic sample rows for every module page (Development only). Enrollment columns reference the sample enrollments.</summary>
internal sealed class ModuleSampleStore(SampleFleetData data) : IModuleDataSource
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, List<ModuleRow>> _rows = new(StringComparer.Ordinal);
    private readonly Guid[] _enrollments = [.. data.Enrollments.Keys.OrderBy(static id => id)];

    public bool IsSampleData => true;

    public IReadOnlyList<ModuleRow>? Rows(ModulePageDefinition page)
    {
        ArgumentNullException.ThrowIfNull(page);
        lock (_gate)
        {
            if (!_rows.TryGetValue(page.Key, out var rows))
            {
                rows = Generate(page);
                _rows[page.Key] = rows;
            }

            return rows.ToArray();
        }
    }

    public ModuleRow? Find(ModulePageDefinition page, Guid id) => Rows(page)!.FirstOrDefault(r => r.Id == id);

    public ModuleRow Add(ModulePageDefinition page, IReadOnlyDictionary<string, string> values, string createdBy)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(values);
        lock (_gate)
        {
            var rows = Rows(page)!;
            var now = DateTimeOffset.UtcNow;
            var dictionary = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var field in page.Fields)
            {
                dictionary[field.Key] = field.Editable && values.TryGetValue(field.Key, out var text) && text.Length > 0
                    ? text.Trim()
                    : DefaultFor(field, now, createdBy);
            }

            var row = new ModuleRow(Guid.NewGuid(), dictionary);
            _rows[page.Key] = [row, .. rows];
            return row;
        }
    }

    private static object? DefaultFor(ModuleField field, DateTimeOffset now, string createdBy) => field.Kind switch
    {
        ModuleFieldKind.Status => field.Statuses!.Contains("Draft") ? "Draft" : field.Statuses![0],
        ModuleFieldKind.DateTime => field.Key is "NextRun" or "ReleasedOn" or "Expires" ? now.AddDays(1) : field.Key is "LastRun" or "Generated" or "Raised" ? null : now,
        ModuleFieldKind.Count => 0,
        ModuleFieldKind.Size => 0L,
        ModuleFieldKind.Percent => 0d,
        ModuleFieldKind.YesNo => false,
        ModuleFieldKind.Enrollment => null,
        _ => field.Key is "Owner" or "Author" ? createdBy : field.Key is "Format" ? "CSV" : field.Key is "Duration" ? "2 h" : string.Empty,
    };

    private List<ModuleRow> Generate(ModulePageDefinition page)
    {
        var rng = new Random(Seed(page.Key));
        var now = data.Now;
        var rows = new List<ModuleRow>(page.SampleCount);
        for (var i = 0; i < page.SampleCount; i++)
        {
            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var field in page.Fields)
            {
                values[field.Key] = Value(page, field, i, rng, now, values);
            }

            rows.Add(new ModuleRow(SampleFleetData.Id(900 + Math.Abs(Seed(page.Key) % 90), i), values));
        }

        return rows;
    }

    private object? Value(ModulePageDefinition page, ModuleField field, int index, Random rng, DateTimeOffset now, Dictionary<string, object?> row)
    {
        if (field.Kind == ModuleFieldKind.Enrollment)
        {
            if (_enrollments.Length == 0)
            {
                return null;
            }

            // Pages with one row per enrollment walk the enrollments in order; longer pages spread rows over them.
            return page.SampleCount <= _enrollments.Length ? _enrollments[index % _enrollments.Length] : _enrollments[(index * 7 + rng.Next(3)) % _enrollments.Length];
        }

        if (field.Kind == ModuleFieldKind.Status)
        {
            return Derived(page, field, row, now) ?? Weighted(field.Statuses!, rng);
        }

        if (field.Choices is not null)
        {
            return Pick(field.Choices, rng);
        }

        return field.Key switch
        {
            "Vendor" => Pick(["Dell", "Lenovo", "HP", "Framework", "Tuxedo"], rng, index),
            "Model" when page.Module == "inventory" => Pick(["Latitude 5540", "ThinkPad T14 Gen 4", "EliteBook 840 G10", "Laptop 13", "InfinityBook Pro 14", "OptiPlex 7010", "ThinkCentre M90q"], rng, index),
            "Model" => Pick(["Samsung PM9A1 512 GB", "WD SN740 1 TB", "Micron 2450 512 GB", "KIOXIA XG8 1 TB", "Seagate IronWolf 4 TB"], rng),
            "Cpu" when field.Kind == ModuleFieldKind.Text => Pick(["Intel Core i7-1365U", "AMD Ryzen 7 PRO 7840U", "Intel Core i5-13500T", "Intel Core Ultra 7 155H", "AMD Ryzen 5 PRO 7540U"], rng, index),
            "Cores" => Pick([8, 12, 16, 10, 6], rng),
            "Memory" when field.Kind == ModuleFieldKind.Size => Pick([16L, 32L, 64L, 8L], rng) * 1024L * 1024L * 1024L,
            "Firmware" => Pick(["1.12.0", "N3QET42W", "U87 Ver. 01.08.00", "03.05", "2.1.9"], rng),
            "Distribution" => Pick(["Debian", "Fedora KDE", "Linux Mint Cinnamon"], rng, index),
            "Version" when page.Module == "inventory" => row["Distribution"] switch { "Debian" => "13", "Fedora KDE" => "42", _ => "22.1" },
            "Version" => $"{rng.Next(1, 130)}.{rng.Next(0, 20)}.{rng.Next(0, 12)}",
            "Kernel" => row.GetValueOrDefault("Distribution") switch { "Debian" => "6.12.22-amd64", "Fedora KDE" => "6.14.6-300.fc42", _ => "6.8.0-59-generic" },
            "Architecture" when page.Module == "inventory" => rng.Next(10) == 0 ? "aarch64" : "x86_64",
            "Architecture" => Pick(["amd64", "amd64", "amd64", "all", "noarch"], rng),
            "Boot" => "UEFI",
            "Interface" => Pick(["enp0s31f6", "wlp0s20f3", "enp3s0", "wlan0", "eth0", "wlp2s0"], rng, index),
            "Mac" => $"3C:97:0E:{rng.Next(256):X2}:{rng.Next(256):X2}:{rng.Next(256):X2}",
            "Address" when page.Module == "software" => Pick(["https://deb.debian.org/debian", "https://security.debian.org/debian-security", "https://mirrors.fedoraproject.org/metalink?repo=updates-released-f42", "https://packages.linuxmint.com", "https://mirror.corp.example/debian", "https://dl.flathub.org/repo", "https://mirror.corp.example/fedora", "https://archive.ubuntu.com/ubuntu"], rng, index),
            "Address" => $"10.20.{rng.Next(1, 40)}.{rng.Next(10, 250)}/24",
            "Gateway" => $"10.20.{rng.Next(1, 40)}.1",
            "Speed" => Pick(["1 Gbit/s", "2.5 Gbit/s", "866 Mbit/s", "10 Gbit/s", "100 Mbit/s"], rng),
            "AgentVersion" => Pick(["1.4.0", "1.3.2", "1.2.0", "1.1.5", "1.0.9", "0.9.4"], rng, index),
            "Channel" => index < 2 ? "stable" : Pick(["stable", "stable", "beta"], rng),
            "Name" => Name(page, rng, index),
            "Description" when page.Module == "cohorts" => Pick(["Workstations in the pilot ring.", "Desktops managed by Finance.", "Laptops that leave the office.", "Machines with developer tooling.", "Terminals in public areas."], rng, index),
            "Description" => Pick(["OpenBSD Secure Shell server", "Network connection manager", "Network time synchronization", "CUPS scheduler", "Netfilter tables", "Regular background program processing daemon", "System logging service", "Unattended upgrades", "iGloo Fleet agent", "Simple desktop display manager"], rng, index),
            "Members" => rng.Next(2, 40),
            "Rule" when page.Module == "cohorts" => Pick(["trust = Active", "agentVersion >= 1.3", "capability has PreflightV1", "profile = Debian baseline", "lastContact < 7 days", "distribution = Fedora KDE"], rng, index),
            "Rule" => Pick(["%sudo ALL=(ALL:ALL) ALL", "%wheel ALL=(ALL) ALL", "svc-backup ALL=(root) NOPASSWD: /usr/bin/rsync", "kiosk ALL=(root) NOPASSWD: /usr/bin/systemctl restart kiosk", "mpeeters ALL=(ALL) ALL", "Defaults use_pty", "Defaults logfile=/var/log/sudo.log"], rng, index),
            "Owner" or "Author" => Pick(["engineering-operator", "m.peeters", "j.claes", "s.vandenberg", "a.janssens"], rng),
            "Publisher" => Publisher(row["Name"] as string),
            "Enrollments" or "Assigned" => rng.Next(0, Math.Max(1, _enrollments.Length + 1)),
            "Package" => Pick(Packages, rng, index),
            "Repository" => Pick(["deb.debian.org trixie", "security.debian.org", "updates.fedoraproject.org", "packages.linuxmint.com", "mirror.corp.example"], rng),
            "Size" or "Capacity" or "Received" or "Sent" => Bytes(field.Key, rng),
            "Suite" => Pick(["trixie", "trixie-security", "f42", "xia", "noble", "stable"], rng, index),
            "Signed" => rng.Next(8) != 0,
            "Target" when page.Module == "networking" => Pick(["fleet.corp.example:8443", "deb.debian.org", "mirrors.fedoraproject.org", "proxy.corp.example:3128", "ntp.corp.example", "10.20.0.53"], rng, index),
            "Target" => Pick(["Pilot ring", "Broad ring", "Finance desktops", "Field laptops", "Developer machines", "All enrollments"], rng),
            "Schedule" when page.Module == "automation" && page.Slug == "schedules" => Pick(["0 2 * * *", "0 3 * * 0", "0 * * * *", "0 1 1-7 * 2", "0 8-18 * * 1-5", "30 23 * * 5"], rng, index),
            "Schedule" => Pick(["Daily 02:00 UTC", "Weekly Sun 03:00 UTC", "Hourly", "Next maintenance window", "Immediately", "Monthly, first Tuesday"], rng, index),
            "Progress" => rng.Next(0, 101) / 1.0,
            "Installed" when field.Kind == ModuleFieldKind.Count => rng.Next(0, 60),
            "Cve" => $"CVE-2026-{rng.Next(1000, 48000):D5}",
            "Reboot" => rng.Next(4) == 0,
            "Deferral" => Pick(["0 days", "3 days", "7 days", "14 days", "30 days"], rng, index),
            "Window" => Pick(["Tue 02:00-04:00 UTC", "Sat 06:00-10:00 UTC", "Daily 01:00-03:00 UTC", "Sun 00:00-06:00 UTC"], rng, index),
            "Ring" => Pick(["Pilot", "Early", "Broad", "Servers", "Kiosks"], rng),
            "Failed" when page.Slug == "posture" => 24 - (int)row["Passed"]!,
            "Pending" or "Failed" or "Errors" or "Restarts" => rng.Next(0, 7),
            "Duration" => $"{rng.Next(0, 14)} min {rng.Next(1, 60)} s",
            "Category" => Pick(["Baseline", "Hardening", "Logging", "Updates", "Access"], rng),
            "Scope" when page.Module == "security" || page.Module == "configuration" && page.Slug == "environment" => Pick(["System", "User", "Group"], rng),
            "Scope" => Pick(["All enrollments", "Pilot ring", "Broad ring", "Servers", "Finance desktops"], rng),
            "Type" when page.Module == "storage" && page.Slug == "disks" => Pick(["NVMe", "NVMe", "SSD", "HDD"], rng),
            "Type" when page.Module == "storage" => Pick(["EFI System", "Linux filesystem", "Linux swap", "Linux LVM", "Linux RAID"], rng, index),
            "Type" when page.Module == "networking" => Pick(["Ethernet", "Wi-Fi", "Ethernet"], rng),
            "Type" => Pick(["Desktop", "Kiosk", "Developer", "Server"], rng),
            "Compliance" or "Score" => Math.Round(55 + rng.NextDouble() * 45, 1),
            "Runtime" => Pick(["bash", "python3", "sh"], rng),
            "Setting" when page.Slug == "ssh" => Pick(SshSettings, rng, index),
            "Setting" => Pick(["http_proxy", "https_proxy", "no_proxy", "LANG", "TZ", "EDITOR", "JAVA_HOME", "XDG_SESSION_TYPE"], rng, index),
            "Value" when page.Slug == "ssh" => SshValue(row["Setting"] as string, rng, false),
            "Baseline" when page.Slug == "ssh" => SshValue(row["Setting"] as string, rng, true),
            "Baseline" => Pick(["CIS Debian 12 Level 1", "CIS Fedora Level 1", "Corp hardening v3", "SSH baseline", "Disk encryption required", "Secure Boot required", "Update currency 14 days", "Firewall default deny"], rng, index),
            "Value" => EnvironmentValue(row["Setting"] as string),
            "Unit" => Pick(Units, rng, index),
            "Uptime" => $"{rng.Next(0, 90)} d {rng.Next(0, 24)} h",
            "Mode" when page.Slug == "services" => Pick(["enabled", "enabled", "static", "disabled"], rng),
            "Mode" when page.Slug == "firewall" => Pick(["nftables", "firewalld", "ufw"], rng),
            "Mode" => "LUKS2",
            "Checks" => 24,
            "Passed" => 24 - rng.Next(0, 6),
            "Device" when page.Slug is "disks" => Pick(["/dev/nvme0n1", "/dev/sda", "/dev/nvme1n1"], rng),
            "Device" when page.Slug is "raid" => Pick(["/dev/md0", "/dev/md1", "/dev/md127"], rng),
            "Device" => Pick(["/dev/nvme0n1p1", "/dev/nvme0n1p2", "/dev/nvme0n1p3", "/dev/mapper/cryptroot", "/dev/vg0/root", "/dev/vg0/home", "/dev/sda1", "/dev/sda2"], rng, index),
            "Cipher" => Pick(["aes-xts-plain64, 512 bit", "aes-xts-plain64, 256 bit", "serpent-xts-plain64, 512 bit"], rng),
            "Unlock" => Pick(["TPM 2.0", "Passphrase", "TPM 2.0 + PIN"], rng),
            "Zone" => Pick(["public", "internal", "trusted", "work", "drop"], rng),
            "Rules" => rng.Next(4, 60),
            "Port" when page.Slug == "ssh" => rng.Next(6) == 0 ? 2222 : 22,
            "Port" => Pick([22, 443, 631, 5353, 9100, 8443, 123], rng),
            "PasswordAuth" => rng.Next(5) == 0,
            "RootLogin" => Pick(["prohibit-password", "no", "no", "yes"], rng),
            "Keys" => rng.Next(1, 6),
            "Account" => Pick(["root", "admin", "svc-backup", "mpeeters", "jclaes", "svandenberg", "kiosk", "ajanssens"], rng, index),
            "Uid" => row["Account"] switch { "root" => 0, "svc-backup" => 998, "kiosk" => 1500, _ => 1000 + rng.Next(0, 20) },
            "Shell" => row["Account"] switch { "svc-backup" or "kiosk" => "/usr/sbin/nologin", "root" => "/bin/bash", _ => Pick(["/bin/bash", "/bin/zsh", "/usr/bin/fish"], rng) },
            "Subject" => Pick(["CN=ws-pilot-01.corp.example", "CN=print.corp.example", "CN=fleet.corp.example", "CN=vpn.corp.example", "CN=mail.corp.example", "CN=wiki.corp.example", "CN=git.corp.example", "CN=kiosk-03.corp.example"], rng, index),
            "Issuer" => Pick(["Corp Issuing CA 2", "Corp Issuing CA 2", "Let's Encrypt R11", "iGloo Fleet CA"], rng),
            "Fingerprint" => SampleFleetData.Hex64(page.Key + index)[..32],
            "Filesystem" => Pick(["ext4", "ext4", "btrfs", "xfs", "vfat", "swap"], rng, index),
            "MountPoint" => Pick(["/", "/boot", "/boot/efi", "/home", "/var", "/srv", "/var/log", "/opt"], rng, index),
            "Options" => Pick(["rw,relatime,errors=remount-ro", "rw,noatime,compress=zstd:1,subvol=@", "rw,relatime,fmask=0077,dmask=0077", "rw,nosuid,nodev,noexec,relatime", "ro,relatime"], rng),
            "Used" or "PacketLoss" or "Swap" => Math.Round(rng.NextDouble() * (field.Key == "PacketLoss" ? 12 : 100), 1),
            "VolumeGroup" => Pick(["vg0", "vg-system", "fedora", "mint-vg"], rng),
            "Level" when field.Kind == ModuleFieldKind.Text => Pick(["RAID 1", "RAID 5", "RAID 10"], rng),
            "Destination" => Pick(["default", "10.20.0.0/16", "172.16.4.0/24", "192.168.122.0/24", "10.20.4.0/24", "fd00:20::/64"], rng, index),
            "Metric" => Pick([100, 600, 20, 1024, 50], rng),
            "Protocol" when page.Slug == "routes" => Pick(["static", "dhcp", "kernel", "ra"], rng),
            "Protocol" => Pick(["tcp", "tcp", "udp"], rng),
            "Server" => Pick(["10.20.0.53", "10.20.0.54", "1.1.1.1", "9.9.9.9", "fd00:20::53"], rng, index),
            "Domain" => Pick(["corp.example", "lab.corp.example", "branch-ghent.corp.example", "branch-liege.corp.example"], rng),
            "Source" when page.Slug == "dns" => Pick(["DHCP", "Static", "systemd-resolved"], rng),
            "Source" when page.Module == "monitoring" => Pick(["kernel", "systemd", "igloo-agent", "sshd", "NetworkManager", "smartd", "fwupd", "cron"], rng, index),
            "Source" => Pick(["deb.debian.org", "updates.fedoraproject.org", "packages.linuxmint.com", "mirror.corp.example", "Flathub"], rng),
            "Latency" => $"{rng.Next(1, 90)} ms",
            "Action" => Pick(["Allow", "Allow", "Deny", "Reject"], rng),
            "Load" => (rng.NextDouble() * 4).ToString("0.00", CultureInfo.CurrentCulture),
            "Cpu" or "Disk" => Math.Round(rng.NextDouble() * 100, 1),
            "Growth" => $"+{(rng.NextDouble() * 3).ToString("0.0", CultureInfo.CurrentCulture)} GiB/day",
            "Message" => Pick(Messages, rng, index),
            "Script" => Pick(["collect-boot-diagnostics.sh", "rotate-journal.sh", "verify-disk-health.py", "refresh-ca-bundle.sh", "report-kernels.py", "clean-apt-cache.sh"], rng, index),
            "Job" => Pick(["Nightly journal rotation", "Weekly disk health", "CA bundle refresh", "Kernel inventory", "Cache cleanup", "Baseline check"], rng, index),
            "Jobs" or "Runs" or "Uses" => rng.Next(0, 40),
            "ExitCode" => rng.Next(6) == 0 ? Pick([1, 2, 124, 127], rng) : 0,
            "Rows" => rng.Next(8, 4000),
            "Format" => Pick(["CSV", "CSV", "PDF", "JSON"], rng),
            "Occurred" or "Raised" or "Start" or "LastRun" or "Checked" or "LastSignIn" or "Updated" or "Generated" => now.AddMinutes(-rng.Next(5, 60 * 24 * 30)),
            "Installed" => now.AddDays(-rng.Next(1, 400)).AddMinutes(-rng.Next(0, 1440)),
            "ReleasedOn" => now.AddDays(-rng.Next(0, 60)),
            "NextRun" => now.AddHours(rng.Next(1, 24 * 14)),
            "Expires" => now.AddDays(rng.Next(-20, 400)),
            _ => field.Kind switch
            {
                ModuleFieldKind.DateTime => now.AddDays(-rng.Next(0, 30)),
                ModuleFieldKind.Count => rng.Next(0, 50),
                ModuleFieldKind.Size => Bytes(field.Key, rng),
                ModuleFieldKind.Percent => Math.Round(rng.NextDouble() * 100, 1),
                ModuleFieldKind.YesNo => rng.Next(2) == 0,
                _ => string.Empty,
            },
        };
    }

    /// <summary>Statuses that follow from other columns of the same row, so a sample row never contradicts itself.</summary>
    private static string? Derived(ModulePageDefinition page, ModuleField field, Dictionary<string, object?> row, DateTimeOffset now)
    {
        if (field.Key != "Status")
        {
            return null;
        }

        return page.Key switch
        {
            "SecurityCertificates" when row["Expires"] is DateTimeOffset expires => expires <= now ? "Expired" : expires <= now.AddDays(30) ? "ExpiresSoon" : "Valid",
            "SecurityPosture" when row["Failed"] is int failed => failed > 3 ? "Critical" : failed > 0 ? "AtRisk" : "Healthy",
            "MonitoringHealth" when row["Disk"] is double disk && row["Memory"] is double memory => disk > 90 || memory > 90 ? "Critical" : disk > 75 || memory > 75 ? "Degraded" : "Healthy",
            "MonitoringStorage" when row["Used"] is double used => used > 90 ? "Critical" : used > 75 ? "Warning" : "Healthy",
            "StorageFilesystems" when row["Used"] is double used => used > 95 ? "Full" : null,
            "InventoryAgentVersions" when row["AgentVersion"] is string version => version switch { "1.4.0" or "1.3.2" => "Current", "1.2.0" or "1.1.5" => "Outdated", _ => "Unsupported" },
            _ => null,
        };
    }

    private static string Name(ModulePageDefinition page, Random rng, int index) => page.Key switch
    {
        "Cohorts" => Pick(["Pilot workstations", "Finance desktops", "Field laptops", "Developer machines", "Kiosk terminals", "Branch office Ghent", "Branch office Liege", "Lab devices", "Executive laptops", "Shared terminals"], rng, index),
        "SoftwareApplications" => Pick(Applications, rng, index),
        "SoftwareRepositories" => Pick(["Debian trixie main", "Debian security", "Fedora 42 updates", "Fedora 42 base", "Linux Mint xia", "Ubuntu noble (Mint base)", "Internal mirror", "Flathub"], rng, index),
        "SoftwareDeployments" => Pick(["Firefox ESR 128.10 rollout", "LibreOffice 24.8 pilot", "KeePassXC 2.7.10", "Visual Studio Code 1.105", "Remmina 1.4.40", "VLC 3.0.21", "Thunderbird 128.10", "Signal Desktop 7.60", "Inkscape 1.4", "GIMP 3.0.4", "Zotero 7.0", "OBS Studio 31.1"], rng, index),
        "UpdatesRings" => Pick(["Pilot", "Early", "Broad", "Servers", "Kiosks"], rng, index),
        "ConfigurationPolicies" => Pick(["CIS Level 1 baseline", "Disable password SSH", "Audit logging", "Automatic security updates", "Screen lock after 10 minutes", "USB storage read-only", "Enforce Secure Boot", "sudo without NOPASSWD", "NTP corporate servers", "Firewall default deny", "Disk encryption required", "Journal retention 90 days", "Bluetooth disabled on kiosks", "Core dumps disabled"], rng, index),
        "ConfigurationProfiles" => Pick(["Standard desktop", "Developer workstation", "Kiosk terminal", "Branch server", "Finance desktop", "Field laptop", "Lab machine", "Executive laptop", "Shared terminal", "Build agent"], rng, index),
        "ConfigurationScripts" or "AutomationScripts" => Pick(["Collect boot diagnostics", "Rotate journal", "Verify disk health", "Refresh CA bundle", "Report installed kernels", "Clean apt cache", "Check NTP drift", "Export firewall rules", "List listening ports", "Snapshot package list", "Check Secure Boot state", "Report TPM status"], rng, index),
        "ConfigurationScheduledJobs" => Pick(["apt-daily.timer", "fstrim.timer", "logrotate.timer", "man-db.timer", "e2scrub_all.timer", "igloo-agent-heartbeat.timer", "dnf-makecache.timer", "systemd-tmpfiles-clean.timer", "apt-daily-upgrade.timer"], rng, index),
        "StorageLvm" => Pick(["root", "home", "var", "swap", "srv", "log"], rng, index),
        "MonitoringAlerts" => Pick(["Disk usage above 90% on /var", "Certificate expires in 9 days", "Secure Boot disabled", "Agent outdated (1.1.5)", "Security updates pending for 14 days", "RAID array degraded", "SSH password authentication enabled", "Time drift above 5 s", "No contact for 30 days", "Filesystem read-only: /home", "Memory pressure sustained", "Firewall inactive", "Failed sign-in burst", "Kernel update requires reboot"], rng, index),
        "AutomationJobs" => Pick(["Nightly journal rotation", "Weekly disk health", "CA bundle refresh", "Kernel inventory", "Cache cleanup", "Baseline check", "Firewall export", "Port audit", "Package snapshot", "NTP drift check", "Secure Boot report", "TPM status report"], rng, index),
        "AutomationSchedules" => Pick(["Nightly 02:00", "Weekly Sunday 03:00", "Hourly", "Monthly first Tuesday", "Business hours", "Friday evening", "Quarterly", "Every 15 minutes"], rng, index),
        "AutomationTemplates" => Pick(["Collect diagnostics bundle", "Baseline check", "Apply SSH hardening", "Rotate logs", "Inventory snapshot", "Certificate refresh", "Disk health report", "Service restart", "Package audit", "Time sync check"], rng, index),
        "AutomationMaintenanceWindows" => Pick(["Tuesday night", "Saturday morning", "Month-end freeze", "Branch Ghent", "Servers", "Kiosks"], rng, index),
        "ReportsInventory" => Pick(["Hardware inventory", "Operating systems", "Agent versions", "Network interfaces", "Installed software", "Disk capacity", "Memory by model", "Firmware versions"], rng, index),
        "ReportsCompliance" => Pick(["Baseline compliance", "SSH baseline", "Disk encryption coverage", "Secure Boot coverage", "sudo policy review", "Firewall coverage", "Local account review", "Policy assignment"], rng, index),
        "ReportsUpdates" => Pick(["Pending security updates", "Update currency", "Failed installs", "Reboot pending", "Ring progress", "Deferred updates", "Kernel versions", "Update history 30 days"], rng, index),
        "ReportsSecurity" => Pick(["Posture summary", "Local administrators", "Certificate expiry", "Open ports", "Failed sign-ins", "Encryption status", "Secure Boot status", "sudo exceptions"], rng, index),
        "ReportsMigrations" => Pick(["Dry-run eligibility", "Approvals last 30 days", "Prepared plans", "Blocked reasons", "Profiles in use", "Work item throughput", "Evidence by distribution", "Assessment outcomes"], rng, index),
        "ReportsCustom" => Pick(["Finance desktops weekly", "Field laptop contact", "Kiosk health", "Developer tooling", "Branch Ghent summary", "Executive summary"], rng, index),
        _ => Pick(["Item " + (index + 1)], rng),
    };

    private static readonly string[] Applications =
    [
        "Firefox ESR", "LibreOffice", "Thunderbird", "VLC", "GIMP", "Visual Studio Code", "Remmina", "KeePassXC", "Inkscape", "Signal Desktop",
        "Zotero", "FreeCAD", "Slack", "Chromium", "OBS Studio", "Audacity", "Okular", "Krita", "Nextcloud Desktop", "Element", "Wireshark", "Blender", "Evolution", "Kdenlive",
    ];

    private static string Publisher(string? name) => name switch
    {
        "Firefox ESR" or "Thunderbird" => "Mozilla",
        "LibreOffice" => "The Document Foundation",
        "VLC" => "VideoLAN",
        "GIMP" => "GIMP team",
        "Visual Studio Code" => "Microsoft",
        "Remmina" => "Remmina project",
        "KeePassXC" => "KeePassXC team",
        "Inkscape" => "Inkscape project",
        "Signal Desktop" => "Signal Foundation",
        "Zotero" => "Corporation for Digital Scholarship",
        "FreeCAD" => "FreeCAD project",
        "Slack" => "Salesforce",
        "Chromium" => "The Chromium Projects",
        "OBS Studio" => "OBS Project",
        "Audacity" => "Muse Group",
        "Okular" or "Krita" or "Kdenlive" => "KDE",
        "Nextcloud Desktop" => "Nextcloud GmbH",
        "Element" => "Element",
        "Wireshark" => "Wireshark Foundation",
        "Blender" => "Blender Foundation",
        "Evolution" => "GNOME",
        _ => "Unknown publisher",
    };

    private static readonly string[] Packages =
    [
        "openssl", "libssl3", "curl", "systemd", "libc6", "bash", "sudo", "openssh-server", "nftables", "network-manager", "python3", "git", "vim", "cups", "pipewire",
        "mesa-vulkan-drivers", "xdg-desktop-portal", "linux-image-amd64", "grub-efi-amd64", "shim-signed", "tpm2-tools", "cryptsetup", "lvm2", "mdadm", "rsync", "tar", "gzip",
        "zstd", "dbus", "polkitd", "plasma-desktop", "cinnamon", "firefox-esr", "libreoffice-core", "fwupd", "chrony", "rsyslog", "ca-certificates", "gnupg", "apt",
    ];

    private static readonly string[] Units =
    [
        "ssh.service", "NetworkManager.service", "systemd-timesyncd.service", "cups.service", "nftables.service", "cron.service", "rsyslog.service", "unattended-upgrades.service",
        "igloo-agent.service", "sddm.service", "lightdm.service", "bluetooth.service", "avahi-daemon.service", "fwupd.service", "chronyd.service", "firewalld.service", "smartd.service", "polkit.service",
    ];

    private static readonly string[] SshSettings =
        ["PasswordAuthentication", "PermitRootLogin", "X11Forwarding", "MaxAuthTries", "AllowTcpForwarding", "ClientAliveInterval", "Ciphers", "KexAlgorithms", "LogLevel", "UsePAM"];

    private static string SshValue(string? setting, Random rng, bool baseline) => setting switch
    {
        "PasswordAuthentication" or "X11Forwarding" => baseline || rng.Next(4) != 0 ? "no" : "yes",
        "PermitRootLogin" => baseline || rng.Next(4) != 0 ? "prohibit-password" : "yes",
        "MaxAuthTries" => baseline || rng.Next(3) != 0 ? "3" : "6",
        "AllowTcpForwarding" => baseline ? "local" : Pick(["local", "yes", "no"], rng),
        "ClientAliveInterval" => baseline || rng.Next(3) != 0 ? "300" : "0",
        "Ciphers" => "chacha20-poly1305,aes256-gcm",
        "KexAlgorithms" => baseline || rng.Next(3) != 0 ? "sntrup761x25519-sha512" : "curve25519-sha256",
        "LogLevel" => baseline || rng.Next(3) != 0 ? "VERBOSE" : "INFO",
        _ => "yes",
    };

    private static string EnvironmentValue(string? setting) => setting switch
    {
        "http_proxy" or "https_proxy" => "http://proxy.corp.example:3128",
        "no_proxy" => "localhost,127.0.0.1,.corp.example",
        "LANG" => "nl_BE.UTF-8",
        "TZ" => "Europe/Brussels",
        "EDITOR" => "vim",
        "JAVA_HOME" => "/usr/lib/jvm/java-21-openjdk-amd64",
        _ => "wayland",
    };

    private static readonly string[] Messages =
    [
        "Link enp0s31f6 is up", "Failed password for invalid user from 10.20.4.17", "SMART: reallocated sector count increased", "Started igloo-agent heartbeat",
        "Time jumped by 3.2 s", "Firmware update available for Thunderbolt controller", "Out of memory: killed process 4821 (firefox-esr)", "Secure Boot enabled; vendor keys loaded",
        "Reached target Graphical Interface", "DHCP lease renewed on wlp0s20f3", "Unit cups.service entered failed state", "Journal rotated, 412 MiB freed",
        "TPM 2.0 PCR 7 measurement changed", "Certificate for print.corp.example renewed", "Mount /home became read-only after I/O error", "Package firefox-esr upgraded to 128.10.0esr-1",
    ];

    private static long Bytes(string key, Random rng) => key switch
    {
        "Capacity" => Pick([256L, 512L, 1024L, 2048L, 4096L], rng) * 1024L * 1024L * 1024L,
        "Received" or "Sent" => (long)(rng.NextDouble() * 80) * 1024L * 1024L * 1024L + rng.Next(1, 1024) * 1024L * 1024L,
        _ => rng.Next(40, 400_000) * 1024L,
    };

    private static T Pick<T>(IReadOnlyList<T> pool, Random rng, int? index = null) =>
        index is { } i && i < pool.Count ? pool[i] : pool[rng.Next(pool.Count)];

    private static string Weighted(IReadOnlyList<string> statuses, Random rng)
    {
        // The first token is the common case; later tokens get rarer.
        var roll = rng.Next(100);
        return roll < 65 || statuses.Count == 1 ? statuses[0] : statuses[1 + rng.Next(statuses.Count - 1)];
    }

    private static int Seed(string key) => BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes("igloo-fleet-module:" + key)), 0) & 0x7FFFFFFF;
}
