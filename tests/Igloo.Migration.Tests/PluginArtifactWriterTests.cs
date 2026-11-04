using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Migration.Tests;

public sealed class PluginArtifactWriterTests
{
    [Fact]
    public async Task ValidatesPayloadBeforeWritingAndPreservesArtifactBytes()
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

    [Theory]
    [InlineData("fedora-kde")]
    [InlineData("debian")]
    [InlineData("linuxmint-cinnamon")]
    public async Task MissingRequiredFirstBootFilesLeaveNoInstallerConfig(string distroId)
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-artifacts-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            var manifest = Manifest(directory);
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                PluginArtifactWriter.WriteAsync(new Plugin(directory, distroId), manifest,
                    directory, NullLogger.Instance));
            Assert.False(File.Exists(Path.Join(directory, "seed.cfg")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task TraversalInArtifactPathIsRejectedBeforeAnyWrite()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-artifacts-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                PluginArtifactWriter.WriteAsync(new Plugin(directory, extraPath: "../escape.cfg"),
                    Manifest(directory), directory, NullLogger.Instance));
            Assert.False(File.Exists(Path.Join(directory, "seed.cfg")));
            Assert.False(File.Exists(Path.Join(directory, "..", "escape.cfg")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task InstallerExtraCannotReplaceThePrimaryConfig()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-artifacts-" + Guid.NewGuid());
        Directory.CreateDirectory(directory);
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() =>
                PluginArtifactWriter.WriteAsync(new Plugin(directory, extraPath: "seed.cfg"),
                    Manifest(directory), directory, NullLogger.Instance));
            Assert.False(File.Exists(Path.Join(directory, "seed.cfg")));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static MigrationManifest Manifest(string directory) => new()
    {
        DistroId = "test", User = new() { WindowsUsername = "test", PreferredLinuxUsername = "test" },
        Files = new() { StagingPath = directory }, Hardware = new(),
    };

    private sealed class Plugin(string directory, string id = "test", string extraPath = "nested/extra") : IDistroPlugin
    {
        public MigrationManifest? Manifest { get; private set; }
        public string Id => id;
        public DistroMetadata Metadata => throw new NotSupportedException();
        public IReadOnlyList<PreflightFinding> CheckCompatibility(PreflightReport report) => throw new NotSupportedException();
        public InstallerBootSpec GetInstallerBootSpec() => throw new NotSupportedException();
        public Task<InstallerConfig> RenderInstallerConfigAsync(MigrationManifest manifest, CancellationToken ct = default)
        {
            Manifest = manifest;
            return Task.FromResult(new InstallerConfig("seed.cfg", new byte[] { 1, 2, 3 },
                [new(extraPath, new byte[] { 4, 5 })]));
        }
        public Task<AgentPayload> GetAgentPayloadAsync(CancellationToken ct = default)
        {
            Assert.False(File.Exists(Path.Join(directory, "seed.cfg")));
            Assert.False(File.Exists(Path.Join(directory, "nested", "extra")));
            return Task.FromResult(new AgentPayload([new("nested/agent.py", new byte[] { 6, 7 }, false)]));
        }
    }
}
