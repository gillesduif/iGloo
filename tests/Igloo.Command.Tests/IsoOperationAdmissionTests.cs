using System.Text.Json;
using System.Net;
using Igloo.Command.Contracts;
using Igloo.Core.Abstractions;
using Igloo.Core.Models;
using Igloo.Core.Plugins;
using Igloo.Core.Runtime;
using Igloo.Core.Runtime.Execution;
using Igloo.Iso;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Igloo.Command.Tests;

// Admission and execution use the real cross-process ISO lease names even when
// acquisition itself is a fixture; do not overlap unrelated lease holders.
[Collection("RecoveryState")]
public sealed class IsoOperationAdmissionTests
{
    [Fact]
    public async Task CancellationAtFirstProgressHasDurableCancelledStatus()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-iso-operation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new FileOperationJournal(directory);
            var acquisition = new RecordingAcquisition();
            var dispatcher = Dispatcher(journal, acquisition);
            using var cancellation = new CancellationTokenSource();
            var progress = new InlineProgress(update =>
            {
                if (update.Phase == "validating") cancellation.Cancel();
            });

            var result = await dispatcher.ExecuteAsync(Request(), progress, cancellation.Token);

            Assert.False(result.Succeeded);
            Assert.Equal(CommandErrorCode.Cancelled, result.Error!.Code);
            Assert.NotNull(result.OperationId);
            Assert.Equal(result.OperationId, Assert.Single(progress.Events).OperationId);
            Assert.Equal(0, acquisition.Calls);
            var reopened = new FileOperationJournal(directory);
            Assert.Equal(LocalOperationState.Cancelled,
                (await reopened.ReadAsync(result.OperationId.Value))!.State);
            var status = await new OperationStatusHandler(reopened, false).ExecuteAsync(
                new CommandRequest(1, "operation.status", 1, Guid.NewGuid(), Guid.NewGuid(),
                    JsonSerializer.SerializeToElement(new OperationSelection(result.OperationId.Value),
                        CommandProtocol.JsonOptions)), null, CancellationToken.None);
            Assert.Equal("Cancelled", Assert.IsType<OperationStatus>(status).State);
        }
        finally
        {
            DeleteFixture(directory);
        }
    }

    [Fact]
    public async Task FailedJournalAdmissionIssuesNoOperationIdentity()
    {
        var acquisition = new RecordingAcquisition();
        var dispatcher = Dispatcher(new FailingJournal(failAdmission: true), acquisition);
        var progress = new InlineProgress(_ => { });

        var result = await dispatcher.ExecuteAsync(Request() with { OperationId = Guid.NewGuid() }, progress);

        Assert.False(result.Succeeded);
        Assert.Equal(CommandErrorCode.InternalFailure, result.Error!.Code);
        Assert.Null(result.OperationId);
        Assert.Empty(progress.Events);
        Assert.Equal(0, acquisition.Calls);
    }

    [Fact]
    public async Task UnknownDistributionIsRejectedBeforeOperationAdmission()
    {
        var acquisition = new RecordingAcquisition();
        var journal = new FailingJournal(failAdmission: true);
        var dispatcher = Dispatcher(journal, acquisition);
        var progress = new InlineProgress(_ => { });
        var request = Request() with
        {
            Input = JsonSerializer.SerializeToElement(new DistroSelection("unknown-provider"),
                CommandProtocol.JsonOptions)
        };

        var result = await dispatcher.ExecuteAsync(request, progress);

        Assert.False(result.Succeeded);
        Assert.Equal(CommandErrorCode.NotSupported, result.Error!.Code);
        Assert.Null(result.OperationId);
        Assert.Empty(progress.Events);
        Assert.Equal(0, acquisition.Calls);
    }

    [Fact]
    public async Task MissingJournalCannotIssueAnIsoOperation()
    {
        var loader = new DistroLoader(NullLogger<DistroLoader>.Instance);
        loader.Load(Path.Join(RepositoryRoot(), "distros"));
        var acquisition = new RecordingAcquisition();
        var dispatcher = new CommandDispatcher(new UnusedPreflight(), loader,
            new DistroRegistry(NullLogger<DistroRegistry>.Instance),
            [new IsoAcquireHandler(loader, new IsoAcquisitionCoordinator(acquisition))]);
        var progress = new InlineProgress(_ => { });

        var result = await dispatcher.ExecuteAsync(Request(), progress);

        Assert.Equal(CommandErrorCode.PreconditionFailed, result.Error!.Code);
        Assert.Null(result.OperationId);
        Assert.Empty(progress.Events);
        Assert.Equal(0, acquisition.Calls);
    }

    [Fact]
    public async Task FailedTerminalJournalWriteReportsUnknownOutcome()
    {
        var acquisition = new RecordingAcquisition();
        var dispatcher = Dispatcher(new FailingJournal(failTerminal: true), acquisition);

        var result = await dispatcher.ExecuteAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Equal(CommandErrorCode.RecoveryRequired, result.Error!.Code);
        Assert.StartsWith("Inspect operation status before retrying", result.Error.Message,
            StringComparison.Ordinal);
        Assert.NotNull(result.OperationId);
        Assert.Equal(1, acquisition.Calls);
    }

    [Theory]
    [InlineData(true, CommandErrorCode.InternalFailure, "IsoNetworkUnavailable")]
    [InlineData(false, CommandErrorCode.VerificationFailed, "IsoVerificationFailed")]
    public async Task SignedMetadataFailureHasSanitizedResultAndDurableCause(
        bool transportFailure, CommandErrorCode expectedCode, string expectedDetail)
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-iso-operation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new FileOperationJournal(directory);
            using var handler = new MetadataResponseHandler(transportFailure);
            var acquisition = new IsoAcquisitionService(new MetadataClientFactory(handler),
                NullLogger<IsoAcquisitionService>.Instance);
            var result = await Dispatcher(journal, acquisition).ExecuteAsync(Request());

            Assert.False(result.Succeeded);
            Assert.Equal(expectedCode, result.Error!.Code);
            Assert.Equal(expectedDetail, result.Error.DetailCode);
            Assert.NotNull(result.OperationId);
            Assert.Equal(LocalOperationState.Failed,
                (await new FileOperationJournal(directory).ReadAsync(result.OperationId.Value))!.State);
            Assert.Equal(expectedDetail,
                (await new FileOperationJournal(directory).ReadAsync(result.OperationId.Value))!.FailureCode);
            Assert.Equal(transportFailure ? 1 : 2, handler.Calls);
            Assert.DoesNotContain("private-source", JsonSerializer.Serialize(result,
                CommandProtocol.JsonOptions), StringComparison.Ordinal);
            Assert.DoesNotContain("download.fedoraproject.org", JsonSerializer.Serialize(result,
                CommandProtocol.JsonOptions), StringComparison.Ordinal);
        }
        finally
        {
            DeleteFixture(directory);
        }
    }

    [Fact]
    public async Task CacheFailureKeepsOperationIdentityWithoutPublishingItsPath()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-iso-operation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new FileOperationJournal(directory);
            var result = await Dispatcher(journal, new FailingAcquisition(
                new IOException("private-source-cache-path"))).ExecuteAsync(Request());

            Assert.Equal(CommandErrorCode.InternalFailure, result.Error!.Code);
            Assert.Equal("IsoCacheUnavailable", result.Error.DetailCode);
            Assert.Equal("IsoCacheUnavailable", (await journal.ReadAsync(result.OperationId!.Value))!.FailureCode);
            Assert.DoesNotContain("private-source-cache-path", JsonSerializer.Serialize(result,
                CommandProtocol.JsonOptions), StringComparison.Ordinal);
        }
        finally
        {
            DeleteFixture(directory);
        }
    }

    [Fact]
    public async Task TransportTimeoutWithoutCallerCancellationIsNotRecordedAsCancelled()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-iso-operation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new FileOperationJournal(directory);
            var result = await Dispatcher(journal, new FailingAcquisition(
                new TaskCanceledException("private-source-timeout"))).ExecuteAsync(Request());

            Assert.Equal(CommandErrorCode.InternalFailure, result.Error!.Code);
            Assert.Equal("IsoNetworkUnavailable", result.Error.DetailCode);
            var record = await journal.ReadAsync(result.OperationId!.Value);
            Assert.Equal(LocalOperationState.Failed, record!.State);
            Assert.Equal("IsoNetworkUnavailable", record.FailureCode);
        }
        finally
        {
            DeleteFixture(directory);
        }
    }

    [Theory]
    [InlineData("debian", 1)]
    [InlineData("debian", 2)]
    [InlineData("linuxmint-cinnamon", 1)]
    [InlineData("linuxmint-cinnamon", 2)]
    [InlineData("fedora-kde", 1)]
    [InlineData("fedora-kde", 2)]
    public async Task DeclaredSignatureCannotBeBypassedByAnUnverifiedAcquisitionResult(
        string distroId, int version)
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-iso-operation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new FileOperationJournal(directory);
            var request = Request() with
            {
                CapabilityVersion = version,
                Input = JsonSerializer.SerializeToElement(new DistroSelection(distroId),
                    CommandProtocol.JsonOptions),
            };
            var result = await Dispatcher(journal, new UnverifiedAcquisition(), version)
                .ExecuteAsync(request);

            Assert.False(result.Succeeded);
            Assert.Equal(CommandErrorCode.VerificationFailed, result.Error!.Code);
            Assert.Equal("IsoVerificationFailed", result.Error.DetailCode);
            Assert.NotNull(result.OperationId);
            var record = await journal.ReadAsync(result.OperationId.Value);
            Assert.Equal(LocalOperationState.Failed, record!.State);
            Assert.Equal("IsoVerificationFailed", record.FailureCode);
            Assert.DoesNotContain("GpgVerified", record.EvidenceCodes);
        }
        finally
        {
            DeleteFixture(directory);
        }
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    public async Task MissingSha256ProofCannotCompleteIsoOrArtifactAcquisition(
        bool artifactMode, int version)
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-iso-operation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new FileOperationJournal(directory);
            var registry = new FileArtifactRegistry(Path.Join(directory, "artifacts"),
                Path.Join(directory, "cache"));
            var request = Request() with
            {
                CapabilityId = artifactMode ? "artifact.acquire" : "iso.acquire",
                CapabilityVersion = version,
            };
            var result = await Dispatcher(journal, new MissingShaVerificationAcquisition(),
                version, artifactMode, registry).ExecuteAsync(request);

            Assert.False(result.Succeeded);
            Assert.Equal(CommandErrorCode.VerificationFailed, result.Error!.Code);
            Assert.Equal("IsoVerificationFailed", result.Error.DetailCode);
            var record = await journal.ReadAsync(result.OperationId!.Value);
            Assert.Equal(LocalOperationState.Failed, record!.State);
            Assert.Equal("IsoVerificationFailed", record.FailureCode);
            Assert.Empty(record.EvidenceCodes);
            Assert.False(Directory.Exists(Path.Join(directory, "artifacts")));
        }
        finally
        {
            DeleteFixture(directory);
        }
    }

    [Fact]
    public async Task ReopenedJournalFindsAnUnfinishedIsoOperationForTheSelectedDistro()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-iso-operation-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new FileOperationJournal(directory);
            var request = Request() with { OperationId = Guid.NewGuid() };
            await journal.CreateAsync(request, IsoAcquireHandler.TargetIdentityHash("fedora-kde"));
            await journal.TransitionAsync(request.OperationId!.Value, LocalOperationState.Running,
                "iso.acquiring");
            var reopened = new FileOperationJournal(directory);
            var loader = new DistroLoader(NullLogger<DistroLoader>.Instance);
            loader.Load(Path.Join(RepositoryRoot(), "distros"));
            var handler = new IsoOperationsHandler(loader, reopened);

            async Task<IsoOperations> LookUp(string distroId) => Assert.IsType<IsoOperations>(
                await handler.ExecuteAsync(Request() with
                {
                    CapabilityId = "iso.operations",
                    Input = JsonSerializer.SerializeToElement(new DistroSelection(distroId),
                        CommandProtocol.JsonOptions),
                }, null, CancellationToken.None));

            var recovered = await LookUp("fedora-kde");
            Assert.Equal("fedora-kde", recovered.DistroId);
            var operation = Assert.Single(recovered.Operations);
            Assert.Equal(request.OperationId, operation.OperationId);
            Assert.Equal("Running", operation.State);
            Assert.Empty((await LookUp("debian")).Operations);
            await Assert.ThrowsAsync<KeyNotFoundException>(() => LookUp("missing-provider"));

            var acquisition = new RecordingAcquisition();
            var blocked = await Dispatcher(reopened, acquisition).ExecuteAsync(Request() with
            {
                OperationId = Guid.NewGuid(),
            });
            Assert.False(blocked.Succeeded);
            Assert.Equal(CommandErrorCode.RecoveryRequired, blocked.Error!.Code);
            Assert.Null(blocked.OperationId);
            Assert.Equal(0, acquisition.Calls);
            Assert.Single((await LookUp("fedora-kde")).Operations);

            await reopened.TransitionAsync(request.OperationId.Value,
                LocalOperationState.Cancelled, "iso.cancelled", failureCode: "Cancelled");
            Assert.Empty((await LookUp("fedora-kde")).Operations);
            var resumed = await Dispatcher(reopened, acquisition).ExecuteAsync(Request());
            Assert.True(resumed.Succeeded);
            Assert.Equal(1, acquisition.Calls);

            var artifactRequest = Request() with
            {
                CapabilityId = "artifact.acquire", OperationId = Guid.NewGuid(),
            };
            await reopened.CreateAsync(artifactRequest,
                IsoAcquireHandler.TargetIdentityHash("fedora-kde"));
            var artifact = Assert.Single((await LookUp("fedora-kde")).Operations);
            Assert.Equal("artifact.acquire", artifact.CapabilityId);
            var overlapping = await Dispatcher(reopened, acquisition).ExecuteAsync(Request());
            Assert.Equal(CommandErrorCode.RecoveryRequired, overlapping.Error!.Code);
            Assert.Equal(1, acquisition.Calls);
        }
        finally
        {
            DeleteFixture(directory);
        }
    }

    [Fact]
    public async Task CorruptJournalPreventsIsoAdmission()
    {
        var directory = Path.Join(Path.GetTempPath(), "igloo-iso-operation-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(Path.Join(directory,
                Guid.NewGuid().ToString("N") + ".json"), "{invalid");
            var acquisition = new RecordingAcquisition();
            var result = await Dispatcher(new FileOperationJournal(directory), acquisition)
                .ExecuteAsync(Request() with { OperationId = Guid.NewGuid() });
            Assert.False(result.Succeeded);
            Assert.Equal(CommandErrorCode.RecoveryRequired, result.Error!.Code);
            Assert.Null(result.OperationId);
            Assert.Equal(0, acquisition.Calls);
        }
        finally
        {
            DeleteFixture(directory);
        }
    }

    private static CommandDispatcher Dispatcher(IOperationJournal journal, IIsoAcquisitionService acquisition,
        int version = 1, bool artifactMode = false, FileArtifactRegistry? artifacts = null)
    {
        var loader = new DistroLoader(NullLogger<DistroLoader>.Instance);
        loader.Load(Path.Join(RepositoryRoot(), "distros"));
        return new CommandDispatcher(new UnusedPreflight(), loader,
            new DistroRegistry(NullLogger<DistroRegistry>.Instance),
            [new IsoAcquireHandler(loader, new IsoAcquisitionCoordinator(acquisition), journal,
                artifacts, artifactMode, version)]);
    }

    private static CommandRequest Request() => new(1, "iso.acquire", 1, Guid.NewGuid(), Guid.NewGuid(),
        JsonSerializer.SerializeToElement(new DistroSelection("fedora-kde"), CommandProtocol.JsonOptions));

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Join(directory.FullName, "Igloo.sln")))
            directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException();
    }

    private static void DeleteFixture(string directory)
    {
        var full = Path.GetFullPath(directory);
        Assert.Equal(Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())),
            Path.GetDirectoryName(full), ignoreCase: OperatingSystem.IsWindows());
        Assert.StartsWith("igloo-iso-operation-", Path.GetFileName(full), StringComparison.Ordinal);
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }

    private sealed class InlineProgress(Action<CommandProgress> onReport) : IProgress<CommandProgress>
    {
        public List<CommandProgress> Events { get; } = [];
        public void Report(CommandProgress value)
        {
            Events.Add(value);
            onReport(value);
        }
    }

    private sealed class RecordingAcquisition : IIsoAcquisitionService
    {
        public int Calls { get; private set; }
        public Task<IsoAcquisitionResult> AcquireAsync(IsoSpecification specification,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new IsoAcquisitionResult("fixture.iso", true, true, 123,
                new string('A', 64)));
        }
    }

    private sealed class FailingAcquisition(Exception failure) : IIsoAcquisitionService
    {
        public Task<IsoAcquisitionResult> AcquireAsync(IsoSpecification specification,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default) =>
            Task.FromException<IsoAcquisitionResult>(failure);
    }

    private sealed class UnverifiedAcquisition : IIsoAcquisitionService
    {
        public Task<IsoAcquisitionResult> AcquireAsync(IsoSpecification specification,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IsoAcquisitionResult(
                Path.Join(Path.GetTempPath(), "fixture-iso-unverified.iso"), true, false,
                123, new string('A', 64)));
    }

    private sealed class MissingShaVerificationAcquisition : IIsoAcquisitionService
    {
        public Task<IsoAcquisitionResult> AcquireAsync(IsoSpecification specification,
            IProgress<IsoAcquisitionProgress>? progress, CancellationToken cancellationToken = default) =>
            Task.FromResult(new IsoAcquisitionResult(
                Path.Join(Path.GetTempPath(), "fixture-iso-unverified.iso"), false, true,
                123, new string('A', 64)));
    }

    private sealed class MetadataClientFactory(MetadataResponseHandler handler) : IHttpClientFactory
    {
#pragma warning disable CA2000 // HttpClient owns the test handler.
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
#pragma warning restore CA2000
    }

    private sealed class MetadataResponseHandler(bool transportFailure) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (transportFailure)
                return Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("private-source-network-diagnostics"));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("invalid signed checksum"),
            });
        }
    }

    private sealed class FailingJournal(bool failAdmission = false, bool failTerminal = false)
        : IOperationJournal
    {
        public Task<LocalOperationRecord?> ReadAsync(Guid operationId,
            CancellationToken cancellationToken = default) => Task.FromResult<LocalOperationRecord?>(null);

        public Task<IReadOnlyList<LocalOperationRecord>> FindAsync(string capabilityId,
            string targetIdentityHash, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<LocalOperationRecord>>([]);

        public Task CreateAsync(CommandRequest request, string targetIdentityHash,
            CancellationToken cancellationToken = default) => failAdmission
                ? Task.FromException(new IOException("fixture journal admission failure"))
                : Task.CompletedTask;

        public Task TransitionAsync(Guid operationId, LocalOperationState state, string stage,
            IReadOnlyList<string>? evidenceCodes = null, string? failureCode = null,
            string? continuationCode = null, CancellationToken cancellationToken = default) =>
            failTerminal && state is LocalOperationState.Completed or LocalOperationState.Failed or
                LocalOperationState.Cancelled
                ? Task.FromException(new IOException("fixture journal terminal failure"))
                : Task.CompletedTask;
    }

    private sealed class UnusedPreflight : IPreflightChecker
    {
        public Task<PreflightReport> RunAsync(CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("ISO acquisition must not inspect the machine.");
    }
}
