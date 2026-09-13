using Igloo.Fleet.Server;

if (args.Length == 1 && args[0] is "--trusted" or "--initialize-trust")
{
    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("The Phase 1 local CA key store requires Windows DPAPI.");
    await TrustedServerCommand.RunAsync(args[0] == "--initialize-trust").ConfigureAwait(false);
    return;
}

if (args.Length != 1 || args[0] != "--development-local")
    throw new InvalidOperationException("Phase 0 requires --development-local and accepts no endpoint overrides.");
var token = Environment.GetEnvironmentVariable("IGLOO_FLEET_DEV_TOKEN")
    ?? throw new InvalidOperationException("Set IGLOO_FLEET_DEV_TOKEN to a development secret of at least 32 characters.");
var app = FleetServer.Build(token);
await using (app.ConfigureAwait(false))
    await app.RunAsync().ConfigureAwait(false);
