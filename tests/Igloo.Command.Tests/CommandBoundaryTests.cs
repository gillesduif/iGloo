using System.Text.Json;
using System.Xml.Linq;
using Igloo.Cli;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;
using Igloo.Core.Runtime;
using Igloo.Core.Runtime.Execution;
using Igloo.Core.Host;
using Igloo.Core.Models;
using Igloo.Core.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Igloo.Command.Tests;

public sealed class CommandBoundaryTests
{
    private sealed class Checker : IPreflightChecker
    {
        public Task<PreflightReport> RunAsync(CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(new PreflightReport
            {
                IsUefi = true, SecureBootEnabled = false, TpmPresent = true,
                BitLocker = BitLockerState.NotEncrypted, TotalRamBytes = 8_000_000_000,
                GpuVendor = "PRIVATE_GPU", Disks = [new("PRIVATE_DEVICE", "PRIVATE_DISK", 100, 50,
                    "GPT", [new(1, "NTFS", 100, "PRIVATE_LABEL", false, true)])],
                Findings = [new(FindingSeverity.Warning, "LOW_RAM", "PRIVATE_MESSAGE", "PRIVATE_PATH")],
            });
        }
    }

    private static CommandDispatcher Dispatcher() => new(new Checker(),
        new DistroLoader(NullLogger<DistroLoader>.Instance),
        new DistroRegistry(NullLogger<DistroRegistry>.Instance));

    private static CommandRequest Request(string capability, int protocol = 1, int version = 1) =>
        new(protocol, capability, version, Guid.NewGuid(), Guid.NewGuid(),
            JsonSerializer.SerializeToElement(new { }));

