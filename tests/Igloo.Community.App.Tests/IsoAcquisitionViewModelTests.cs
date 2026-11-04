using System.IO;
using Igloo.Cli.Client;
using Igloo.Command.Contracts;
using Igloo.Community.App;
using FluentAssertions;
using Igloo.Community.App.ViewModels;
using Igloo.Core.Abstractions;
using Igloo.Core.Host;
using Igloo.Core.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Community.App.Tests;

public sealed class IsoAcquisitionViewModelTests
{
    private sealed class CancelThenAcquire : IIsoWorkflow
    {
        public int Calls { get; private set; }

        public Task<IsoAcquisitionResult> AcquireAsync(DistroManifest distro,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Calls == 1
                ? Task.FromException<IsoAcquisitionResult>(new OperationCanceledException())
                : Task.FromResult(new IsoAcquisitionResult("C:\\fixture.iso", true, false, 1024,
                    new string('A', 64)));
        }
    }

    private sealed class FailThenAcquire : IIsoWorkflow
    {
        public int Calls { get; private set; }

        public Task<IsoAcquisitionResult> AcquireAsync(DistroManifest distro,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Calls == 1
                ? Task.FromException<IsoAcquisitionResult>(new IOException("mirror checksum mismatch"))
                : Task.FromResult(new IsoAcquisitionResult("C:\\fixture.iso", true, false, 1024,
                    new string('A', 64)));
        }
    }

    private sealed class DelayedAcquire : IIsoWorkflow
    {
        private readonly TaskCompletionSource<IsoAcquisitionResult> _outcome =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private IProgress<IsoAcquisitionProgress>? _progress;

        public Task<IsoAcquisitionResult> AcquireAsync(DistroManifest distro,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default)
        {
            _progress = progress;
            return _outcome.Task;
        }

        public void Report(IsoAcquisitionProgress progress) => _progress!.Report(progress);

        public void Complete() => _outcome.SetResult(new IsoAcquisitionResult(
            "C:\\debian.iso", true, true, 1024, new string('A', 64)));

        public void Fail(Exception error) => _outcome.SetException(error);
    }

