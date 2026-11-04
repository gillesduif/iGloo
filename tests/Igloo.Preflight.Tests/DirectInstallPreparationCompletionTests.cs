using Igloo.Core.Recovery;
using Xunit;

namespace Igloo.Preflight.Tests;

public sealed class DirectInstallPreparationCompletionTests
{
    private static CompletedDirectInstallPreparation Target(int disk = 2, uint partition = 4,
        char letter = 'R') => new(disk, partition, letter,
        new(new("fixture-disk", 1, 7, Guid.NewGuid(), 160UL << 30, 512, 4096),
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 100UL << 30, 5UL << 30, "FAT32"));

    [Fact]
    public async Task Partial_work_fields_alone_do_not_admit_boot_registration()
    {
        var completion = new DirectInstallPreparationCompletion();
        var target = Target();
        bool reachedIo = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => completion.RegisterAsync(() => target, _ => reachedIo = true));

        Assert.False(reachedIo);
    }

    [Fact]
    public async Task Late_payload_failure_resets_partial_state_and_blocks_registration_before_io()
    {
        var completion = new DirectInstallPreparationCompletion();
        CompletedDirectInstallPreparation? working = null;
        var target = Target();
        bool reachedIo = false;

        await Assert.ThrowsAsync<IOException>(() => completion.PrepareAsync(() => working = null,
            () => { working = target; throw new IOException("fixture late payload failure"); },
            () => working, CancellationToken.None));

        Assert.Null(working);
        await Assert.ThrowsAsync<InvalidOperationException>(() => completion.RegisterAsync(() => target, _ => reachedIo = true));
        Assert.False(reachedIo);
    }

    [Fact]
    public async Task Precancelled_retry_invalidates_previous_success_without_starting_worker()
    {
        var completion = new DirectInstallPreparationCompletion();
        CompletedDirectInstallPreparation? working = null;
        var target = Target();
        await completion.PrepareAsync(() => working = null, () => working = target,
            () => working, CancellationToken.None);
        int registrations = 0;
        await completion.RegisterAsync(() => working, _ => registrations++);
        bool workerCalled = false;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.PrepareAsync(
            () => working = null, () => workerCalled = true, () => working, new CancellationToken(true)));

        Assert.False(workerCalled);
        Assert.Null(working);
        await Assert.ThrowsAsync<InvalidOperationException>(() => completion.RegisterAsync(() => target, _ => registrations++));
        Assert.Equal(1, registrations);
    }

    [Fact]
    public async Task Cancellation_after_payload_work_does_not_publish_completion()
    {
        var completion = new DirectInstallPreparationCompletion();
        CompletedDirectInstallPreparation? working = null;
        var target = Target();
        using var cancellation = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => completion.PrepareAsync(
            () => working = null, () => { working = target; cancellation.Cancel(); },
            () => working, cancellation.Token));

        Assert.Null(working);
        bool reachedIo = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() => completion.RegisterAsync(() => target,
            _ => reachedIo = true));
        Assert.False(reachedIo);
    }

    [Fact]
    public async Task Successful_reuse_publishes_only_the_current_completed_target()
    {
        var completion = new DirectInstallPreparationCompletion();
        CompletedDirectInstallPreparation? working = null;
        var reused = Target();
        var registered = new List<CompletedDirectInstallPreparation>();
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await completion.PrepareAsync(() => working = null, () => working = reused,
                () => working, CancellationToken.None);
            await completion.RegisterAsync(() => working, registered.Add);
        }

        Assert.Equal(new[] { reused, reused }, registered);
    }

    [Fact]
    public async Task Changed_completed_snapshot_is_rejected_and_cannot_be_replayed()
    {
        var completion = new DirectInstallPreparationCompletion();
        var target = Target();
        CompletedDirectInstallPreparation? working = null;
        await completion.PrepareAsync(() => working = null, () => working = target,
            () => working, CancellationToken.None);
        working = target with { DriveLetter = 'S' };
        bool reachedIo = false;

        await Assert.ThrowsAsync<InvalidOperationException>(() => completion.RegisterAsync(() => working, _ => reachedIo = true));
        working = target;
        await Assert.ThrowsAsync<InvalidOperationException>(() => completion.RegisterAsync(() => working, _ => reachedIo = true));
        Assert.False(reachedIo);
    }

    [Fact]
    public async Task Concurrent_preparation_cannot_replace_or_complete_the_active_attempt()
    {
        var completion = new DirectInstallPreparationCompletion();
        var target = Target();
        CompletedDirectInstallPreparation? working = null;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool secondWorker = false;
        var active = completion.PrepareAsync(() => working = null, () =>
        {
            working = target;
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        }, () => working, CancellationToken.None);
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => completion.PrepareAsync(
                () => working = null, () => secondWorker = true, () => Target(3), CancellationToken.None));
            bool reachedIo = false;
            await Assert.ThrowsAsync<InvalidOperationException>(() => completion.RegisterAsync(() => working,
                _ => reachedIo = true));
            Assert.False(reachedIo);
        }
        finally { release.Set(); }
        await active;

        Assert.False(secondWorker);
        await completion.RegisterAsync(() => working, actual => Assert.Equal(target, actual));
    }

    [Fact]
    public async Task Registration_keeps_preparation_state_stable_until_it_returns()
    {
        var completion = new DirectInstallPreparationCompletion();
        var target = Target();
        CompletedDirectInstallPreparation? working = null;
        await completion.PrepareAsync(() => working = null, () => working = target,
            () => working, CancellationToken.None);
        Task? conflicting = null;

        await completion.RegisterAsync(() => working, _ => conflicting = completion.PrepareAsync(
            () => working = null, () => working = Target(3), () => working, CancellationToken.None));

        await Assert.ThrowsAsync<InvalidOperationException>(() => conflicting!);
        Assert.Equal(target, working);
    }

    [Fact]
    public async Task Registration_requested_during_preparation_stays_rejected_after_worker_finishes()
    {
        var completion = new DirectInstallPreparationCompletion();
        var target = Target();
        CompletedDirectInstallPreparation? working = null;
        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        bool reachedIo = false;
        var active = completion.PrepareAsync(() => working = null, () =>
        {
            working = target;
            started.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        }, () => working, CancellationToken.None);
        Task registration;
        try
        {
            Assert.True(started.Wait(TimeSpan.FromSeconds(10)));
            // Deliberately observe this result only after preparation completes:
            // worker scheduling must not turn an invalid request into authority.
            registration = completion.RegisterAsync(() => working, _ => reachedIo = true);
        }
        finally { release.Set(); }
        await active;

        await Assert.ThrowsAsync<InvalidOperationException>(() => registration);
        Assert.False(reachedIo);
        await completion.RegisterAsync(() => working, actual => Assert.Equal(target, actual));
    }
}
