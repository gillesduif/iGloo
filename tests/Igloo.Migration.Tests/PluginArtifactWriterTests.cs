using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Migration.Tests;

public sealed class PluginArtifactWriterTests
{
    [Fact]
    public async Task PreservesArtifactBytesDirectoriesAndPluginCallOrder()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-artifacts-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var plugin = new Plugin(directory);
            var manifest = new MigrationManifest
            {
                DistroId = "test", User = new() { WindowsUsername = "test", PreferredLinuxUsername = "test", FullName = "Test" },
                Files = new() { StagingPath = directory }, Hardware = new(),
            };
            await PluginArtifactWriter.WriteAsync(plugin, manifest, directory, NullLogger.Instance);
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(Path.Join(directory, "seed.cfg")));
            Assert.Equal(new byte[] { 4, 5 }, await File.ReadAllBytesAsync(Path.Join(directory, "nested", "extra")));
            Assert.Equal(new byte[] { 6, 7 }, await File.ReadAllBytesAsync(Path.Join(directory, "igloo-agent", "nested", "agent.py")));
            Assert.Same(manifest, plugin.Manifest);
        }
        finally
        {
            // Only this test's newly created GUID directory is removed.
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class Plugin(string directory) : IDistroPlugin
    {
        public MigrationManifest? Manifest { get; private set; }
        public string Id => "test";
        public DistroMetadata Metadata => throw new NotSupportedException();
        public IReadOnlyList<PreflightFinding> CheckCompatibility(PreflightReport report) => throw new NotSupportedException();
        public InstallerBootSpec GetInstallerBootSpec() => throw new NotSupportedException();
        public Task<InstallerConfig> RenderInstallerConfigAsync(MigrationManifest manifest, CancellationToken ct = default)
        {
            Manifest = manifest;
            return Task.FromResult(new InstallerConfig("seed.cfg", new byte[] { 1, 2, 3 },
                [new("nested/extra", new byte[] { 4, 5 })]));
        }
        public Task<AgentPayload> GetAgentPayloadAsync(CancellationToken ct = default)
        {
            Assert.True(File.Exists(Path.Join(directory, "seed.cfg")));
            Assert.True(File.Exists(Path.Join(directory, "nested", "extra")));
            return Task.FromResult(new AgentPayload([new("nested/agent.py", new byte[] { 6, 7 }, false)]));
        }
    }
}
