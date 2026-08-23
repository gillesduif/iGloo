using FluentAssertions;
using Igloo.App.ViewModels;
using Igloo.Core.Abstractions;
using Igloo.Core.Plugins;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.App.Tests;

public class DirectInstallViewModelTests
{
    private sealed class StubInstaller : IDirectInstallService
    {
        public Task PrepareAsync(int diskNumber, long linuxSizeBytes, string isoPath,
            string stagingDirectory, InstallerBootSpec bootSpec, Uri? stage2Url = null,
            IProgress<DirectInstallProgress>? progress = null, CancellationToken ct = default)
            => Task.CompletedTask;

        public Task RegisterBootEntryAsync(
            IProgress<DirectInstallProgress>? progress = null, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static DirectInstallViewModel Create() => new(
        new StubInstaller(),
        new DistroRegistry(NullLogger<DistroRegistry>.Instance),
        NullLogger<DirectInstallViewModel>.Instance);

    [Fact]
    public void Logs_start_expanded_so_a_running_install_is_visible()
    {
        Create().LogsExpanded.Should().BeTrue();
    }

    [Fact]
    public void Finishing_collapses_the_logs()
    {
        var vm = Create();

        vm.IsComplete = true;

        vm.LogsExpanded.Should().BeFalse();
    }

    // On the error panel the log is the answer, so it has to stay where it was.
    [Fact]
    public void Failing_leaves_the_logs_alone()
    {
        var vm = Create();

        vm.HasError = true;

        vm.LogsExpanded.Should().BeTrue();
    }

    [Fact]
    public void The_user_can_reopen_the_logs_after_it_finished()
    {
        var vm = Create();
        vm.IsComplete = true;

        vm.LogsExpanded = true;

        vm.LogsExpanded.Should().BeTrue();
    }
}
