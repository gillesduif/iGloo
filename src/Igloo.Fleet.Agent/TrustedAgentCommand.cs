using System.Net.Http.Json;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using Igloo.Core.Plugins;
using Igloo.Fleet.Contracts;
using Igloo.Preflight;
using Microsoft.Extensions.Logging.Abstractions;

namespace Igloo.Fleet.Agent;

[System.ComponentModel.Localizable(false)]
internal static class TrustedAgentCommand
{
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA1303:Do not pass literals as localized parameters",
        Justification = "Engineering CLI diagnostics are intentionally English in Phase 1.")]
    internal static async Task RunAsync(bool enroll)
    {
        var address = new Uri(Environment.GetEnvironmentVariable("IGLOO_FLEET_SERVER_URI") ?? "https://localhost:5188/");
        if (address.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(address.UserInfo) ||
            address.AbsolutePath != "/" || !string.IsNullOrEmpty(address.Query))
            throw new InvalidOperationException("Trusted Fleet requires an HTTPS server origin.");
        var directory = Environment.GetEnvironmentVariable("IGLOO_FLEET_AGENT_DATA") ??
            Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Igloo", "Fleet", "trusted");
        Directory.CreateDirectory(directory);
        using var user = WindowsIdentity.GetCurrent();
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(acl);
        using var processLock = new FileStream(Path.Join(directory, "agent.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var rootPath = Environment.GetEnvironmentVariable("IGLOO_FLEET_CA_CERT")
            ?? throw new InvalidOperationException("Provision IGLOO_FLEET_CA_CERT through a trusted channel before enrollment.");
        using var root = X509Certificate2.CreateFromPem(await File.ReadAllTextAsync(rootPath).ConfigureAwait(false));
        var identityStore = new EnrolledIdentityStore(directory);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        if (enroll)
        {
            if (identityStore.Exists) throw new InvalidOperationException("Already enrolled or partial identity exists. Explicit recovery is required.");
            using var key = RSA.Create(2048);
            var csr = new CertificateRequest("CN=Fleet enrollment", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var token = Environment.GetEnvironmentVariable("IGLOO_FLEET_ENROLLMENT_TOKEN")
                ?? throw new InvalidOperationException("Set IGLOO_FLEET_ENROLLMENT_TOKEN for this one enrollment.");
            using var enrollmentHandler = FleetTls.CreateHandler(root);
            using var enrollmentClient = new HttpClient(enrollmentHandler) { BaseAddress = address };
            using var response = await enrollmentClient.PostAsJsonAsync("v2/enroll",
                new EnrollmentRequest(PlanningProtocol.Version, token, csr.CreateSigningRequestPem()), timeout.Token).ConfigureAwait(false);
            await TrustedAgentWorkflow.CheckAsync(response, timeout.Token).ConfigureAwait(false);
            var accepted = await response.Content.ReadFromJsonAsync<EnrollmentResponse>(timeout.Token).ConfigureAwait(false)
                ?? throw new InvalidDataException("Missing enrollment response.");
            identityStore.Save(accepted, key);
            Console.WriteLine("Enrolled DeviceId=" + accepted.Identity.DeviceId);
        }
        var (metadata, certificate) = identityStore.Load();
        using (certificate)
        using (var handler = FleetTls.CreateHandler(root, certificate))
        using (var client = new HttpClient(handler) { BaseAddress = address, Timeout = TimeSpan.FromSeconds(30) })
        {
            var registry = new DistroRegistry(NullLogger<DistroRegistry>.Instance);
            var distros = Environment.GetEnvironmentVariable("IGLOO_FLEET_DISTROS") ?? Path.Join(AppContext.BaseDirectory, "distros");
            await registry.LoadAsync(Path.GetFullPath(distros), timeout.Token).ConfigureAwait(false);
            var checker = new WindowsPreflightChecker(NullLogger<WindowsPreflightChecker>.Instance);
            var workflow = new TrustedAgentWorkflow(client, new ReadOnlyPlanner(checker, registry), new ResultSpool(Path.Join(directory, "outbox")));
            await workflow.RunOnceAsync(metadata.Identity, timeout.Token).ConfigureAwait(false);
            Console.WriteLine("Read-only Agent cycle complete; execution capability is absent.");
        }
    }
}