    private sealed class ImmediateProgressContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) => callback(state);
    }

    private sealed class TwoDelayedAcquisitions : IIsoWorkflow
    {
        private readonly TaskCompletionSource<IsoAcquisitionResult>[] _outcomes =
        [
            new(TaskCreationOptions.RunContinuationsAsynchronously),
            new(TaskCreationOptions.RunContinuationsAsynchronously),
        ];

        public int Calls { get; private set; }

        public Task<IsoAcquisitionResult> AcquireAsync(DistroManifest distro,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default) =>
            _outcomes[Calls++].Task;

        public void Complete(int index) => _outcomes[index].SetResult(new IsoAcquisitionResult(
            $"C:\\{index}.iso", true, true, 1024, new string('A', 64)));
    }

    private sealed class RecoveryThenAcquire : IIsoWorkflow, IIsoOperationStatusWorkflow
    {
        public Guid OperationId { get; } = Guid.NewGuid();
        public int AcquireCalls { get; private set; }
        public int StatusCalls { get; private set; }
        public bool IsTerminal { get; set; }

        public Task<IsoAcquisitionResult> AcquireAsync(DistroManifest distro,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default)
        {
            AcquireCalls++;
            return AcquireCalls == 1
                ? Task.FromException<IsoAcquisitionResult>(
                    new IsoAcquisitionRecoveryRequiredException(OperationId))
                : Task.FromResult(new IsoAcquisitionResult("C:\\fixture.iso", true, false, 1024,
                    new string('A', 64)));
        }

        public Task<Guid?> FindUnresolvedOperationAsync(string distroId,
            CancellationToken cancellationToken = default)
        {
            distroId.Should().Be("debian");
            StatusCalls++;
            return Task.FromResult<Guid?>(AcquireCalls > 0 && !IsTerminal ? OperationId : null);
        }
    }

    private static DistroManifest Debian() => new()
    {
        Id = "debian", DisplayName = "Debian", Description = "Fixture",
        Iso = new DistroIsoSpec
        {
            DownloadUrl = new Uri("https://example.test/debian.iso"),
            Sha256 = new string('A', 64),
        },
    };

    private static DistroManifest Fedora() => Debian() with
    {
        Id = "fedora-kde",
        DisplayName = "Fedora KDE",
    };

    [Fact]
    public async Task Cancelled_download_shows_a_retry_that_can_complete()
    {
        var acquisition = new CancelThenAcquire();
        var vm = new IsoAcquisitionViewModel(acquisition,
            NullLogger<IsoAcquisitionViewModel>.Instance);
        vm.Prepare(new DistroManifest
        {
            Id = "debian", DisplayName = "Debian", Description = "Fixture",
            Iso = new DistroIsoSpec
            {
                DownloadUrl = new Uri("https://example.test/debian.iso"),
                Sha256 = new string('A', 64),
            },
        });

        await vm.AcquireCommand.ExecuteAsync(null);
        vm.IsRunning.Should().BeFalse();
        vm.IsComplete.Should().BeFalse();
        vm.HasError.Should().BeTrue();
        vm.ErrorTitle.Should().Be("Download failed");
        vm.ErrorMessage.Should().Contain("cancelled");

        await vm.AcquireCommand.ExecuteAsync(null);
        acquisition.Calls.Should().Be(2);
        vm.HasError.Should().BeFalse();
        vm.IsComplete.Should().BeTrue();
        vm.Result.Should().NotBeNull();
    }

    [Fact]
    public async Task Failed_download_uses_existing_retry_without_showing_protected_diagnostics()
    {
        var acquisition = new FailThenAcquire();
        var vm = new IsoAcquisitionViewModel(acquisition,
            NullLogger<IsoAcquisitionViewModel>.Instance);
        vm.Prepare(new DistroManifest
        {
            Id = "debian", DisplayName = "Debian", Description = "Fixture",
            Iso = new DistroIsoSpec
            {
                DownloadUrl = new Uri("https://example.test/debian.iso"),
                Sha256 = new string('A', 64),
            },
        });

        await vm.AcquireCommand.ExecuteAsync(null);

        vm.HasError.Should().BeTrue();
        vm.ErrorMessage.Should().StartWith("Check your connection and available storage")
            .And.NotContain("mirror checksum mismatch");

        await vm.AcquireCommand.ExecuteAsync(null);

        acquisition.Calls.Should().Be(2);
        vm.IsComplete.Should().BeTrue();
        vm.HasError.Should().BeFalse();
    }

    [Theory]
    [InlineData(CommandErrorCode.VerificationFailed, "Retry image verification", "verify the image again")]
    [InlineData(CommandErrorCode.ResourceBusy, "Wait for the other transfer", "other iGloo image transfer")]
    [InlineData(CommandErrorCode.NotSupported, "Choose another distribution", "Use Back")]
    [InlineData(CommandErrorCode.UnsupportedVersion, "Repair local iGloo package", "Repair or reinstall")]
    public async Task Typed_cli_failure_shows_the_relevant_action_without_protected_detail(
        CommandErrorCode code, string title, string action)
    {
        var vm = new IsoAcquisitionViewModel(new CommandFailure(code),
            NullLogger<IsoAcquisitionViewModel>.Instance);
        vm.Prepare(Debian());

        await vm.AcquireCommand.ExecuteAsync(null);

        vm.HasError.Should().BeTrue();
        vm.IsComplete.Should().BeFalse();
        vm.ErrorTitle.Should().Be(title);
        vm.ErrorMessage.Should().Contain(action).And.NotContain("protected fixture detail");
    }

    [Theory]
    [InlineData(CommandErrorCode.InternalFailure, "IsoNetworkUnavailable", "Check your connection", "internet connection")]
    [InlineData(CommandErrorCode.InternalFailure, "IsoCacheUnavailable", "Check the download folder", "download folder")]
    [InlineData(CommandErrorCode.VerificationFailed, "IsoVerificationFailed", "Retry image verification", "verify the image again")]
    [InlineData(CommandErrorCode.VerificationFailed, "IsoSourceMetadataUnavailable", "Update iGloo or choose another distribution", "updated iGloo package")]
    public async Task Sanitized_iso_failure_detail_identifies_a_relevant_recovery_action(
        CommandErrorCode code, string detailCode, string title, string action)
    {
        var vm = new IsoAcquisitionViewModel(new CommandFailure(code, detailCode),
            NullLogger<IsoAcquisitionViewModel>.Instance);
        vm.Prepare(Fedora());

        await vm.AcquireCommand.ExecuteAsync(null);

        vm.HasError.Should().BeTrue();
        vm.ErrorTitle.Should().Be(title);
        vm.ErrorMessage.Should().Contain(action).And.NotContain("protected fixture detail")
            .And.NotContain(detailCode);
    }

    [Fact]
    public async Task Mismatched_detail_cannot_relabel_a_verification_failure_as_network_trouble()
    {
        var vm = new IsoAcquisitionViewModel(new CommandFailure(
                CommandErrorCode.VerificationFailed, "IsoNetworkUnavailable"),
            NullLogger<IsoAcquisitionViewModel>.Instance);
        vm.Prepare(Debian());

        await vm.AcquireCommand.ExecuteAsync(null);

        vm.ErrorTitle.Should().Be("Retry image verification");
        vm.ErrorMessage.Should().NotContain("internet connection");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Earlier_download_cannot_complete_a_new_review(bool sameDistro)
    {
        var acquisition = new DelayedAcquire();
        var vm = new IsoAcquisitionViewModel(acquisition,
            NullLogger<IsoAcquisitionViewModel>.Instance);
        vm.Prepare(Debian());
        var previousContext = SynchronizationContext.Current;
        Task pending;
        try
        {
            SynchronizationContext.SetSynchronizationContext(new ImmediateProgressContext());
            pending = vm.AcquireCommand.ExecuteAsync(null);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previousContext);
        }
        vm.IsRunning.Should().BeTrue();

        vm.Prepare(sameDistro ? Debian() : Fedora());
        acquisition.Report(new IsoAcquisitionProgress(IsoAcquisitionPhase.VerifyingSha256,
            512, 1024, null));
        vm.Phase.Should().Be(IsoAcquisitionPhase.Downloading);
        vm.BytesCompleted.Should().Be(0);
        acquisition.Complete();
        await pending;

        vm.Distro?.Id.Should().Be(sameDistro ? "debian" : "fedora-kde");
        vm.Result.Should().BeNull();
        vm.IsComplete.Should().BeFalse();
        vm.HasError.Should().BeFalse();
    }

    [Fact]
    public async Task Uncertain_earlier_download_blocks_its_own_distro_not_the_new_review()
    {
        var acquisition = new DelayedAcquire();
        var vm = new IsoAcquisitionViewModel(acquisition,
            NullLogger<IsoAcquisitionViewModel>.Instance);
        var operationId = Guid.NewGuid();
        vm.Prepare(Debian());
        var pending = vm.AcquireCommand.ExecuteAsync(null);

        vm.Prepare(Fedora());
        acquisition.Fail(new IsoAcquisitionRecoveryRequiredException(operationId));
        await pending;

        vm.Distro?.Id.Should().Be("fedora-kde");
        vm.HasError.Should().BeFalse();
        vm.RetryBlocked.Should().BeFalse();
        vm.Prepare(Debian());
        vm.RetryBlocked.Should().BeTrue();
        vm.OperationToReview.Should().Be(operationId);
    }

    [Fact]
    public async Task Uncertain_earlier_download_blocks_a_new_review_of_the_same_distro()
    {
        var acquisition = new DelayedAcquire();
        var vm = new IsoAcquisitionViewModel(acquisition,
            NullLogger<IsoAcquisitionViewModel>.Instance);
        var operationId = Guid.NewGuid();
        vm.Prepare(Debian());
        var pending = vm.AcquireCommand.ExecuteAsync(null);

        vm.Prepare(Debian());
        acquisition.Fail(new IsoAcquisitionRecoveryRequiredException(operationId));
        await pending;

        vm.Result.Should().BeNull();
        vm.IsComplete.Should().BeFalse();
        vm.RetryBlocked.Should().BeTrue();
        vm.OperationToReview.Should().Be(operationId);
        vm.IsRunning.Should().BeFalse("the recovery gate must release the busy state");
    }

    [Fact]
    public async Task Older_completion_keeps_the_screen_busy_while_a_new_acquisition_runs()
    {
        var acquisition = new TwoDelayedAcquisitions();
        var vm = new IsoAcquisitionViewModel(acquisition,
            NullLogger<IsoAcquisitionViewModel>.Instance);
        vm.Prepare(Debian());
        var first = vm.AcquireCommand.ExecuteAsync(null);

        vm.Prepare(Fedora());
        var second = vm.AcquireCommand.ExecuteAsync(null);
        acquisition.Calls.Should().Be(2);
        vm.IsRunning.Should().BeTrue();

        acquisition.Complete(0);
        await first;
        vm.IsRunning.Should().BeTrue("the Fedora acquisition is still active");
        vm.Result.Should().BeNull();

        acquisition.Complete(1);
        await second;
        vm.IsRunning.Should().BeFalse();
        vm.IsComplete.Should().BeTrue();
        vm.Result?.LocalPath.Should().Be("C:\\1.iso");
    }

    [Fact]
    public async Task Unconfirmed_download_blocks_retry_until_read_only_status_is_terminal()
    {
        var acquisition = new RecoveryThenAcquire();
        var vm = new IsoAcquisitionViewModel(acquisition,
            NullLogger<IsoAcquisitionViewModel>.Instance);
        vm.Prepare(Debian());

        await vm.AcquireCommand.ExecuteAsync(null);

        vm.HasError.Should().BeTrue();
        vm.CanRetry.Should().BeFalse();
        vm.CanCheckStatus.Should().BeTrue();
        vm.IsRunning.Should().BeFalse("a status-review gate is not an active transfer");
        vm.ErrorTitle.Should().Be("Download failed");
        vm.Prepare(Fedora());
        vm.CanRetry.Should().BeTrue("another distribution uses a different ISO resource");
        vm.Prepare(Debian());
        vm.CanRetry.Should().BeFalse("Back and Next must not discard an unresolved operation");
        vm.CanCheckStatus.Should().BeTrue();
        await vm.AcquireCommand.ExecuteAsync(null);
        acquisition.AcquireCalls.Should().Be(1);

        await vm.CheckStatusCommand.ExecuteAsync(null);
        vm.CanRetry.Should().BeFalse();
        acquisition.StatusCalls.Should().Be(2);

        acquisition.IsTerminal = true;
        await vm.CheckStatusCommand.ExecuteAsync(null);
        vm.CanRetry.Should().BeTrue();
        vm.CanCheckStatus.Should().BeFalse();
        await vm.AcquireCommand.ExecuteAsync(null);

        acquisition.AcquireCalls.Should().Be(2);
        vm.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Recovery_without_a_confirmed_operation_identity_keeps_retry_closed()
    {
        var acquisition = new RecoveryWithoutIdentity();
        var vm = new IsoAcquisitionViewModel(acquisition,
            NullLogger<IsoAcquisitionViewModel>.Instance);
        vm.Prepare(Debian());

        await vm.AcquireCommand.ExecuteAsync(null);

        vm.CanRetry.Should().BeFalse();
        vm.CanCheckStatus.Should().BeFalse();
        vm.ErrorTitle.Should().Be("Review the download log");
        await vm.AcquireCommand.ExecuteAsync(null);
        acquisition.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Transport_loss_without_an_operation_id_never_enables_immediate_retry()
    {
        var acquisition = new UnknownTransportOutcome();
        var vm = new IsoAcquisitionViewModel(acquisition,
            NullLogger<IsoAcquisitionViewModel>.Instance);
        vm.Prepare(Debian());

        await vm.AcquireCommand.ExecuteAsync(null);

        vm.CanRetry.Should().BeFalse();
        vm.CanCheckStatus.Should().BeFalse();
        vm.Prepare(Debian());
        vm.CanRetry.Should().BeFalse();
        await vm.AcquireCommand.ExecuteAsync(null);
        acquisition.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Proven_prelaunch_package_failure_can_retry_after_package_repair()
    {
        var vm = new IsoAcquisitionViewModel(new PrelaunchPackageFailure(),
            NullLogger<IsoAcquisitionViewModel>.Instance);
        vm.Prepare(Debian());

        await vm.AcquireCommand.ExecuteAsync(null);

        vm.CanRetry.Should().BeTrue();
        vm.CanCheckStatus.Should().BeFalse();
        vm.ErrorTitle.Should().Be("Repair local iGloo package");
    }

    [Fact]
    public async Task Fresh_download_screen_recovers_an_unfinished_operation_before_transfer()
    {
        var journal = new JournalLookup { Pending = Guid.NewGuid() };
        var vm = new IsoAcquisitionViewModel(journal,
            NullLogger<IsoAcquisitionViewModel>.Instance);
        vm.Prepare(Debian());

        await vm.AcquireCommand.ExecuteAsync(null);

        journal.AcquireCalls.Should().Be(0);
        vm.CanRetry.Should().BeFalse();
        vm.CanCheckStatus.Should().BeTrue();
        vm.OperationToReview.Should().Be(journal.Pending);

        journal.Pending = null;
        await vm.CheckStatusCommand.ExecuteAsync(null);
        vm.CanRetry.Should().BeTrue();
        await vm.AcquireCommand.ExecuteAsync(null);
        journal.AcquireCalls.Should().Be(1);
        journal.LookupCalls.Should().Be(3);
        vm.IsComplete.Should().BeTrue();
    }

    [Fact]
    public async Task Unavailable_download_history_blocks_transfer_until_read_only_lookup_recovers()
    {
        var journal = new JournalLookup { LookupError = new IOException("journal unavailable") };
        var vm = new IsoAcquisitionViewModel(journal,
            NullLogger<IsoAcquisitionViewModel>.Instance);
        vm.Prepare(Debian());

        await vm.AcquireCommand.ExecuteAsync(null);

        journal.AcquireCalls.Should().Be(0);
        vm.CanRetry.Should().BeFalse();
        vm.CanCheckStatus.Should().BeTrue();
        vm.ErrorTitle.Should().Be("Download failed");
        vm.ErrorMessage.Should().StartWith("Check that the local iGloo CLI can read download history");

        journal.LookupError = null;
        await vm.CheckStatusCommand.ExecuteAsync(null);
        vm.CanRetry.Should().BeTrue();
        await vm.AcquireCommand.ExecuteAsync(null);
        journal.AcquireCalls.Should().Be(1);
    }

    [Theory]
    [InlineData("Accepted", true)]
    [InlineData("Running", true)]
    [InlineData("RecoveryRequired", true)]
    [InlineData("FutureNonterminalState", true)]
    [InlineData("Completed", false)]
    [InlineData("Failed", false)]
    [InlineData("Cancelled", false)]
    public void Journal_lookup_only_releases_terminal_image_operations(string state,
        bool blocks)
    {
        var operationId = Guid.NewGuid();
        var status = new OperationStatus(operationId, "iso.acquire", state,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "", [], null, null);
        var operations = new IsoOperations("debian", [status]);

        CliIsoWorkflow.FindUnresolvedOperation(operations, "debian")
            .Should().Be(blocks ? operationId : null);
    }

    [Fact]
    public void Journal_lookup_rejects_a_mismatched_distribution_or_capability()
    {
        var status = new OperationStatus(Guid.NewGuid(), "iso.acquire", "Completed",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "", [], null, null);
        Action wrongDistro = () => CliIsoWorkflow.FindUnresolvedOperation(
            new IsoOperations("fedora-kde", [status]), "debian");
        Action wrongCapability = () => CliIsoWorkflow.FindUnresolvedOperation(
            new IsoOperations("debian", [status with { CapabilityId = "migration.stage" }]),
            "debian");

        wrongDistro.Should().Throw<InvalidDataException>();
        wrongCapability.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void Journal_lookup_blocks_an_unfinished_artifact_transfer_on_the_same_image_resource()
    {
        var operationId = Guid.NewGuid();
        var status = new OperationStatus(operationId, "artifact.acquire", "Running",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "", [], null, null);

        CliIsoWorkflow.FindUnresolvedOperation(new IsoOperations("debian", [status]),
            "debian").Should().Be(operationId);
    }

    [Fact]
    public async Task Process_start_failure_can_retry_because_no_command_was_sent()
    {
        var vm = new IsoAcquisitionViewModel(new PrelaunchPackageFailure(
                CliTransportError.ProcessStartFailed),
            NullLogger<IsoAcquisitionViewModel>.Instance);
        vm.Prepare(Debian());

        await vm.AcquireCommand.ExecuteAsync(null);

        vm.CanRetry.Should().BeTrue();
        vm.CanCheckStatus.Should().BeFalse();
        vm.ErrorTitle.Should().Be("Check local iGloo CLI");
        vm.ErrorMessage.Should().StartWith("Check that the local iGloo CLI can start");
    }

    private sealed class CommandFailure(CommandErrorCode code, string? detailCode = null) : IIsoWorkflow
    {
        public Task<IsoAcquisitionResult> AcquireAsync(DistroManifest distro,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default) =>
            Task.FromException<IsoAcquisitionResult>(new IsoAcquisitionCommandException(
                code, Guid.NewGuid(), "protected fixture detail", detailCode));
    }

    private sealed class RecoveryWithoutIdentity : IIsoWorkflow
    {
        public int Calls { get; private set; }

        public Task<IsoAcquisitionResult> AcquireAsync(DistroManifest distro,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromException<IsoAcquisitionResult>(
                new IsoAcquisitionRecoveryRequiredException((Guid?)null));
        }
    }

    private sealed class UnknownTransportOutcome : IIsoWorkflow
    {
        public int Calls { get; private set; }

        public Task<IsoAcquisitionResult> AcquireAsync(DistroManifest distro,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromException<IsoAcquisitionResult>(new CliTransportException(
                CliTransportError.ProcessFailed, "Transport ended without a result.",
                Guid.NewGuid(), Guid.NewGuid()));
        }
    }

    private sealed class PrelaunchPackageFailure : IIsoWorkflow
    {
        private readonly CliTransportError _error;

        public PrelaunchPackageFailure(CliTransportError error = CliTransportError.IdentityMismatch) =>
            _error = error;

        public Task<IsoAcquisitionResult> AcquireAsync(DistroManifest distro,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default) =>
            Task.FromException<IsoAcquisitionResult>(new CliTransportException(
                _error, "The local CLI could not start."));
    }

    private sealed class JournalLookup : IIsoWorkflow, IIsoOperationStatusWorkflow
    {
        public Guid? Pending { get; set; }
        public Exception? LookupError { get; set; }
        public int LookupCalls { get; private set; }
        public int AcquireCalls { get; private set; }

        public Task<Guid?> FindUnresolvedOperationAsync(string distroId,
            CancellationToken cancellationToken = default)
        {
            distroId.Should().Be("debian");
            LookupCalls++;
            return LookupError is { } error
                ? Task.FromException<Guid?>(error)
                : Task.FromResult(Pending);
        }

        public Task<IsoAcquisitionResult> AcquireAsync(DistroManifest distro,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default)
        {
            AcquireCalls++;
            return Task.FromResult(new IsoAcquisitionResult("C:\\fixture.iso", true,
                false, 1024, new string('A', 64)));
        }
    }
}
