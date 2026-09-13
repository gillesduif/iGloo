using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Igloo.Fleet.Persistence;

namespace Igloo.Fleet.Server;

[System.ComponentModel.Localizable(false)]
internal static class TrustedServerCommand
{
    [SupportedOSPlatform("windows")]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters",
        Justification = "Engineering CLI diagnostics are intentionally English in Phase 1.")]
    internal static async Task RunAsync(bool initialize)
    {
        var directory = Environment.GetEnvironmentVariable("IGLOO_FLEET_SERVER_DATA") ??
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Igloo", "FleetServer");
        Directory.CreateDirectory(directory);
        using var user = WindowsIdentity.GetCurrent();
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(acl);
        var caPath = Path.Join(directory, "ca-key.dpapi");
        var serverPath = Path.Join(directory, "server-key.dpapi");
        if (initialize)
        {
            if (File.Exists(caPath) || File.Exists(serverPath))
                throw new InvalidOperationException("Trust already initialized; existing keys are never replaced automatically.");
            using var root = FleetCertificateAuthority.CreateRoot();
            FleetCertificateAuthority.SaveProtected(caPath, root);
            using var authority = new FleetCertificateAuthority(root);
            using var server = authority.CreateServerCertificate(Environment.GetEnvironmentVariable("IGLOO_FLEET_TLS_NAME") ?? "localhost");
            FleetCertificateAuthority.SaveProtected(serverPath, server);
            await File.WriteAllTextAsync(Path.Join(directory, "fleet-root.pem"), root.ExportCertificatePem()).ConfigureAwait(false);
            _ = new SqlitePlanningStore(Path.Join(directory, "fleet.db"));
            Console.WriteLine("Fleet trust and database initialized. Provision fleet-root.pem through a trusted channel.");
            return;
        }
        using var loaded = FleetCertificateAuthority.LoadProtected(caPath);
        using var issuer = new FleetCertificateAuthority(loaded);
        using var certificate = FleetCertificateAuthority.LoadProtected(serverPath);
        var secret = Environment.GetEnvironmentVariable("IGLOO_FLEET_OPERATOR_SECRET")
            ?? throw new InvalidOperationException("Set a separate IGLOO_FLEET_OPERATOR_SECRET of at least 32 characters.");
        var store = new SqlitePlanningStore(Path.Join(directory, "fleet.db"));
        var bind = System.Net.IPAddress.Parse(Environment.GetEnvironmentVariable("IGLOO_FLEET_LISTEN_ADDRESS") ?? "127.0.0.1");
        var app = TrustedFleetServer.Build(store, issuer, certificate, secret, listenAddress: bind);
        await using (app.ConfigureAwait(false))
            await app.RunAsync().ConfigureAwait(false);
    }
}
