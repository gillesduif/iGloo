using System.IO;
using FluentAssertions;
using Igloo.App.ViewModels;
using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.App.Tests;

public sealed class InstallationTargetWorkflowTests
{
    [Fact]
    public async Task Identity_requiring_plugin_cannot_reach_legacy_disk_preparation()
    {
        var repository = new DirectoryInfo(AppContext.BaseDirectory);
        while (repository is not null && !File.Exists(Path.Join(repository.FullName, "Igloo.sln")))
            repository = repository.Parent;
        repository.Should().NotBeNull();
        var registry = new DistroRegistry(NullLogger<DistroRegistry>.Instance);
        await registry.LoadAsync(Path.Join(repository!.FullName, "distros"));
        registry.Get("deepin").Should().BeAssignableTo<IInstallationTargetConsumer>();
        var installer = new ForbiddenInstaller();
        var vm = new DirectInstallViewModel(installer, registry, NullLogger<DirectInstallViewModel>.Instance);
        vm.Prepare(new IsoAcquisitionResult("unused.iso", true, false, 1),
            new FileStagingResult("unused-staging", 0, 0),
            new DiskInfo(@"\\.\PHYSICALDRIVE7", "synthetic", 128L << 30, 64L << 30, "GPT", []), 32, "deepin");

        await vm.InstallCommand.ExecuteAsync(null);

        installer.Called.Should().BeFalse();
        vm.IsComplete.Should().BeFalse();
        vm.HasError.Should().BeTrue();
        vm.ErrorMessage.Should().Contain("Legacy disk preparation cannot satisfy its identity requirement");
    }

    private sealed class ForbiddenInstaller : IDirectInstallService
    {
        public bool Called { get; private set; }

        public Task PrepareAsync(int diskNumber, long linuxSizeBytes, string isoPath,
            string stagingDirectory, InstallerBootSpec bootSpec, Uri? stage2Url = null,
            IProgress<DirectInstallProgress>? progress = null, CancellationToken ct = default)
        {
            Called = true;
            throw new InvalidOperationException("The test must never reach any storage operation.");
        }

        public Task RegisterBootEntryAsync(IProgress<DirectInstallProgress>? progress = null,
            CancellationToken ct = default) => throw new InvalidOperationException("Boot registration is forbidden.");
    }
}
