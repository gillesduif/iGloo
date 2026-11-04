using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Igloo.Cli;
using Igloo.Cli.Client;
using Igloo.Command.Contracts;
using Xunit;

namespace Igloo.Command.Tests;

public sealed class CliProcessBoundaryTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Igloo.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private static string Executable()
    {
        // These process tests must exercise the CLI built in the same configuration
        // as this assembly. The development locator prefers Debug when both exist.
        var framework = Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new DirectoryNotFoundException();
        return CliExecutableLocator.Validate(Path.Join(RepositoryRoot(), "src", "Igloo.Cli",
            "Igloo.Cli", "bin", configuration, "portable", framework, CliExecutableLocator.ExecutableName));
    }

    private static string StreamFixtureExecutable()
    {
        var framework = Path.GetFileName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory));
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new DirectoryNotFoundException();
        var executable = Path.Join(RepositoryRoot(), "tests", "Igloo.Command.Tests",
            "StreamFixture", "bin", configuration, framework,
            CliExecutableLocator.ExecutableName);
        Assert.True(File.Exists(executable), "The isolated stream fixture must be built with the command tests.");
        return executable;
    }

    [Fact]
    public void BuiltCliCarriesTheProviderFirstBootPayload()
    {
        var cliDistros = Path.Join(Path.GetDirectoryName(Executable())!, "distros");
        var configuration = new DirectoryInfo(AppContext.BaseDirectory).Parent?.Name
            ?? throw new DirectoryNotFoundException();
        foreach (var distro in new[] { "debian", "linuxmint-cinnamon", "fedora-kde" })
        {
            var sourceAgent = Path.Join(RepositoryRoot(), "distros",
                distro == "fedora-kde" ? "fedora-kde" : "_debian-family", "agent");
            var builtAgent = Path.Join(RepositoryRoot(), "distros", distro, "bin",
                configuration, "net8.0", distro is "fedora-kde" or "linuxmint-cinnamon"
                    ? distro : string.Empty, "agent");
            var packagedAgent = Path.Join(cliDistros, distro, "agent");
            var names = new List<string> { "agent.py", "first-boot.sh", "igloo_boot.py",
                "grub-theme-stylish-1080p.tar.gz", "grub-theme-stylish-4k.tar.gz" };
            if (distro == "linuxmint-cinnamon")
                names.Add("igloo-verify-seed.py");
            foreach (var name in names)
            {
                var built = Path.Join(builtAgent, name);
                var packaged = Path.Join(packagedAgent, name);
                Assert.True(File.Exists(built), $"Provider build payload is missing {distro}/{name}.");
                Assert.True(File.Exists(packaged), $"CLI package payload is missing {distro}/{name}.");
                if (name is "agent.py" or "first-boot.sh")
                    Assert.Equal(File.ReadAllBytes(Path.Join(sourceAgent, name)), File.ReadAllBytes(built));
                if (name == "igloo_boot.py")
                    Assert.Equal(File.ReadAllBytes(Path.Join(RepositoryRoot(), "distros", "_shared",
                        "agent", name)), File.ReadAllBytes(built));
                Assert.Equal(File.ReadAllBytes(built), File.ReadAllBytes(packaged));
            }
            Assert.Empty(Directory.EnumerateFiles(packagedAgent, "*.pyc",
                SearchOption.AllDirectories));
            if (distro is "debian" or "linuxmint-cinnamon")
            {
                // Keep the executable installer's inputs adjacent to the provider
                // that renders the configuration and declares its initrd extras.
                var builtPreseed = Path.Join(RepositoryRoot(), "distros", distro, "bin",
                    configuration, "net8.0", distro, "preseed");
                var packagedPreseed = Path.Join(cliDistros, distro, "preseed");
                var preseedNames = distro == "debian"
                    ? new[] { "igloo-bound-free-setup.sh", "igloo-bound-free-choices.sh",
                        "igloo-bound-free-do-option.sh" }
                    : new[] { "igloo-bootstrap.sh", "igloo-bootstrap.service",
                        "igloo-partman.sh.template", "igloo-bound-free-choices.sh",
                        "igloo-bound-free-do-option.sh", "igloo-bound-efi-fstab.sh" };
                foreach (var name in preseedNames)
                    Assert.Equal(File.ReadAllBytes(Path.Join(builtPreseed, name)),
                        File.ReadAllBytes(Path.Join(packagedPreseed, name)));
            }
        }
    }

    [Fact]
    public void ClientAndOneKeepEndpointExecutionBehindTheCliProject()
    {
        string[] References(string project)
        {
            var document = XDocument.Load(Path.Join(RepositoryRoot(), project));
            return document.Descendants("ProjectReference")
                .Select(item => Path.GetFileNameWithoutExtension(item.Attribute("Include")!.Value))
                .Order(StringComparer.Ordinal).ToArray();
        }

        Assert.Equal(new[] { "Igloo.Command.Contracts" }, References(
            "src/Igloo.Cli.Client/Igloo.Cli.Client.csproj"));
        Assert.Equal(new[] { "Igloo.Cli.Client", "Igloo.Command.Contracts", "Igloo.Mascot.Wpf" }, References(
            "Igloo.One.App/Igloo.One.App.csproj"));
        Assert.Equal(new[] { "Igloo.Command.Contracts", "Igloo.Core.Host" }, References(
            "src/Igloo.Cli/Igloo.Cli/Igloo.Cli.csproj"));
    }

    [Fact]
    public async Task MachineModeHasOnlyJsonFramesAndOneTerminalResult()
    {
        var request = CliCommandClient.Request("system.version", new { });
        var start = new ProcessStartInfo(Executable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("invoke");
        start.ArgumentList.Add("--machine");
        using var process = Process.Start(start)!;
        var helloLine = await process.StandardOutput.ReadLineAsync();
        var hello = JsonSerializer.Deserialize<CommandStreamEvent>(helloLine!,
            CommandProtocol.JsonOptions)!;
        Assert.Equal("hello", hello.Type); // The CLI negotiates before receiving a request.
        await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(request, CommandProtocol.JsonOptions));
        process.StandardInput.Close();
        var stdout = await process.StandardOutput.ReadToEndAsync();
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, stderr);
        var lines = (helloLine + "\n" + stdout).Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var frames = lines.Select(line => JsonSerializer.Deserialize<CommandStreamEvent>(line,
            CommandProtocol.JsonOptions)!).ToArray();
        Assert.Equal("hello", frames[0].Type);
        Assert.Equal("Igloo.Cli", frames[0].Product);
        Assert.Equal(Guid.Empty, frames[0].RequestId);
        Assert.Equal("accepted", frames[1].Type);
        Assert.Equal("result", frames[^1].Type);
        Assert.Single(frames, frame => frame.Type == "result");
        Assert.All(frames.Skip(1), frame => Assert.Equal(request.RequestId, frame.RequestId));
        Assert.True(frames[^1].Result!.Succeeded);
    }

    [Fact]
    public async Task TypedClientDiscoversCapabilitiesAndKeepsStructuredErrors()
    {
        var client = new CliCommandClient(Executable());
        var catalogue = await client.DiscoverAsync();
        Assert.Contains(catalogue, capability => capability.Id == "system.version");
        Assert.DoesNotContain(catalogue, capability => capability.Id == "migration.execute");
        var distros = await client.ExecuteAsync(CliCommandClient.Request("distro.list", new { }));
        Assert.Contains(distros.Data!.Value.Deserialize<DistroList>(CommandProtocol.JsonOptions)!.Distros,
            distro => distro.Id == "fedora-kde");

        var progress = new CapturedProgress();
        var result = await client.ExecuteAsync(CliCommandClient.Request("system.version", new { }), progress);
        Assert.True(result.Succeeded);
        Assert.Equal(CommandProtocol.Version, result.ProtocolVersion);
        Assert.Contains(progress.Events, item => item.Phase == "validating");

        var unsupported = await client.ExecuteAsync(CliCommandClient.Request("migration.execute", new { }));
        Assert.Equal(CommandErrorCode.UnsupportedCapability, unsupported.Error!.Code);
        var wrongVersion = await client.ExecuteAsync(CliCommandClient.Request("system.version", new { })
            with { CapabilityVersion = 2 });
        Assert.Equal(CommandErrorCode.UnsupportedVersion, wrongVersion.Error!.Code);
        var operationId = Guid.NewGuid();
        var unknownOperation = await client.ExecuteAsync(CliCommandClient.Request("operation.status",
            new OperationSelection(operationId)) with { OperationId = operationId });
        Assert.Equal(CommandErrorCode.NotSupported, unknownOperation.Error!.Code);
        Assert.Equal(operationId, unknownOperation.OperationId);
    }

    [Fact]
    public void PackagedClientRejectsNonadjacentProviderOverride()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-cli-empty-providers-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var rejected = Assert.Throws<CliTransportException>(() =>
                CliCommandClient.ForApplication(RepositoryRoot(), distrosDirectory: directory));
            Assert.Equal(CliTransportError.IdentityMismatch, rejected.Code);
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    [Fact]
    public async Task HumanAndMachineVersionUseTheSameStructuredSemantics()
    {
        var machine = await new CliCommandClient(Executable()).ExecuteAsync(
            CliCommandClient.Request("system.version", new { }));
        var start = new ProcessStartInfo(Executable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("version");
        start.ArgumentList.Add("--json");
        using var process = Process.Start(start)!;
        var stdout = await process.StandardOutput.ReadToEndAsync();
        await process.WaitForExitAsync();
        var human = JsonSerializer.Deserialize<CommandResult>(stdout, CommandProtocol.JsonOptions)!;
        Assert.True(human.Succeeded);
        Assert.Equal(machine.CapabilityId, human.CapabilityId);
        Assert.Equal(machine.CapabilityVersion, human.CapabilityVersion);
        Assert.Equal(machine.Data!.Value.GetRawText(), human.Data!.Value.GetRawText());
        Assert.Equal(machine.Warnings, human.Warnings);
    }

    [Fact]
    public void LocatorRejectsMissingOrWrongExecutableWithoutSearchingPath()
    {
        var missing = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "Igloo.Cli.exe");
        Assert.Equal(CliTransportError.ExecutableUnavailable,
            Assert.Throws<CliTransportException>(() => CliExecutableLocator.Validate(missing)).Code);
        Assert.Equal(CliTransportError.IdentityMismatch,
            Assert.Throws<CliTransportException>(() => CliExecutableLocator.Validate(
                Path.Join(RepositoryRoot(), "iGloo.exe"))).Code);
        var outsideRepository = Path.Join(Path.GetTempPath(),
            "igloo-cli-no-package-" + Guid.NewGuid().ToString("N"));
        Assert.Equal(CliTransportError.ExecutableUnavailable,
            Assert.Throws<CliTransportException>(() =>
                CliExecutableLocator.Resolve(outsideRepository)).Code);
        Assert.Equal(CliTransportError.ExecutableUnavailable,
            Assert.Throws<CliTransportException>(() =>
                CliExecutableLocator.Resolve(Path.Join(RepositoryRoot(),
                    "tests", "Igloo.Command.Tests"))).Code);
    }

    [Fact]
    public async Task UnknownProviderIsRejectedBeforeOperationAdmissionAcrossTheRealMachineProcess()
    {
        // An unknown provider fails before acquisition or any storage effect.
        var progress = new CapturedProgress();
        var result = await new CliCommandClient(Executable()).ExecuteAsync(
            CliCommandClient.Request("iso.acquire", new DistroSelection("missing-provider-fixture")),
            progress);
        Assert.False(result.Succeeded);
        Assert.Equal(CommandErrorCode.NotSupported, result.Error!.Code);
        Assert.Null(result.OperationId);
        Assert.Empty(progress.Events);
    }

    [Fact]
    public async Task UnexpectedHandlerFailureRetainsAssignedOperationAcrossIsolatedProcess()
    {
        var request = CliCommandClient.Request("fixture.unexpected-handler", new { });
        var progress = new CapturedProgress();
        var result = await new CliCommandClient(StreamFixtureExecutable()).ExecuteAsync(request,
            progress);

        Assert.False(result.Succeeded);
        Assert.Equal(CommandErrorCode.InternalFailure, result.Error!.Code);
        Assert.NotNull(result.OperationId);
        Assert.Equal(result.OperationId, Assert.Single(progress.Events,
            item => item.Phase == "validating").OperationId);
        Assert.DoesNotContain("PRIVATE_EXCEPTION",
            JsonSerializer.Serialize(result, CommandProtocol.JsonOptions), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("fixture.inconsistent-success")]
    [InlineData("fixture.inconsistent-failure")]
    public async Task ActualSubprocessRejectsSucceededErrorContradiction(string capability)
    {
        var request = CliCommandClient.Request(capability, new { });
        var failure = await Assert.ThrowsAsync<CliTransportException>(() =>
            new CliCommandClient(StreamFixtureExecutable()).ExecuteAsync(request));
        Assert.Equal(CliTransportError.MalformedStream, failure.Code);
        Assert.Equal(request.RequestId, failure.RequestId);
        Assert.Equal(request.CorrelationId, failure.CorrelationId);
    }

    [Fact]
    public void FirstPartyClientRequiresItsPackagedCliSlot()
    {
        var package = Path.Join(Path.GetTempPath(), "igloo-cli-missing-slot-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(package);
        try
        {
            File.Copy(Executable(), Path.Join(package, CliExecutableLocator.ExecutableName));
            Assert.Equal(CliTransportError.ExecutableUnavailable,
                Assert.Throws<CliTransportException>(() =>
                    CliCommandClient.ForApplication(package)).Code);
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public void DirectCliRequiresAdjacentProvidersOrExactDevelopmentBuild()
    {
        var package = Path.Join(Path.GetTempPath(), "igloo-cli-providerless-" +
            Guid.NewGuid().ToString("N"));
        var cli = Path.Join(package, "cli");
        Directory.CreateDirectory(cli);
        try
        {
            Assert.Throws<InvalidDataException>(() => Program.FindDistrosDirectory(cli));
            var build = Path.GetDirectoryName(Executable())!;
            var adjacent = Path.Join(build, "distros");
            Assert.Equal(adjacent, Program.FindDistrosDirectory(build));
            Assert.Equal(File.ReadAllBytes(Path.Join(RepositoryRoot(), "distros", "debian",
                    "bin", new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name,
                    "net8.0", "Igloo.Distro.Debian.dll")),
                File.ReadAllBytes(Path.Join(adjacent, "debian", "Igloo.Distro.Debian.dll")));
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public void DevelopmentBackendAcceptsOnlyTheIsolatedPortablePackage()
    {
        var build = Path.GetDirectoryName(Executable())!;
        Assert.Equal(Path.Join(build, "distros"), Program.FindDistrosDirectory(build));
        var framework = new DirectoryInfo(build).Name;
        var configurationRoot = new DirectoryInfo(build).Parent!.Parent!.FullName;
        // Historical RID output trees can still exist beside this build. They
        // must not regain provider authority merely because a new layout exists.
        Assert.Throws<InvalidDataException>(() =>
            Program.FindDistrosDirectory(Path.Join(configurationRoot, framework)));
        Assert.Throws<InvalidDataException>(() =>
            Program.FindDistrosDirectory(Path.Join(build, "win-x64")));
    }

    [Fact]
    public void ClientRemovesInheritedRuntimeAndProviderOverrides()
    {
        IDictionary<string, string?> environment = new Dictionary<string, string?>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_STARTUP_HOOKS"] = "external.dll",
            ["DOTNET_ADDITIONAL_DEPS"] = "external.deps.json",
            ["DOTNET_SHARED_STORE"] = "external-store",
            ["DOTNET_ROOT"] = "external-root",
            ["DOTNET_ROLL_FORWARD"] = "LatestMajor",
            ["CORECLR_ENABLE_PROFILING"] = "1",
            ["COREHOST_TRACE"] = "1",
            ["COMPlus_ReadyToRun"] = "0",
            ["COR_ENABLE_PROFILING"] = "1",
            ["IGLOO_DISTROS_DIRECTORY"] = "external-distros",
            ["SystemRoot"] = "C:\\Windows",
            ["TEMP"] = "temporary",
        };
        CliCommandClient.RestrictChildEnvironment(environment);
        Assert.Equal(new[] { "SystemRoot", "TEMP" }, environment.Keys.Order());
    }

    [Fact]
    public async Task GatewayTimeoutRetainsRequestCorrelationAndDoesNotClaimCancellation()
    {
        var request = CliCommandClient.Request("system.version", new { }) with
        {
            OperationId = Guid.NewGuid(),
        };
        var client = new CliCommandClient(Executable(), timeout: TimeSpan.FromTicks(1));
        var failure = await Assert.ThrowsAsync<CliTransportException>(() =>
            client.ExecuteAsync(request));
        Assert.Equal(CliTransportError.TimedOut, failure.Code);
        Assert.Equal(request.RequestId, failure.RequestId);
        Assert.Equal(request.CorrelationId, failure.CorrelationId);
        Assert.Equal(request.OperationId, failure.OperationId);
    }

    [Fact]
    public async Task PrematureGatewayExitRetainsKnownOperationIdentity()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-cli-premature-exit-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Join(directory, CliExecutableLocator.ExecutableName);
            File.Copy(Executable(), executable);
            var request = CliCommandClient.Request("operation.status", new { }) with
            {
                OperationId = Guid.NewGuid(),
            };
            var failure = await Assert.ThrowsAsync<CliTransportException>(() =>
                new CliCommandClient(executable, timeout: TimeSpan.FromSeconds(5))
                    .ExecuteAsync(request));
            Assert.Equal(CliTransportError.MalformedStream, failure.Code);
            Assert.Equal(request.RequestId, failure.RequestId);
            Assert.Equal(request.CorrelationId, failure.CorrelationId);
            Assert.Equal(request.OperationId, failure.OperationId);
        }
        finally
        {
            await DeletePackageAsync(directory);
        }
    }

    [Fact]
    public void LocatorRejectsRedirectedPackageDirectory()
    {
        var package = Path.Join(Path.GetTempPath(), "igloo-cli-link-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(package);
        var link = Path.Join(package, "cli");
        try
        {
            Directory.CreateSymbolicLink(link, Path.GetDirectoryName(Executable())!);
            Assert.NotNull(new DirectoryInfo(link).LinkTarget);
            var failure = Assert.Throws<CliTransportException>(() =>
                CliExecutableLocator.Resolve(package));
            Assert.Equal(CliTransportError.IdentityMismatch, failure.Code);
        }
        finally
        {
            if (Directory.Exists(link)) Directory.Delete(link);
            Directory.Delete(package);
        }
    }

    [Fact]
    public void ClientRejectsRedirectedPackagedProviderDirectory()
    {
        var package = Path.Join(Path.GetTempPath(), "igloo-cli-provider-link-" +
            Guid.NewGuid().ToString("N"));
        var cli = Path.Join(package, "cli");
        var providers = Path.Join(package, "distros");
        var destination = Path.Join(package, "untrusted-providers");
        Directory.CreateDirectory(cli);
        Directory.CreateDirectory(destination);
        try
        {
            File.Copy(Executable(), Path.Join(cli, CliExecutableLocator.ExecutableName));
            Directory.CreateSymbolicLink(providers, destination);
            Assert.NotNull(new DirectoryInfo(providers).LinkTarget);
            Assert.Equal(CliTransportError.IdentityMismatch,
                Assert.Throws<CliTransportException>(() =>
                    CliCommandClient.ForApplication(package)).Code);
        }
        finally
        {
            if (Directory.Exists(providers)) Directory.Delete(providers);
            Directory.Delete(package, recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MachineModeRejectsOversizedOrInvalidUtf8RequestBeforeAcceptance(bool invalidUtf8)
    {
        var start = new ProcessStartInfo(Executable())
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("invoke");
        start.ArgumentList.Add("--machine");
        using var process = Process.Start(start)!;
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var hello = await process.StandardOutput.ReadLineAsync(deadline.Token);
            Assert.Equal("hello", JsonSerializer.Deserialize<CommandStreamEvent>(hello!,
                CommandProtocol.JsonOptions)!.Type);
            var payload = invalidUtf8 ? new byte[] { 0xC3, 0x28, 0x0A }
                : Encoding.ASCII.GetBytes(new string('x', 4 * 1024 * 1024 + 1) + "\n");
            await process.StandardInput.BaseStream.WriteAsync(payload, deadline.Token);
            process.StandardInput.Close();
            var output = await process.StandardOutput.ReadToEndAsync(deadline.Token);
            var error = await process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            var only = Assert.Single(output.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            var frame = JsonSerializer.Deserialize<CommandStreamEvent>(only,
                CommandProtocol.JsonOptions)!;
            Assert.Equal("result", frame.Type);
            Assert.Equal(CommandErrorCode.InvalidRequest, frame.Result!.Error!.Code);
            Assert.Equal(2, process.ExitCode);
            Assert.Equal(string.Empty, error);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task MachineModeRejectsExcessiveJsonDepthBeforeAcceptance()
    {
        var request = CliCommandClient.Request("system.version", new { });
        var ordinary = JsonSerializer.Serialize(request, CommandProtocol.JsonOptions);
        Assert.Contains("\"input\":{}", ordinary, StringComparison.Ordinal);
        var deep = ordinary.Replace("\"input\":{}",
            "\"input\":" + new string('[', 70) + "{}" + new string(']', 70),
            StringComparison.Ordinal);
        using var output = new StringWriter();
        using var error = new StringWriter();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var exit = await CliApplication.RunAsync(["invoke", "--machine"],
            new WaitingDispatcher(), new StringReader(deep + "\n"), output, error,
            deadline.Token);
        Assert.Equal(2, exit);
        var frames = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonSerializer.Deserialize<CommandStreamEvent>(line,
                CommandProtocol.JsonOptions)!).ToArray();
        Assert.Equal(new[] { "hello", "result" }, frames.Select(frame => frame.Type));
        Assert.Equal(CommandErrorCode.InvalidRequest, frames[^1].Result!.Error!.Code);
    }

    [Fact]
    public async Task HumanInvokeRejectsOversizedStandardInputBeforeDispatch()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApplication.RunAsync(["invoke"], new WaitingDispatcher(),
            new StringReader(new string('x', 4 * 1024 * 1024 + 1)), output, error);
        Assert.Equal(2, exit);
        Assert.Equal(CommandErrorCode.InvalidRequest,
            JsonSerializer.Deserialize<CommandResult>(output.ToString(),
                CommandProtocol.JsonOptions)!.Error!.Code);
    }

    [Fact]
    public void MachineCancellationControlRejectsUnknownDuplicateAndWrongBindings()
    {
        var id = Guid.NewGuid();
        var valid = JsonSerializer.Serialize(new { type = "cancel", requestId = id },
            CommandProtocol.JsonOptions);
        Assert.True(CliApplication.IsMatchingCancellation(valid, id));
        Assert.False(CliApplication.IsMatchingCancellation(valid, Guid.NewGuid()));
        Assert.False(CliApplication.IsMatchingCancellation(valid[..^1] + ",\"extra\":true}", id));
        Assert.False(CliApplication.IsMatchingCancellation("{\"type\":\"cancel\"," +
            "\"type\":\"cancel\",\"requestId\":\"" + id + "\"}", id));
    }

    [Fact]
    public async Task PackagedCliRunsOutsideRepository()
    {
        var package = Path.Join(Path.GetTempPath(), "igloo-cli-package-" +
            Guid.NewGuid().ToString("N"));
        var packagedCli = Path.Join(package, "cli");
        Directory.CreateDirectory(packagedCli);
        var packagedDistros = Path.Join(package, "distros");
        Directory.CreateDirectory(packagedDistros);
        try
        {
            var buildDirectory = Path.GetDirectoryName(Executable())!;
            foreach (var source in Directory.EnumerateFiles(buildDirectory, "*",
                         SearchOption.AllDirectories))
            {
                var destination = Path.Join(packagedCli,
                    Path.GetRelativePath(buildDirectory, source));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(source, destination);
            }
            foreach (var (id, assembly) in new[]
                     {
                         ("fedora-kde", "Igloo.Distro.FedoraKde.dll"),
                         ("debian", "Igloo.Distro.Debian.dll"),
                         ("ubuntu", "Igloo.Distro.Ubuntu.dll"),
                         ("linuxmint-cinnamon", "Igloo.Distro.LinuxmintCinnamon.dll"),
                     })
            {
                var destination = Path.Join(packagedDistros, id);
                Directory.CreateDirectory(destination);
                var source = Path.Join(RepositoryRoot(), "distros", id);
                File.Copy(Path.Join(source, "distro.json"), Path.Join(destination, "distro.json"));
                File.Copy(Path.Join(source, assembly), Path.Join(destination, assembly));
            }
            var expected = Path.Join(packagedCli, CliExecutableLocator.ExecutableName);
            Assert.Equal(expected, CliExecutableLocator.Resolve(package));
            var client = CliCommandClient.ForApplication(package);
            var result = await client.ExecuteAsync(CliCommandClient.Request("system.version", new { }));
            Assert.True(result.Succeeded);

            // A direct human CLI invocation has the same packaged-provider
            // boundary even if an inherited override points at the checkout.
            File.Delete(Path.Join(packagedDistros, "debian", "distro.json"));
            foreach (var helpArgs in new[]
                     {
                         new[] { "help" },
                         new[] { "--help" },
                         new[] { "--", "help" },
                     })
            {
                var helpStart = new ProcessStartInfo(expected)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (var argument in helpArgs) helpStart.ArgumentList.Add(argument);
                using var helpProcess = Process.Start(helpStart)!;
                var helpOutput = await helpProcess.StandardOutput.ReadToEndAsync();
                var helpError = await helpProcess.StandardError.ReadToEndAsync();
                await helpProcess.WaitForExitAsync();
                Assert.Equal(0, helpProcess.ExitCode);
                Assert.StartsWith("iGloo CLI commands:", helpOutput, StringComparison.Ordinal);
                Assert.Equal(string.Empty, helpError);
            }
            var start = new ProcessStartInfo(expected)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("version");
            start.ArgumentList.Add("--json");
            start.Environment["IGLOO_DISTROS_DIRECTORY"] = Path.Join(RepositoryRoot(), "distros");
            using var process = Process.Start(start)!;
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            Assert.Equal(4, process.ExitCode);
            Assert.Equal(string.Empty, stdout);
            Assert.Contains("LocalBackendInitializationFailed", stderr,
                StringComparison.Ordinal);
        }
        finally
        {
            await DeletePackageAsync(package);
        }
    }

    [Fact]
    public async Task ActualPublishedPackageHasProviderQueriesAndHumanMachineParity()
    {
        // Set only when validating a clean One or Fleet publish output. Routine
        // source tests have no obligation to build both frontend packages.
        var package = Environment.GetEnvironmentVariable("IGLOO_TEST_PUBLISHED_APP_DIR");
        if (string.IsNullOrWhiteSpace(package)) return;

        var executable = CliExecutableLocator.Resolve(package);
        Assert.Equal(Path.Join(package, "cli", CliExecutableLocator.ExecutableName),
            executable);
        var client = CliCommandClient.ForApplication(package);
        var capabilities = await client.DiscoverAsync();
        Assert.DoesNotContain(capabilities, item => item.Id is
            "migration.prepare" or "migration.execute" or "migration.verify");
        Assert.Contains("fedora-kde", Assert.Single(capabilities,
            item => item.Id == "distro.compatibility").SupportedProviders);

        var listed = await client.ExecuteAsync(CliCommandClient.Request("distro.list", new { }));
        Assert.True(listed.Succeeded);
        var distros = listed.Data!.Value.Deserialize<DistroList>(CommandProtocol.JsonOptions)!;
        Assert.Contains(distros.Distros, distro => distro.Id == "fedora-kde");
        var poison = Path.Join(Path.GetTempPath(), "igloo-untrusted-providers-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(poison);
        var previousOverride = Environment.GetEnvironmentVariable("IGLOO_DISTROS_DIRECTORY");
        try
        {
            Environment.SetEnvironmentVariable("IGLOO_DISTROS_DIRECTORY", poison);
            var stillPackaged = await client.ExecuteAsync(CliCommandClient.Request("distro.list", new { }));
            Assert.Contains(stillPackaged.Data!.Value.Deserialize<DistroList>(CommandProtocol.JsonOptions)!.Distros,
                distro => distro.Id == "fedora-kde");
        }
        finally
        {
            Environment.SetEnvironmentVariable("IGLOO_DISTROS_DIRECTORY", previousOverride);
            Directory.Delete(poison);
        }
        var inspected = await client.ExecuteAsync(CliCommandClient.Request("distro.inspect",
            new DistroSelection("fedora-kde")));
        Assert.True(inspected.Succeeded);
        Assert.Equal("fedora-kde", inspected.Data!.Value
            .Deserialize<DistroSummary>(CommandProtocol.JsonOptions)!.Id);

        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        };
        start.ArgumentList.Add("distros");
        start.ArgumentList.Add("inspect");
        start.ArgumentList.Add("fedora-kde");
        start.ArgumentList.Add("--json");
        using var process = Process.Start(start)!;
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(string.Empty, error);
        var human = JsonSerializer.Deserialize<CommandResult>(output, CommandProtocol.JsonOptions)!;
        Assert.True(human.Succeeded);
        Assert.Equal(inspected.CapabilityId, human.CapabilityId);
        Assert.Equal(inspected.Data!.Value.GetRawText(), human.Data!.Value.GetRawText());
    }

    [Fact]
    public async Task ClientRejectsExecutableSubstitutionBeforeDispatch()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-cli-substitution-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Join(directory, CliExecutableLocator.ExecutableName);
            File.Copy(Executable(), executable);
            var client = new CliCommandClient(executable);
            // Appending bytes leaves the PE version resource intact. Metadata alone
            // would still identify the changed file as Igloo.Cli.
            await using (var changed = new FileStream(executable, FileMode.Append, FileAccess.Write))
                changed.WriteByte(0x5A);
            Assert.Equal("Igloo.Cli", FileVersionInfo.GetVersionInfo(executable).ProductName);
            var rejected = await Assert.ThrowsAsync<CliTransportException>(() =>
                client.ExecuteAsync(CliCommandClient.Request("system.version", new { })));
            Assert.Equal(CliTransportError.IdentityMismatch, rejected.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClientRejectsAdjacentDependencySubstitutionBeforeDispatch()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-cli-dependency-" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var executable = Path.Join(directory, CliExecutableLocator.ExecutableName);
            var dependency = Path.Join(directory, "Igloo.Command.Contracts.dll");
            File.Copy(Executable(), executable);
            File.Copy(Path.Join(Path.GetDirectoryName(Executable())!,
                "Igloo.Command.Contracts.dll"), dependency);
            var client = new CliCommandClient(executable);
            await using (var changed = new FileStream(dependency, FileMode.Append, FileAccess.Write))
                changed.WriteByte(0x5A);
            var rejected = await Assert.ThrowsAsync<CliTransportException>(() =>
                client.ExecuteAsync(CliCommandClient.Request("system.version", new { })));
            Assert.Equal(CliTransportError.IdentityMismatch, rejected.Code);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task ClientRejectsPackagedProviderSubstitutionBeforeDispatch()
    {
        var package = Path.Join(Path.GetTempPath(), "igloo-cli-provider-substitution-" +
            Guid.NewGuid().ToString("N"));
        var cli = Path.Join(package, "cli");
        var provider = Path.Join(package, "distros", "fedora-kde", "distro.json");
        Directory.CreateDirectory(cli);
        Directory.CreateDirectory(Path.GetDirectoryName(provider)!);
        try
        {
            File.Copy(Executable(), Path.Join(cli, CliExecutableLocator.ExecutableName));
            File.Copy(Path.Join(RepositoryRoot(), "distros", "fedora-kde", "distro.json"),
                provider);
            var client = CliCommandClient.ForApplication(package);
            await using (var changed = new FileStream(provider, FileMode.Append, FileAccess.Write))
                changed.WriteByte(0x5A);
            var rejected = await Assert.ThrowsAsync<CliTransportException>(() =>
                client.ExecuteAsync(CliCommandClient.Request("system.version", new { })));
            Assert.Equal(CliTransportError.IdentityMismatch, rejected.Code);
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public void PackagedClientReportsOversizedFileSetInsteadOfGenericVerificationFailure()
    {
        var package = Path.Join(Path.GetTempPath(), "igloo-cli-oversized-" +
            Guid.NewGuid().ToString("N"));
        var cli = Path.Join(package, "cli");
        var distros = Path.Join(package, "distros");
        Directory.CreateDirectory(cli);
        Directory.CreateDirectory(distros);
        try
        {
            File.Copy(Executable(), Path.Join(cli, CliExecutableLocator.ExecutableName));
            for (var i = 0; i < 1024; i++)
                File.WriteAllBytes(Path.Join(distros, $"padding-{i:D4}"), []);
            var rejected = Assert.Throws<CliTransportException>(() =>
                CliCommandClient.ForApplication(package));
            Assert.Equal(CliTransportError.IdentityMismatch, rejected.Code);
            Assert.Equal("The packaged CLI file set exceeds its bound.", rejected.Message);
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public void LocatorRejectsWrongProductAtPackagedNameInsteadOfFallingBackToBuild()
    {
        var package = Path.Join(Path.GetTempPath(), "igloo-cli-wrong-product-" +
            Guid.NewGuid().ToString("N"));
        var packagedCli = Path.Join(package, "cli");
        Directory.CreateDirectory(packagedCli);
        try
        {
            var alternate = Environment.ProcessPath ?? throw new InvalidOperationException();
            Assert.NotEqual("Igloo.Cli", FileVersionInfo.GetVersionInfo(alternate).ProductName);
            File.Copy(alternate, Path.Join(packagedCli, CliExecutableLocator.ExecutableName));
            var rejected = Assert.Throws<CliTransportException>(() =>
                CliExecutableLocator.Resolve(package));
            Assert.Equal(CliTransportError.IdentityMismatch, rejected.Code);
        }
        finally
        {
            Directory.Delete(package, recursive: true);
        }
    }

    [Fact]
    public async Task MachineCancellationYieldsOneStructuredCancelledResult()
    {
        var request = CliCommandClient.Request("fixture.wait", new { });
        var input = JsonSerializer.Serialize(request, CommandProtocol.JsonOptions) + Environment.NewLine +
            JsonSerializer.Serialize(new { type = "cancel", requestId = request.RequestId },
                CommandProtocol.JsonOptions) + Environment.NewLine;
        using var output = new StringWriter();
        using var error = new StringWriter();
        var exit = await CliApplication.RunAsync(["invoke", "--machine"], new WaitingDispatcher(),
            new StringReader(input), output, error);
        Assert.Equal(130, exit);
        var frames = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries).Select(line => JsonSerializer.Deserialize<CommandStreamEvent>(line,
                CommandProtocol.JsonOptions)!).ToArray();
        Assert.Equal(CommandErrorCode.Cancelled, Assert.Single(frames,
            frame => frame.Type == "result").Result!.Error!.Code);
        Assert.Equal(string.Empty, error.ToString());
    }

    [Fact]
    public async Task ClientRejectsIncompatibleOrDuplicatedMachineFrames()
    {
        var request = CliCommandClient.Request("system.version", new { });
        string Frame(CommandStreamEvent value) => JsonSerializer.Serialize(value, CommandProtocol.JsonOptions);
        var hello = new CommandStreamEvent("hello", CommandProtocol.Version,
            Guid.Empty, Guid.Empty, "Igloo.Cli", "1.0.0.0");
        var accepted = new CommandStreamEvent("accepted", CommandProtocol.Version,
            request.RequestId, request.CorrelationId);
        var outcome = new CommandResult(CommandProtocol.Version, request.CapabilityId,
            request.CapabilityVersion, request.RequestId, request.CorrelationId, null,
            true, JsonSerializer.SerializeToElement(new { protocolVersion = 1 }), null, [], []);
        var terminal = new CommandStreamEvent("result", CommandProtocol.Version,
            request.RequestId, request.CorrelationId, Result: outcome);

        var incompatible = await Assert.ThrowsAsync<CliTransportException>(() =>
            CliCommandClient.ReadStreamAsync(new StringReader(Frame(hello with { ProtocolVersion = 2 })),
                request, null));
        Assert.Equal(CliTransportError.IncompatibleProtocol, incompatible.Code);

        var duplicated = await Assert.ThrowsAsync<CliTransportException>(() =>
            CliCommandClient.ReadStreamAsync(new StringReader(string.Join(Environment.NewLine,
                [Frame(hello), Frame(accepted), Frame(terminal), Frame(terminal)])), request, null));
        Assert.Equal(CliTransportError.MalformedStream, duplicated.Code);
        var crashed = await Assert.ThrowsAsync<CliTransportException>(() =>
            CliCommandClient.ReadStreamAsync(new StringReader(string.Empty), request, null));
        Assert.Equal(CliTransportError.MalformedStream, crashed.Code);

        foreach (var malformed in new[]
                 {
                     Frame(hello with { Product = "iGloo" }),
                     string.Join(Environment.NewLine, [Frame(hello), Frame(terminal)]),
                     string.Join(Environment.NewLine, [Frame(hello), Frame(accepted),
                         Frame(terminal with { CorrelationId = Guid.NewGuid() })]),
                     string.Join(Environment.NewLine, [Frame(hello), Frame(accepted),
                         Frame(terminal with { Result = outcome with { CapabilityId = "migration.execute" } })]),
                     string.Join(Environment.NewLine, [Frame(hello), Frame(accepted),
                         Frame(new CommandStreamEvent("progress", CommandProtocol.Version,
                             request.RequestId, request.CorrelationId,
                             Progress: new CommandProgress(Guid.NewGuid(), null, "validating"))),
                         Frame(terminal)]),
                     string.Join(Environment.NewLine, [Frame(hello), Frame(accepted),
                         Frame(accepted), Frame(terminal)]),
                     Frame(hello) + Environment.NewLine +
                         Frame(accepted) + Environment.NewLine +
                         Frame(terminal)[..^1] + ",\"unknown\":true}",
                 })
        {
            var rejected = await Assert.ThrowsAsync<CliTransportException>(() =>
                CliCommandClient.ReadStreamAsync(new StringReader(malformed), request, null));
            Assert.Equal(CliTransportError.MalformedStream, rejected.Code);
        }
    }

    [Fact]
    public async Task ReaderRejectsResultForDifferentKnownOperation()
    {
        var request = CliCommandClient.Request("operation.status", new { }) with
        {
            OperationId = Guid.NewGuid(),
        };
        var accepted = new CommandStreamEvent("accepted", CommandProtocol.Version,
            request.RequestId, request.CorrelationId);
        var result = new CommandResult(CommandProtocol.Version, request.CapabilityId,
            request.CapabilityVersion, request.RequestId, request.CorrelationId,
            Guid.NewGuid(), true, JsonSerializer.SerializeToElement(new { state = "running" }),
            null, [], []);
        string Wire(CommandResult terminal) => string.Join("\n", new[]
        {
            new CommandStreamEvent("hello", CommandProtocol.Version, Guid.Empty,
                Guid.Empty, "Igloo.Cli", "1.0.0.0"),
            accepted,
            new CommandStreamEvent("result", CommandProtocol.Version,
                request.RequestId, request.CorrelationId, Result: terminal),
        }.Select(item => JsonSerializer.Serialize(item, CommandProtocol.JsonOptions))) + "\n";
        var rejected = await Assert.ThrowsAsync<CliTransportException>(() =>
            CliCommandClient.ReadStreamAsync(new StringReader(Wire(result)), request, null));
        Assert.Equal(CliTransportError.MalformedStream, rejected.Code);
        var acceptedResult = await CliCommandClient.ReadStreamAsync(new StringReader(
            Wire(result with { OperationId = request.OperationId })), request, null);
        Assert.Equal(request.OperationId, acceptedResult.OperationId);
    }

    private sealed class CapturedProgress : IProgress<CommandProgress>
    {
        public List<CommandProgress> Events { get; } = [];
        public void Report(CommandProgress value) => Events.Add(value);
    }

    private static async Task DeletePackageAsync(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (UnauthorizedAccessException) when (attempt < 9)
            {
                // Windows can release a just-exited executable image slightly after
                // the process exit notification. This only removes this test package.
                await Task.Delay(50);
            }
        }
    }

    private sealed class WaitingDispatcher : ICommandDispatcher
    {
        public IReadOnlyList<CapabilityDescriptor> Capabilities => [];
        public async Task<CommandResult> ExecuteAsync(CommandRequest request,
            IProgress<CommandProgress>? progress = null, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException();
        }
    }
}
