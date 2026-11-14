using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Igloo.Command.Tests;

[Collection("RecoveryState")]
public sealed class BootCleanupHostTests
{
    [Fact]
    public async Task The_task_argument_reaches_the_platform_only_as_a_partition_guid()
    {
        var direct = new RecordingDirectInstall();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddCommunityBackend();
        services.Replace(ServiceDescriptor.Singleton<IDirectInstallService>(direct));
        await using var host = services.BuildServiceProvider();
        var preparation = host.GetRequiredService<IInstallationPreparation>();

        const string partition = "dddd4e82-820a-4dd7-b745-e91cb5e232a6";
        const string entry = "{7a757dff-ddf9-11f1-9250-e8c97e719636}";
        foreach (var argument in new[] { "", "--boot-cleanup", "dddd4e82", Guid.Empty.ToString() })
        {
            Assert.Equal(BootCleanupOutcome.NotRead, await preparation.CleanUpBootEntriesAsync(argument, entry));
            Assert.Equal(BootCleanupOutcome.NotRead, await preparation.CleanUpBootEntriesAsync(partition, argument));
        }
        Assert.Empty(direct.Asked);

        Assert.Equal(BootCleanupOutcome.Removed, await preparation.CleanUpBootEntriesAsync(partition, entry));
        Assert.Equal([(Guid.Parse(partition), Guid.Parse(entry))], direct.Asked);
    }

    private sealed class RecordingDirectInstall : IDirectInstallService
    {
        public List<(Guid Partition, Guid Entry)> Asked { get; } = [];

        public Observation<bool> PreparationAvailability => Observations.Available(true);

        public Task PrepareAsync(int diskNumber, long linuxSizeBytes, string isoPath, string stagingDirectory,
            InstallerBootSpec bootSpec, InstallerTargetRules targetRules, Uri? stage2Url = null,
            IProgress<DirectInstallProgress>? progress = null, CancellationToken ct = default) =>
            throw new InvalidOperationException("Not part of this test.");

        public Task RegisterBootEntryAsync(IProgress<DirectInstallProgress>? progress = null,
            CancellationToken ct = default) => throw new InvalidOperationException("Not part of this test.");

        public Task<BootCleanupOutcome> CleanUpBootEntriesAsync(Guid installerPartitionGuid, Guid bcdEntry,
            CancellationToken ct = default)
        {
            Asked.Add((installerPartitionGuid, bcdEntry));
            return Task.FromResult(BootCleanupOutcome.Removed);
        }
    }
}