    [Fact]
    public async Task InspectionReturnsTypedAllowlistedFactsWithoutPrivateObservationText()
    {
        var result = await Dispatcher().ExecuteAsync(Request("system.inspect"));
        Assert.True(result.Succeeded);
        Assert.Equal(64, result.ObservedStateFingerprint!.Length);
        var data = result.Data!.Value.Deserialize<SystemInspection>(CommandProtocol.JsonOptions)!;
        Assert.True(data.IsUefi);
        Assert.Equal(1, data.DiskCount);
        Assert.Equal("LOW_RAM", Assert.Single(data.Findings).Code);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result, CommandProtocol.JsonOptions), StringComparison.Ordinal);
    }

    [Fact]
    public async Task DetailedInspectionExposesCapacityFactsWithoutPrivateDeviceOrPartitionText()
    {
        var result = await Dispatcher().ExecuteAsync(Request("system.inspect.detail"));
        Assert.True(result.Succeeded);
        Assert.Equal(64, result.ObservedStateFingerprint!.Length);
        var detail = result.Data!.Value.Deserialize<DetailedSystemInspection>(CommandProtocol.JsonOptions)!;
        Assert.Equal(1, detail.Summary.DiskCount);
        var disk = Assert.Single(detail.Disks);
        Assert.Equal(0, disk.Ordinal);
        Assert.True(disk.HasBootPartition);
        Assert.Equal(50, disk.FreeBytes);
        Assert.Equal("NTFS", Assert.Single(disk.Partitions).FileSystemCategory);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result, CommandProtocol.JsonOptions),
            StringComparison.Ordinal);
        Assert.Contains(Dispatcher().Capabilities, c => c.Id == "system.inspect.detail" &&
            c.Kind == CommandKind.Query);
    }

    [Fact]
    public async Task UnreviewedFindingCodeCannotCarryPrivateTextAcrossCommandBoundary()
    {
        var checker = new PrivateFindingChecker();
        var dispatcher = new CommandDispatcher(checker,
            new DistroLoader(NullLogger<DistroLoader>.Instance),
            new DistroRegistry(NullLogger<DistroRegistry>.Instance));
        var result = await dispatcher.ExecuteAsync(Request("system.inspect.detail"));
        var detail = result.Data!.Value.Deserialize<DetailedSystemInspection>(CommandProtocol.JsonOptions)!;
        Assert.Equal("ProviderFinding", Assert.Single(detail.Summary.Findings).Code);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(result, CommandProtocol.JsonOptions),
            StringComparison.Ordinal);
    }

    private sealed class PrivateFindingChecker : IPreflightChecker
    {
        public async Task<PreflightReport> RunAsync(CancellationToken ct = default) =>
            (await new Checker().RunAsync(ct)) with
            {
                Findings = [new(FindingSeverity.Warning, "PRIVATE_PASSWORD", "PRIVATE_MESSAGE")],
            };
    }

    [Theory]
    [InlineData(100, 0, 1, 20, MigrationCapacityState.Unknown, null)]
    [InlineData(100, 10, 1, 20, MigrationCapacityState.Insufficient, 10L)]
    [InlineData(100, 0, 30, 20, MigrationCapacityState.KnownAvailable, 30L)]
    [InlineData(10, 0, 1, 20, MigrationCapacityState.PhysicalDiskTooSmall, 10L)]
    public void AdvisoryCapacityDistinguishesUnknownMeasuredAndPhysicalLimit(long totalGb,
        long shrinkGb, long freeGb, long requiredGb, MigrationCapacityState expectedState,
        long? expectedAvailableGb)
    {
        DiskInfo[] disks = [new("private-device", "private-model", totalGb << 30,
            freeGb << 30, "GPT", [new(1, "NTFS", totalGb << 30, null,
                false, true, shrinkGb << 30)])];
        var evaluated = MigrationCapacityEvaluator.Evaluate(disks, requiredGb << 30);
        Assert.Equal(expectedState, evaluated.State);
        Assert.Equal(expectedAvailableGb.HasValue ? expectedAvailableGb.Value << 30 : null,
            evaluated.ObservedAvailableBytes);
    }

    [Fact]
    public async Task CatalogueAndVersionRejectUnknownOrMutatingCapabilities()
    {
        var dispatcher = Dispatcher();
        var catalogue = await dispatcher.ExecuteAsync(Request("system.capabilities"));
        Assert.True(catalogue.Succeeded);
        Assert.DoesNotContain(dispatcher.Capabilities, c => c.Kind == CommandKind.Mutation);
        Assert.Contains(dispatcher.Capabilities, c => c.Id == "migration.plan" && c.Kind == CommandKind.Plan);
        foreach (var capability in new[] { "migration.prepare", "migration.execute", "migration.verify" })
        {
            Assert.DoesNotContain(dispatcher.Capabilities, c => c.Id == capability);
            Assert.Equal(CommandErrorCode.UnsupportedCapability,
                (await dispatcher.ExecuteAsync(Request(capability))).Error!.Code);
        }
        Assert.Equal(CommandErrorCode.UnsupportedVersion,
            (await dispatcher.ExecuteAsync(Request("system.inspect", protocol: 2))).Error!.Code);
        Assert.Equal(CommandErrorCode.UnsupportedVersion,
            (await dispatcher.ExecuteAsync(Request("system.inspect", version: 2))).Error!.Code);
        Assert.Equal(CommandErrorCode.InvalidRequest,
            (await dispatcher.ExecuteAsync(Request("system.inspect") with { Input = default })).Error!.Code);
    }

    [Fact]
    public async Task CancellationIsStructuredAndDoesNotCallChecker()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var result = await Dispatcher().ExecuteAsync(Request("system.inspect"), cancellationToken: cts.Token);
        Assert.Equal(CommandErrorCode.Cancelled, result.Error!.Code);
    }

    [Fact]
    public async Task CliJsonIsOneParseableResultAndErrorsHaveStableExitCodes()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApplication.RunAsync(["inspect", "--json"], Dispatcher(), new StringReader(""), output, error);
        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error.ToString());
        using var inspected = JsonDocument.Parse(output.ToString());
        Assert.Equal("system.inspect", inspected.RootElement.GetProperty("capabilityId").GetString());
        output.GetStringBuilder().Clear();
        exit = await CliApplication.RunAsync(["distros", "list", "--json"], Dispatcher(),
            new StringReader(""), output, error);
        Assert.Equal(0, exit);
        Assert.Equal("distro.list", JsonDocument.Parse(output.ToString()).RootElement
            .GetProperty("capabilityId").GetString());
        output.GetStringBuilder().Clear();
        exit = await CliApplication.RunAsync(["capabilities", "list", "--json"], Dispatcher(),
            new StringReader(""), output, error);
        Assert.Equal(0, exit);
        using var document = JsonDocument.Parse(output.ToString());
        Assert.Equal("system.capabilities", document.RootElement.GetProperty("capabilityId").GetString());
        Assert.True(document.RootElement.GetProperty("succeeded").GetBoolean());
        output.GetStringBuilder().Clear();
        exit = await CliApplication.RunAsync(["unknown", "--json"], Dispatcher(), new StringReader(""), output, error);
        Assert.Equal(2, exit);
        Assert.Equal("InvalidRequest", JsonDocument.Parse(output.ToString()).RootElement
            .GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task HumanCatalogueRendersCommandsWithoutJsonOrQueryProgress()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApplication.RunAsync(["capabilities", "list"], Dispatcher(),
            new StringReader(""), output, error);
        Assert.Equal(0, exit);
        Assert.Contains("system.inspect v1", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("{", output.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task DetailedInspectionHumanAndJsonModesUseTheSameCapability()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var dispatcher = Dispatcher();
        var exit = await CliApplication.RunAsync(["inspect", "detail"], dispatcher,
            new StringReader(""), output, error);
        Assert.Equal(0, exit);
        Assert.Contains("Disk 0:", output.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE", output.ToString(), StringComparison.Ordinal);
        output.GetStringBuilder().Clear();
        exit = await CliApplication.RunAsync(["inspect", "detail", "--json"], dispatcher,
            new StringReader(""), output, error);
        Assert.Equal(0, exit);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("system.inspect.detail", json.RootElement.GetProperty("capabilityId").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("data").GetProperty("disks").GetArrayLength());
    }

    [Fact]
    public void ProjectReferencesKeepProtocolAndFleetServerIsolated()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "Igloo.sln"))) root = root.Parent;
        Assert.NotNull(root);
        static string[] Refs(string path) => XDocument.Load(path).Descendants("ProjectReference")
            .Select(e => Path.GetFileNameWithoutExtension(e.Attribute("Include")!.Value)).ToArray();
        string Project(string name) => Path.Join(root!.FullName, "src", name, name + ".csproj");
        Assert.Empty(Refs(Project("Igloo.Command.Contracts")));
        Assert.Equal(["Igloo.Command.Contracts", "Igloo.Core", "Igloo.Preflight"],
            Refs(Project("Igloo.Core.Runtime")).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Refs(Project("Igloo.Fleet.Server")),
            r => r is "Igloo.Core.Runtime" or "Igloo.Preflight" or "Igloo.Iso" or "Igloo.Migration");
        Assert.All(Directory.EnumerateFiles(Path.Join(root!.FullName, "distros"), "*.csproj", SearchOption.AllDirectories),
            path => Assert.All(Refs(path), r => Assert.Equal("Igloo.Core", r)));
        Assert.DoesNotContain("Igloo.Community.App", Refs(Project("Igloo.Core.Runtime.Execution")));
        Assert.DoesNotContain("Igloo.Fleet.Server", Refs(Project("Igloo.Core.Runtime.Execution")));
        Assert.Equal(["Igloo.Command.Contracts", "Igloo.Core.Host"],
            Refs(Path.Join(root.FullName, "src", "Igloo.Cli", "Igloo.Cli", "Igloo.Cli.csproj"))
                .Order(StringComparer.Ordinal));
        Assert.Equal(["Igloo.Command.Contracts", "Igloo.Core.Host"],
            Refs(Project("Igloo.Community.App")).Order(StringComparer.Ordinal));
        Assert.Equal(["Igloo.Cli.Client", "Igloo.Command.Contracts"],
            Refs(Path.Join(root.FullName, "Igloo.One.App", "Igloo.One.App.csproj"))
                .Order(StringComparer.Ordinal));
        Assert.Equal(new[] { "Igloo.Command.Contracts" }, Refs(Project("Igloo.Cli.Client")));
        foreach (var source in Directory.EnumerateFiles(Path.Join(root.FullName, "src", "Igloo.Cli.Client"),
                     "*.cs", SearchOption.AllDirectories).Where(path =>
                     !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                         StringComparison.OrdinalIgnoreCase)))
        {
            var clientText = File.ReadAllText(source);
            Assert.DoesNotContain("using Igloo.Core", clientText, StringComparison.Ordinal);
            Assert.DoesNotContain("using Igloo.Preflight", clientText, StringComparison.Ordinal);
            Assert.DoesNotContain("using Igloo.Migration", clientText, StringComparison.Ordinal);
            Assert.DoesNotContain("using Igloo.Iso", clientText, StringComparison.Ordinal);
            Assert.DoesNotContain("IglooHost.", clientText, StringComparison.Ordinal);
        }
        Assert.Equal(["Igloo.Cli.Client", "Igloo.Command.Contracts", "Igloo.Core.Host", "Igloo.Fleet.Contracts", "Igloo.Fleet.Domain"],
            Refs(Project("Igloo.Fleet.Agent")).Order(StringComparer.Ordinal));
        Assert.DoesNotContain(Refs(Project("Igloo.Fleet.Server")), r => r is
            "Igloo.Core.Host" or "Igloo.Core.Runtime.Execution" or "Igloo.Core.Runtime" or
            "Igloo.Preflight" or "Igloo.Iso" or "Igloo.Migration" or "Igloo.UsbWriter");
        var host = XDocument.Load(Project("Igloo.Core.Host"));
        Assert.DoesNotContain(host.Descendants("UseWPF"), e => e.Value == "true");
        Assert.DoesNotContain(Refs(Project("Igloo.Core.Host")), r => r.StartsWith("Igloo.Fleet", StringComparison.Ordinal));
        foreach (var product in new[] { "Igloo.Community.App", "Igloo.Fleet.Agent" })
        {
            foreach (var source in Directory.EnumerateFiles(Path.Join(root.FullName, "src", product),
                         "*.cs", SearchOption.AllDirectories).Where(p =>
                         !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                             StringComparison.OrdinalIgnoreCase)))
            {
                var text = File.ReadAllText(source);
                Assert.DoesNotContain("using Igloo.Preflight;", text, StringComparison.Ordinal);
                Assert.DoesNotContain("using Igloo.Iso;", text, StringComparison.Ordinal);
                Assert.DoesNotContain("using Igloo.Migration;", text, StringComparison.Ordinal);
                Assert.DoesNotContain("using Igloo.UsbWriter;", text, StringComparison.Ordinal);
                Assert.DoesNotContain("using Igloo.Core.Runtime.Execution;", text, StringComparison.Ordinal);
            }
        }
        foreach (var source in Directory.EnumerateFiles(Path.Join(root.FullName, "Igloo.One.App"),
                     "*.cs", SearchOption.AllDirectories).Where(p =>
                     !p.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar,
                         StringComparison.OrdinalIgnoreCase)))
        {
            var text = File.ReadAllText(source);
            Assert.DoesNotContain("using Igloo.Preflight;", text, StringComparison.Ordinal);
            Assert.DoesNotContain("using Igloo.Iso;", text, StringComparison.Ordinal);
            Assert.DoesNotContain("using Igloo.Migration;", text, StringComparison.Ordinal);
            Assert.DoesNotContain("using Igloo.UsbWriter;", text, StringComparison.Ordinal);
            Assert.DoesNotContain("IglooHost.CreateAsync(", text, StringComparison.Ordinal);
            Assert.DoesNotContain("IglooHost.AddLocalBackend(", text, StringComparison.Ordinal);
            foreach (var privateWorkflow in new[] { "IPreflightWorkflow", "IIsoWorkflow",
                         "IMigrationStagingWorkflow", "IUsbPreparation", "IInstallationPreparation",
                         "IWindowsMaintenance" })
                Assert.DoesNotContain(privateWorkflow, text, StringComparison.Ordinal);
        }
        foreach (var layer in new[] { "Igloo.Core", "Igloo.Core.Runtime", "Igloo.Core.Runtime.Execution", "Igloo.Core.Host" })
        {
            var project = XDocument.Load(Project(layer));
            Assert.DoesNotContain(project.Descendants("UseWPF"), e => e.Value == "true");
            Assert.DoesNotContain(Refs(Project(layer)), r => r is "Igloo.One.App" or
                "Igloo.Community.App" or "Igloo.Fleet.Server" || r.Contains("AI", StringComparison.OrdinalIgnoreCase));
        }
        var cliSource = File.ReadAllText(Path.Join(root.FullName, "src", "Igloo.Cli", "Igloo.Cli", "Program.cs"));
        Assert.DoesNotContain("IsoAcquisitionService", cliSource, StringComparison.Ordinal);
        Assert.DoesNotContain("DirectInstallService", cliSource, StringComparison.Ordinal);
        var stagingView = File.ReadAllText(Path.Join(root.FullName, "src", "Igloo.Community.App",
            "ViewModels", "FileStagingViewModel.cs"));
        Assert.DoesNotContain("ManifestGeneratorService", stagingView, StringComparison.Ordinal);
        Assert.DoesNotContain("PluginArtifactWriter", stagingView, StringComparison.Ordinal);
        Assert.DoesNotContain("BrowserCredentialMigration", stagingView, StringComparison.Ordinal);
        var isoView = File.ReadAllText(Path.Join(root.FullName, "src", "Igloo.Community.App",
            "ViewModels", "IsoAcquisitionViewModel.cs"));
        Assert.DoesNotContain("IsoSpecification", isoView, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IndependentHostConsumerDiscoversProvidersCompatibilityAndAdvisoryPlan()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddLocalBackend(includeIsoAcquisition: false);
        services.Replace(ServiceDescriptor.Singleton<IPreflightChecker>(new Checker()));
        using var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IBackendCatalog>().LoadAsync(Path.Join(RepositoryRoot(), "distros"));
        var commands = provider.GetRequiredService<ICommandDispatcher>();
        Assert.DoesNotContain(commands.Capabilities, c => c.Kind == CommandKind.Mutation);
        foreach (var blocked in new[] { "migration.layout.finalize", "migration.prepare",
                     "migration.execute", "migration.verify" })
        {
            Assert.DoesNotContain(commands.Capabilities, c => c.Id == blocked);
            Assert.Equal(CommandErrorCode.UnsupportedCapability,
                (await commands.ExecuteAsync(Request(blocked))).Error!.Code);
        }
        var list = await commands.ExecuteAsync(Request("distro.list"));
        Assert.Contains(list.Data!.Value.Deserialize<DistroList>(CommandProtocol.JsonOptions)!.Distros,
            d => d.Id == "fedora-kde");
        var compatible = await commands.ExecuteAsync(Request("distro.compatibility") with
        { Input = JsonSerializer.SerializeToElement(new DistroSelection("fedora-kde"), CommandProtocol.JsonOptions) });
        Assert.True(compatible.Succeeded);
        var plan = await commands.ExecuteAsync(Request("migration.plan") with
        { Input = JsonSerializer.SerializeToElement(new MigrationPlanInput("fedora-kde", 0),
            CommandProtocol.JsonOptions) });
        Assert.True(plan.Succeeded);
        Assert.Equal(64, plan.ObservedStateFingerprint!.Length);
        Assert.False(plan.Data!.Value.Deserialize<MigrationPlanResult>(CommandProtocol.JsonOptions)!.Executable);
        Assert.DoesNotContain("PRIVATE", JsonSerializer.Serialize(plan, CommandProtocol.JsonOptions),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogueSecureBootRuleIsSharedByCompatibilityAndAdvisoryPlan()
    {
        var fixture = Path.Join(Path.GetTempPath(), "igloo-compatibility-" + Guid.NewGuid().ToString("N"));
        try
        {
            var root = RepositoryRoot();
            var original = new DistroLoader(NullLogger<DistroLoader>.Instance);
            original.Load(Path.Join(root, "distros"));
            var distro = Assert.Single(original.LoadedDistros, d => d.Id == "fedora-kde");
            var folder = Path.Join(fixture, distro.Id);
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(Path.Join(folder, "distro.json"),
                JsonSerializer.Serialize(distro with { Tags = [] }, CommandProtocol.JsonOptions));
            var loader = new DistroLoader(NullLogger<DistroLoader>.Instance);
            loader.Load(fixture);
            var registry = new DistroRegistry(NullLogger<DistroRegistry>.Instance);
            await registry.LoadAsync(Path.Join(root, "distros"));
            var checker = new CheckerWithSecureBoot();
            var dispatcher = new CommandDispatcher(checker, loader, registry);
            var selection = JsonSerializer.SerializeToElement(new DistroSelection(distro.Id),
                CommandProtocol.JsonOptions);
            var compatibility = await dispatcher.ExecuteAsync(Request("distro.compatibility") with
            { Input = selection });
            Assert.True(compatibility.Succeeded);
            Assert.Contains(compatibility.Data!.Value.Deserialize<DistroCompatibility>(
                CommandProtocol.JsonOptions)!.Findings, f => f.Code == "SecureBootUnsupported" &&
                f.Severity == "Blocker");
            var plan = await dispatcher.ExecuteAsync(Request("migration.plan") with
            { Input = JsonSerializer.SerializeToElement(new MigrationPlanInput(distro.Id, 0),
                CommandProtocol.JsonOptions) });
            Assert.Contains("DistroCompatibilityBlocker", plan.Data!.Value.Deserialize<MigrationPlanResult>(
                CommandProtocol.JsonOptions)!.BlockerCodes);
        }
        finally
        {
            DeleteFixtureDirectory(fixture);
        }
    }

    [Fact]
    public async Task AdvisoryPlanV2HasDurableIdentityAndKeepsV1Available()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-plan-" + Guid.NewGuid().ToString("N"));
        try
        {
            var loader = new DistroLoader(NullLogger<DistroLoader>.Instance);
            loader.Load(Path.Join(RepositoryRoot(), "distros"));
            var registry = new DistroRegistry(NullLogger<DistroRegistry>.Instance);
            await registry.LoadAsync(Path.Join(RepositoryRoot(), "distros"));
            var dispatcher = new CommandDispatcher(new Checker(), loader, registry,
                planJournal: new FileAdvisoryPlanJournal(directory));
            var input = JsonSerializer.SerializeToElement(new MigrationPlanInput("fedora-kde", 0),
                CommandProtocol.JsonOptions);
            var v1 = await dispatcher.ExecuteAsync(Request("migration.plan") with { Input = input });
            Assert.True(v1.Succeeded);
            Assert.False(v1.Data!.Value.Deserialize<MigrationPlanResult>(CommandProtocol.JsonOptions)!.Executable);
            var v2 = await dispatcher.ExecuteAsync(Request("migration.plan", version: 2) with { Input = input });
            Assert.True(v2.Succeeded);
            var plan = v2.Data!.Value.Deserialize<MigrationPlanResultV2>(CommandProtocol.JsonOptions)!;
            Assert.NotEqual(Guid.Empty, plan.PlanId);
            Assert.False(plan.Executable);
            Assert.Equal("AdvisoryOnly", plan.Disposition);
            Assert.Equal(64, plan.ProviderRevisionFingerprint.Length);
            Assert.Equal(v2.ObservedStateFingerprint, plan.ObservedStateFingerprint);
            Assert.Equal(ObservedStateRevision.Fingerprint(await new Checker().RunAsync()),
                plan.ObservedMachineFingerprint);
            Assert.Equal(plan.ObservedStateFingerprint,
                MigrationStateRevision.Bind(ObservedStateRevision.Fingerprint(await new Checker().RunAsync()),
                    plan.ProviderRevisionFingerprint, plan.DistroId));
            Assert.NotEqual(plan.ObservedStateFingerprint,
                MigrationStateRevision.Bind(new string('A', 64), plan.ProviderRevisionFingerprint,
                    plan.DistroId));
            var reopened = new CommandDispatcher(new Checker(), loader, registry,
                planJournal: new FileAdvisoryPlanJournal(directory));
            var read = await reopened.ExecuteAsync(Request("migration.plan.read") with
            { Input = JsonSerializer.SerializeToElement(new PlanSelection(plan.PlanId),
                CommandProtocol.JsonOptions) });
            var reopenedPlan = read.Data!.Value.Deserialize<MigrationPlanResultV2>(CommandProtocol.JsonOptions)!;
            Assert.Equal(plan.PlanId, reopenedPlan.PlanId);
            Assert.Equal(plan.ObservedStateFingerprint, reopenedPlan.ObservedStateFingerprint);
            Assert.Equal(plan.ProviderRevisionFingerprint, reopenedPlan.ProviderRevisionFingerprint);
            Assert.Equal(plan.BlockerCodes.ToArray(), reopenedPlan.BlockerCodes.ToArray());
            Assert.DoesNotContain("PRIVATE", await File.ReadAllTextAsync(Path.Join(directory,
                plan.PlanId.ToString("N") + ".json")), StringComparison.Ordinal);
            Assert.Contains(dispatcher.Capabilities, c => c.Id == "migration.plan" && c.Version == 1);
            Assert.Contains(dispatcher.Capabilities, c => c.Id == "migration.plan" && c.Version == 2);
            var described = await dispatcher.ExecuteAsync(Request("system.capability.describe") with
            { Input = JsonSerializer.SerializeToElement(new CapabilitySelection("migration.plan"),
                CommandProtocol.JsonOptions) });
            Assert.Equal(2, described.Data!.Value.Deserialize<CapabilityDescriptor>(
                CommandProtocol.JsonOptions)!.Version);

            using var cliOutput = new StringWriter();
            using var cliError = new StringWriter();
            Assert.Equal(0, await CliApplication.RunAsync(["migration", "plan", "fedora-kde", "0", "--json"],
                dispatcher, new StringReader(""), cliOutput, cliError));
            using var cliPlan = JsonDocument.Parse(cliOutput.ToString());
            Assert.Equal(2, cliPlan.RootElement.GetProperty("capabilityVersion").GetInt32());
            var cliPlanId = cliPlan.RootElement.GetProperty("data").GetProperty("planId").GetGuid();
            cliOutput.GetStringBuilder().Clear();
            Assert.Equal(0, await CliApplication.RunAsync(["migration", "plans", "read",
                cliPlanId.ToString(), "--json"], reopened, new StringReader(""), cliOutput, cliError));
            using var cliRead = JsonDocument.Parse(cliOutput.ToString());
            Assert.Equal(cliPlanId, cliRead.RootElement.GetProperty("data").GetProperty("planId").GetGuid());

            var recordPath = Path.Join(directory, plan.PlanId.ToString("N") + ".json");
            var record = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(recordPath))!;
            record["plan"]!["requiredBytes"] = 42;
            await File.WriteAllTextAsync(recordPath, record.ToJsonString());
            var corrupt = await reopened.ExecuteAsync(Request("migration.plan.read") with
            { Input = JsonSerializer.SerializeToElement(new PlanSelection(plan.PlanId),
                CommandProtocol.JsonOptions) });
            Assert.Equal(CommandErrorCode.VerificationFailed, corrupt.Error!.Code);
            record["plan"]!["observedMachineFingerprint"] = null;
            await File.WriteAllTextAsync(recordPath, record.ToJsonString());
            var missingRevision = await reopened.ExecuteAsync(Request("migration.plan.read") with
            { Input = JsonSerializer.SerializeToElement(new PlanSelection(plan.PlanId),
                CommandProtocol.JsonOptions) });
            Assert.Equal(CommandErrorCode.VerificationFailed, missingRevision.Error!.Code);
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    private sealed class CheckerWithSecureBoot : IPreflightChecker
    {
        public async Task<PreflightReport> RunAsync(CancellationToken ct = default) =>
            (await new Checker().RunAsync(ct)) with { SecureBootEnabled = true };
    }

    [Fact]
    public async Task CommunityIsoWorkflowAndCliCommandShareHostAcquisitionSemantics()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-host-iso-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var acquisition = new FakeIsoAcquisition();
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddLocalBackend();
            services.Replace(ServiceDescriptor.Singleton<IIsoAcquisitionService>(acquisition));
            services.Replace(ServiceDescriptor.Singleton<IPreflightChecker>(new Checker()));
            services.Replace(ServiceDescriptor.Singleton<IOperationJournal>(new FileOperationJournal(directory)));
            using var provider = services.BuildServiceProvider();
            await provider.GetRequiredService<IBackendCatalog>().LoadAsync(Path.Join(RepositoryRoot(), "distros"));
            var distro = Assert.Single(provider.GetRequiredService<IProviderCatalog>().LoadedDistros,
                d => d.Id == "fedora-kde");
            var communityResult = await provider.GetRequiredService<IIsoWorkflow>().AcquireAsync(distro, null);
            Assert.True(communityResult.GpgVerified);
            var communitySpec = acquisition.LastSpec;
            var commands = provider.GetRequiredService<ICommandDispatcher>();
            var request = Request("iso.acquire") with
            { Input = JsonSerializer.SerializeToElement(new DistroSelection(distro.Id), CommandProtocol.JsonOptions) };
            var cliResult = await commands.ExecuteAsync(request);
            Assert.True(cliResult.Succeeded);
            Assert.Equal(communitySpec, acquisition.LastSpec);
            Assert.True(cliResult.Data!.Value.Deserialize<IsoAcquired>(CommandProtocol.JsonOptions)!.GpgVerified);
            Assert.DoesNotContain("PRIVATE_PATH", JsonSerializer.Serialize(cliResult, CommandProtocol.JsonOptions),
                StringComparison.Ordinal);
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public async Task ArtifactIdentitySurvivesRegistryReopenAndDetectsChangedCacheBytes()
    {
        var root = Path.Join(Path.GetTempPath(), "igloo-artifact-" + Guid.NewGuid().ToString("N"));
        try
        {
            var cache = Path.Join(root, "cache", "fedora-kde");
            Directory.CreateDirectory(cache);
            var path = Path.Join(cache, "fixture.iso");
            var bytes = System.Text.Encoding.UTF8.GetBytes("verified fixture bytes");
            await File.WriteAllBytesAsync(path, bytes);
            var digest = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
            var loader = new DistroLoader(NullLogger<DistroLoader>.Instance);
            loader.Load(Path.Join(RepositoryRoot(), "distros"));
            var registry = new FileArtifactRegistry(Path.Join(root, "records"), Path.Join(root, "cache"));
            var journal = new FileOperationJournal(Path.Join(root, "operations"));
            var dispatcher = new CommandDispatcher(new Checker(), loader,
                new DistroRegistry(NullLogger<DistroRegistry>.Instance),
                [new IsoAcquireHandler(loader, new IsoAcquisitionCoordinator(
                    new FakeArtifactAcquisition(path, digest, bytes.Length)), journal,
                    registry, artifactMode: true), new ArtifactStatusHandler(registry)]);
            var acquired = await dispatcher.ExecuteAsync(Request("artifact.acquire") with
            { Input = JsonSerializer.SerializeToElement(new DistroSelection("fedora-kde"),
                CommandProtocol.JsonOptions) });
            Assert.True(acquired.Succeeded);
            var identity = acquired.Data!.Value.Deserialize<ArtifactAcquired>(CommandProtocol.JsonOptions)!;
            Assert.StartsWith("sha256-", identity.ArtifactId, StringComparison.Ordinal);
            Assert.Equal(digest, identity.Sha256);
            Assert.DoesNotContain(path, JsonSerializer.Serialize(acquired, CommandProtocol.JsonOptions),
                StringComparison.OrdinalIgnoreCase);
            Assert.Equal(LocalOperationState.Completed,
                (await journal.ReadAsync(acquired.OperationId!.Value))!.State);

            var reopened = new FileArtifactRegistry(Path.Join(root, "records"), Path.Join(root, "cache"));
            var selected = Request("artifact.status") with
            { Input = JsonSerializer.SerializeToElement(new ArtifactSelection(identity.ArtifactId),
                CommandProtocol.JsonOptions) };
            var statusDispatcher = new CommandDispatcher(new Checker(), loader,
                new DistroRegistry(NullLogger<DistroRegistry>.Instance),
                [new ArtifactStatusHandler(reopened)]);
            var status = await statusDispatcher.ExecuteAsync(selected);
            Assert.True(status.Data!.Value.Deserialize<ArtifactStatus>(CommandProtocol.JsonOptions)!.VerifiedNow);

            await File.WriteAllBytesAsync(path, System.Text.Encoding.UTF8.GetBytes("tampered fixture bytes"));
            status = await statusDispatcher.ExecuteAsync(selected);
            Assert.False(status.Data!.Value.Deserialize<ArtifactStatus>(CommandProtocol.JsonOptions)!.VerifiedNow);
            Assert.DoesNotContain(path, JsonSerializer.Serialize(status, CommandProtocol.JsonOptions),
                StringComparison.OrdinalIgnoreCase);

            var recordPath = Path.Join(root, "records", identity.ArtifactId + ".json");
            var record = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(recordPath))!;
            record["sizeBytes"] = bytes.Length + 1;
            await File.WriteAllTextAsync(recordPath, record.ToJsonString());
            var corrupt = await statusDispatcher.ExecuteAsync(selected);
            Assert.Equal(CommandErrorCode.VerificationFailed, corrupt.Error!.Code);
            Assert.DoesNotContain(path, JsonSerializer.Serialize(corrupt, CommandProtocol.JsonOptions),
                StringComparison.OrdinalIgnoreCase);

            var badAcquisition = new CommandDispatcher(new Checker(), loader,
                new DistroRegistry(NullLogger<DistroRegistry>.Instance),
                [new IsoAcquireHandler(loader, new IsoAcquisitionCoordinator(
                    new FakeArtifactAcquisition(path, new string('A', 64), bytes.Length)), journal,
                    registry, artifactMode: true)]);
            var rejected = await badAcquisition.ExecuteAsync(Request("artifact.acquire") with
            { Input = JsonSerializer.SerializeToElement(new DistroSelection("fedora-kde"),
                CommandProtocol.JsonOptions) });
            Assert.Equal(CommandErrorCode.VerificationFailed, rejected.Error!.Code);
        }
        finally
        {
            DeleteFixtureDirectory(root);
        }
    }

    private sealed class FakeArtifactAcquisition(string path, string digest, long size)
        : IIsoAcquisitionService
    {
        public Task<IsoAcquisitionResult> AcquireAsync(IsoSpecification spec,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken ct = default) =>
            Task.FromResult(new IsoAcquisitionResult(path, true, true, size, digest));
    }

    [Fact]
    public async Task JournalIsDurableRejectsReplayAndPublishesOnlySafeStatus()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-operation-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new FileOperationJournal(directory);
            var request = Request("iso.acquire") with { OperationId = Guid.NewGuid() };
            await journal.CreateAsync(request, "ABCD");
            await journal.TransitionAsync(request.OperationId!.Value, LocalOperationState.Running,
                "iso.acquiring");
            Assert.Throws<ResourceBusyException>(() => journal.CreateAsync(request, "ABCD")
                .GetAwaiter().GetResult());
            var reopened = new FileOperationJournal(directory);
            var status = await new OperationStatusHandler(reopened, false).ExecuteAsync(
                Request("operation.status") with
                { Input = JsonSerializer.SerializeToElement(new OperationSelection(request.OperationId.Value),
                    CommandProtocol.JsonOptions) }, null, CancellationToken.None);
            Assert.Equal("Running", Assert.IsType<OperationStatus>(status).State);
            await journal.TransitionAsync(request.OperationId.Value, LocalOperationState.Completed,
                "iso.verified", ["Sha256Verified"]);
            await Assert.ThrowsAsync<InvalidOperationException>(() => journal.TransitionAsync(
                request.OperationId.Value, LocalOperationState.Running, "iso.acquiring"));
            var bytes = await File.ReadAllTextAsync(Path.Join(directory,
                request.OperationId.Value.ToString("N") + ".json"));
            Assert.DoesNotContain("PRIVATE", bytes, StringComparison.Ordinal);
        }
        finally
        {
            DeleteFixtureDirectory(directory);
        }
    }

    [Fact]
    public async Task StateRevisionChangesWithRelevantDiskObservationAndLocksAreCrossCall()
    {
        var first = await new Checker().RunAsync();
        var fingerprint = ObservedStateRevision.Fingerprint(first);
        Assert.True(ObservedStateRevision.Matches(first, fingerprint));
        Assert.False(ObservedStateRevision.Matches(first with { SecureBootEnabled = true }, fingerprint));
        using var lease = await ResourceLock.AcquireManyAsync(["disk:fixture", "partition-table:fixture"]);
        await Assert.ThrowsAsync<ResourceBusyException>(async () =>
        {
            using var denied = await ResourceLock.AcquireAsync("disk:fixture");
        });
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ResourceLock.AcquireAsync("unrelated", cancelled.Token));
    }

    [Fact]
    public async Task CommunityCoordinatorAndCommandUseSameVerifiedIsoPath()
    {
        var root = RepositoryRoot();
        var loader = new DistroLoader(NullLogger<DistroLoader>.Instance);
        loader.Load(Path.Join(root, "distros"));
        var distro = Assert.Single(loader.LoadedDistros, d => d.Id == "fedora-kde");
        var fake = new FakeIsoAcquisition();
        var coordinator = new IsoAcquisitionCoordinator(fake);
        var direct = await coordinator.AcquireAsync(distro, null);
        Assert.True(direct.Sha256Verified);
        var first = fake.LastSpec;
        var dispatcher = new CommandDispatcher(new Checker(), loader,
            new DistroRegistry(NullLogger<DistroRegistry>.Instance),
            [new IsoAcquireHandler(loader, coordinator)]);
        var request = new CommandRequest(1, "iso.acquire", 1, Guid.NewGuid(), Guid.NewGuid(),
            JsonSerializer.SerializeToElement(new DistroSelection(distro.Id), CommandProtocol.JsonOptions));
        var result = await dispatcher.ExecuteAsync(request);
        Assert.True(result.Succeeded);
        Assert.NotNull(result.OperationId);
        Assert.Equal(first, fake.LastSpec);
        Assert.DoesNotContain("PRIVATE_PATH", JsonSerializer.Serialize(result, CommandProtocol.JsonOptions), StringComparison.Ordinal);
        var data = result.Data!.Value.Deserialize<IsoAcquired>(CommandProtocol.JsonOptions)!;
        Assert.True(data.GpgVerified);
        using var lease = await ResourceLock.AcquireAsync("iso:" + distro.Id);
        var busy = await dispatcher.ExecuteAsync(request with { RequestId = Guid.NewGuid() });
        Assert.Equal(CommandErrorCode.ResourceBusy, busy.Error!.Code);
    }

    [Fact]
    public async Task FixedInvokeRequestHasStableJsonEnvelope()
    {
        var request = new CommandRequest(1, "distro.list", 1,
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            JsonSerializer.SerializeToElement(new { }));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApplication.RunAsync(["invoke"], Dispatcher(),
            new StringReader(JsonSerializer.Serialize(request, CommandProtocol.JsonOptions)), output, error);
        Assert.Equal(0, exit);
        Assert.Equal("{\"protocolVersion\":1,\"capabilityId\":\"distro.list\",\"capabilityVersion\":1," +
            "\"requestId\":\"11111111-1111-1111-1111-111111111111\"," +
            "\"correlationId\":\"22222222-2222-2222-2222-222222222222\"," +
            "\"succeeded\":true,\"data\":{\"distros\":[]},\"warnings\":[],\"evidence\":[]}" + Environment.NewLine,
            output.ToString());
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public void PublishedV1SchemasAreValidJsonAndCoverTheImplementedPayloads()
    {
        var directory = Path.Join(RepositoryRoot(), "docs", "spec", "command-contract");
        foreach (var name in new[] { "request", "result", "capability", "capabilities", "stream-event" })
        {
            using var schema = JsonDocument.Parse(File.ReadAllText(Path.Join(directory, name + ".schema.json")));
            Assert.Equal("https://json-schema.org/draft/2020-12/schema",
                schema.RootElement.GetProperty("$schema").GetString());
        }
        using var payloads = JsonDocument.Parse(File.ReadAllText(Path.Join(directory, "capabilities.schema.json")));
        foreach (var name in new[] { "distroSelectionInput", "distroListResult",
            "systemInspectionResult", "systemInspectionDetailResult", "isoAcquiredResult",
            "artifactAcquiredResult", "artifactSelectionInput", "artifactStatusResult", "migrationInspectionResult",
            "migrationPlanInput", "migrationPlanResult", "migrationPlanResultV2",
            "planSelectionInput", "migrationTargetBindingResult", "migrationAllocationInput",
            "migrationAllocationResult", "recoveryInspectionResult",
            "operationSelectionInput", "operationStatusResult" })
            Assert.True(payloads.RootElement.GetProperty("$defs").TryGetProperty(name, out _));
        var planV2 = payloads.RootElement.GetProperty("$defs").GetProperty("migrationPlanResultV2");
        Assert.Contains(planV2.GetProperty("required").EnumerateArray(), item =>
            item.GetString() == "observedMachineFingerprint");
        Assert.True(planV2.GetProperty("properties").TryGetProperty(
            "observedMachineFingerprint", out _));
        var allocation = payloads.RootElement.GetProperty("$defs")
            .GetProperty("migrationAllocationResult");
        Assert.False(allocation.GetProperty("properties").GetProperty("executable")
            .GetProperty("const").GetBoolean());
        Assert.Equal("WindowsBootNtfsShrinkIntentV1", allocation.GetProperty("properties")
            .GetProperty("scope").GetProperty("const").GetString());
    }

    [Fact]
    public async Task StagingCoordinatorWritesPrivateManifestAfterCopyWithoutUiExecution()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-command-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var service = new FakeStaging(directory);
            var hooks = new MigrationStagingHooks(
                _ =>
                {
                    Assert.True(service.Called);
                    return Task.FromResult<IReadOnlyList<WifiNetwork>>([]);
                },
                (browsers, _, _) => Task.FromResult(browsers),
                (manifest, _) => manifest,
                (manifest, _) => manifest);
            var coordinator = new MigrationStagingCoordinator(service,
                new DistroRegistry(NullLogger<DistroRegistry>.Instance),
                NullLogger<MigrationStagingCoordinator>.Instance, hooks);
            var user = new UserSetup { WindowsUsername = "fixture", LinuxUsername = "fixture",
                LinuxPassword = "fixture-secret" };
            var result = await coordinator.StageAsync(new(new FileStagingRequest("fixture-distro", []),
                user, (await new Checker().RunAsync()), null, DiskInstallMode.ReplaceDisk, 0), null);
            Assert.Equal(directory, result.StagingDirectory);
            var manifest = await File.ReadAllTextAsync(Path.Join(directory, "migration-manifest.json"));
            Assert.Contains("fixture-distro", manifest, StringComparison.Ordinal);
            Assert.DoesNotContain("fixture-secret", manifest, StringComparison.Ordinal);
        }
        finally
        {
            var full = Path.GetFullPath(directory);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), full, StringComparison.OrdinalIgnoreCase);
            Directory.Delete(full, recursive: true);
        }
    }

    private static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "Igloo.sln"))) root = root.Parent;
        return root?.FullName ?? throw new DirectoryNotFoundException();
    }

    private static void DeleteFixtureDirectory(string directory)
    {
        var full = Path.GetFullPath(directory);
        var temp = Path.GetFullPath(Path.GetTempPath());
        Assert.StartsWith(temp, full, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith("igloo-", Path.GetFileName(full), StringComparison.Ordinal);
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    private sealed class FakeIsoAcquisition : IIsoAcquisitionService
    {
        public IsoSpecification? LastSpec { get; private set; }
        public Task<IsoAcquisitionResult> AcquireAsync(IsoSpecification spec,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken ct = default)
        {
            LastSpec = spec;
            return Task.FromResult(new IsoAcquisitionResult("PRIVATE_PATH", true, true, 123));
        }
    }

    private sealed class FakeStaging(string directory) : IFileStagingService
    {
        public bool Called { get; private set; }
        public Task<FileStagingResult> StageAsync(FileStagingRequest request,
            IProgress<FileStagingProgress>? progress, CancellationToken ct = default)
        {
            Called = true;
            return Task.FromResult(new FileStagingResult(directory, 0, 0));
        }
    }
}
