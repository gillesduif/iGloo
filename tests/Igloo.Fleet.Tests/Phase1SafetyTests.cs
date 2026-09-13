using System.Xml.Linq;
using Igloo.Fleet.Agent;
using Igloo.Fleet.Contracts;
using Xunit;

namespace Igloo.Fleet.Tests;

public sealed class Phase1SafetyTests
{
    internal static string RepositoryRoot()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Join(root.FullName, "Igloo.sln"))) root = root.Parent;
        return root?.FullName ?? throw new DirectoryNotFoundException();
    }

    [Fact]
    public void AgentHasOnlyReadOnlyWorkAndNoDestructiveServiceCalls()
    {
        Assert.Equal(new[] { ReadOnlyWorkType.RunAssessment, ReadOnlyWorkType.RunMigrationDryRun }, Enum.GetValues<ReadOnlyWorkType>());
        var root = RepositoryRoot();
        var directory = Path.Join(root, "src", "Igloo.Fleet.Agent");
        var forbidden = new[] { "IDirectInstallService", "DirectInstallService", "PartitionResizeService",
            "LinuxRemovalService", "IUsbWriterService", "UsbWriterService", "IFileStagingService",
            "RenderInstallerConfigAsync(", "GetAgentPayloadAsync(", "Process.Start(", "SetFirmwareEnvironmentVariable",
            "RegisterBootEntryAsync(", "PluginArtifactWriter" };
        foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.TopDirectoryOnly))
        {
            var source = File.ReadAllText(file);
            Assert.All(forbidden, token => Assert.DoesNotContain(token, source, StringComparison.Ordinal));
        }
        var references = XDocument.Load(Path.Join(directory, "Igloo.Fleet.Agent.csproj")).Descendants("ProjectReference")
            .Select(e => e.Attribute("Include")!.Value);
        Assert.All(references, reference =>
        {
            Assert.DoesNotContain("Igloo.Migration", reference, StringComparison.Ordinal);
            Assert.DoesNotContain("Igloo.UsbWriter", reference, StringComparison.Ordinal);
            Assert.DoesNotContain("Igloo.Iso", reference, StringComparison.Ordinal);
        });
        Assert.DoesNotContain(typeof(ReadOnlyPlanner).Assembly.GetReferencedAssemblies(),
            a => a.Name is "Igloo.Migration" or "Igloo.UsbWriter" or "Igloo.Community.App");
    }
}
