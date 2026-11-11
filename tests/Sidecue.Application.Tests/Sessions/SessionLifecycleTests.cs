using System.Collections.Concurrent;
using Sidecue.Application.Abstractions;
using Sidecue.Application.Sessions;
using Sidecue.Core.Contracts;
using Xunit;

namespace Sidecue.Application.Tests.Sessions;

public sealed class SessionLifecycleTests
{
    [Fact]
    public async Task PauseDropsUnsentAudioWithoutCancellingApprovedGenerationAndHideOnlyHides()
    {
        await using var fixture = new Fixture();
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        using var permit = await fixture.Controller.BeginGenerationAsync(NewRequest(), TestContext.Current.CancellationToken);
        fixture.Effects.QueueUnsent(7);
        fixture.Clock.AdvanceTo(130_000);

        await fixture.Controller.PauseAsync(TestContext.Current.CancellationToken);
        await fixture.Controller.PauseAsync(TestContext.Current.CancellationToken);
        await fixture.Controller.HideAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionLifecycleState.Paused, fixture.Controller.Snapshot.State);
        Assert.Equal(SessionCaptureState.Paused, fixture.Controller.Snapshot.Capture);
        Assert.False(fixture.Controller.Snapshot.OverlayVisible);
        Assert.False(fixture.Effects.Acquiring);
        Assert.False(fixture.Effects.Uploading);
        Assert.Equal(0, fixture.Effects.UnsentFrames);
        Assert.Equal(7, fixture.Effects.DroppedFrames);
        Assert.Equal(130_000, fixture.Effects.PauseCutoff?.Offset.Value);
        Assert.Equal(1, fixture.Effects.PauseCalls);
        Assert.Equal(1, fixture.Effects.PersistCalls);
        Assert.Equal(1, fixture.Effects.OpenCalls);
        Assert.False(permit.CancellationToken.IsCancellationRequested);
        Assert.True(fixture.Controller.IsCurrent(permit.Fence));

        await fixture.Controller.ResumeAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SessionLifecycleState.Listening, fixture.Controller.Snapshot.State);
        Assert.Equal(2, fixture.Effects.OpenCalls);
        Assert.True(fixture.Controller.IsCurrent(permit.Fence));
    }

    [Fact]
    public async Task PauseDuringUncancellableOpenNeverActivatesLateLease()
    {
        await using var fixture = new Fixture();
        var pending = fixture.Effects.BlockNextOpen();
        Task starting = fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        await pending.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        fixture.Clock.AdvanceTo(100_000);
        Task pause = fixture.Controller.PauseAsync(TestContext.Current.CancellationToken);
        var lateLease = fixture.Effects.NewLease();
        try
        {
            await pause.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            fixture.Clock.AdvanceTo(900_000);
        }
        finally
        {
            pending.Result.TrySetResult(lateLease);
            await Task.WhenAll(starting, pause).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        await lateLease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(SessionLifecycleState.Paused, fixture.Controller.Snapshot.State);
        Assert.Equal(0, lateLease.Activations);
        Assert.NotEmpty(lateLease.Stops);
        Assert.Equal(100_000, fixture.Effects.PauseCutoff?.Offset.Value);
        Assert.False(fixture.Effects.Acquiring);
        Assert.False(fixture.Effects.Uploading);
    }

    [Fact]
    public async Task PauseFencesNewAudioBeforeBlockedCaptureStopCompletes()
    {
        await using var fixture = new Fixture();
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        fixture.Effects.QueueUnsent(3);
        fixture.Effects.BlockStop = true;
        fixture.Clock.AdvanceTo(100_000);

        Task pause = fixture.Controller.PauseAsync(TestContext.Current.CancellationToken);
        Task? repeatedPause = null;
        try
        {
            await fixture.Effects.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            fixture.Clock.AdvanceTo(200_000);
            repeatedPause = fixture.Controller.PauseAsync(TestContext.Current.CancellationToken);
            Assert.False(fixture.Effects.Acquiring);
            Assert.False(fixture.Effects.Uploading);
            Assert.Equal(0, fixture.Effects.UnsentFrames);
            Assert.Equal(3, fixture.Effects.DroppedFrames);
            Assert.False(pause.IsCompleted);
            Assert.False(repeatedPause.IsCompleted);
            var fence = Assert.Single(fixture.Effects.Fences);
            Assert.Equal(CaptureStopReason.Pause, fence.Reason);
            Assert.Equal(100_000, fence.Cutoff.Offset.Value);
        }
        finally
        {
            fixture.Effects.StopRelease.TrySetResult();
            await Task.WhenAll(pause, repeatedPause ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        Assert.Equal(0, fixture.Effects.UnsentFrames);
        Assert.Equal(3, fixture.Effects.DroppedFrames);
    }

    [Fact]
    public async Task EndDuringUncancellableReconnectFencesAndDisposesLateLeaseOnlyFinalizingOnce()
    {
        await using var fixture = new Fixture();
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        await fixture.Controller.PauseAsync(TestContext.Current.CancellationToken);
        var pending = fixture.Effects.BlockNextOpen();
        Task resuming = fixture.Controller.ResumeAsync(TestContext.Current.CancellationToken);
        await pending.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Task ending = fixture.Controller.EndAsync(TestContext.Current.CancellationToken);
        var lateLease = fixture.Effects.NewLease();
        try
        {
            await ending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        finally
        {
            pending.Result.TrySetResult(lateLease);
            await Task.WhenAll(resuming, ending).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        await lateLease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await fixture.Controller.EndAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionLifecycleState.Completed, fixture.Controller.Snapshot.State);
        Assert.Equal(0, lateLease.Activations);
        Assert.NotEmpty(lateLease.Stops);
        Assert.Equal(1, fixture.Effects.FinalizeCalls);
        Assert.False(fixture.Effects.Acquiring);
        Assert.False(fixture.Effects.Uploading);
    }

    [Fact]
    public async Task EndStopsAnActiveLeaseWhileItsDeviceReconnectIsPending()
    {
        await using var fixture = new Fixture();
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        var lease = Assert.IsType<FakeEffects.FakeCaptureLease>(fixture.Effects.LatestLease);
        lease.BeginDeviceReconnect();

        await fixture.Controller.EndAsync(TestContext.Current.CancellationToken);
        lease.FinishDeviceReconnect();

        Assert.Contains(CaptureStopReason.End, lease.Stops);
        Assert.Equal(1, lease.Activations);
        Assert.False(fixture.Effects.Acquiring);
        Assert.False(fixture.Effects.Uploading);
        Assert.Equal(SessionLifecycleState.Completed, fixture.Controller.Snapshot.State);
    }

    [Fact]
    public async Task ClearCancelsAndFencesOldAnswerAtNewBoundaryWithoutDeletingHistory()
    {
        await using var fixture = new Fixture();
        fixture.Effects.StoredHistory.Add("synthetic committed history");
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        using var old = await fixture.Controller.BeginGenerationAsync(NewRequest(), TestContext.Current.CancellationToken);
        fixture.Clock.AdvanceTo(200_000);

        await fixture.Controller.ClearContextAsync(TestContext.Current.CancellationToken);

        Assert.True(old.CancellationToken.IsCancellationRequested);
        Assert.False(fixture.Controller.IsCurrent(old.Fence));
        Assert.False(fixture.Controller.CompleteGeneration(old.Fence));
        Assert.Equal(1, fixture.Controller.Snapshot.ContextEpoch.Value);
        Assert.Equal(200_000, fixture.Controller.Snapshot.ContextStart.Offset.Value);
        Assert.Equal(1, fixture.Effects.ClearCalls);
        Assert.Equal("synthetic committed history", Assert.Single(fixture.Effects.StoredHistory));
        using var next = await fixture.Controller.BeginGenerationAsync(NewRequest(), TestContext.Current.CancellationToken);
        Assert.Equal(1, next.Fence.ContextEpoch.Value);
        Assert.True(fixture.Controller.IsCurrent(next.Fence));
    }

    [Fact]
    public async Task ClearAndHideDoNotWaitForBlockedResumeOpen()
    {
        await using var fixture = new Fixture();
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        await fixture.Controller.PauseAsync(TestContext.Current.CancellationToken);
        using var old = await fixture.Controller.BeginGenerationAsync(NewRequest(), TestContext.Current.CancellationToken);
        var pending = fixture.Effects.BlockNextOpen();
        Task resuming = fixture.Controller.ResumeAsync(TestContext.Current.CancellationToken);
        await pending.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        fixture.Clock.AdvanceTo(200_000);

        Task clear = fixture.Controller.ClearContextAsync(TestContext.Current.CancellationToken);
        Task hide = fixture.Controller.HideAsync(TestContext.Current.CancellationToken);
        try
        {
            await Task.WhenAll(clear, hide).WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.True(old.CancellationToken.IsCancellationRequested);
            Assert.False(fixture.Controller.IsCurrent(old.Fence));
            Assert.Equal(1, fixture.Controller.Snapshot.ContextEpoch.Value);
            Assert.Equal(200_000, fixture.Controller.Snapshot.ContextStart.Offset.Value);
            Assert.False(fixture.Controller.Snapshot.OverlayVisible);
        }
        finally
        {
            pending.Result.TrySetResult(fixture.Effects.NewLease());
            await resuming.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await Task.WhenAll(clear, hide).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task ConcurrentClearsEachAdvanceTheEpochAndPreserveHistory()
    {
        await using var fixture = new Fixture();
        fixture.Effects.StoredHistory.Add("synthetic committed history");
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        using var old = await fixture.Controller.BeginGenerationAsync(NewRequest(), TestContext.Current.CancellationToken);

        await Task.WhenAll(fixture.Controller.ClearContextAsync(TestContext.Current.CancellationToken), fixture.Controller.ClearContextAsync(TestContext.Current.CancellationToken));

        Assert.True(old.CancellationToken.IsCancellationRequested);
        Assert.False(fixture.Controller.IsCurrent(old.Fence));
        Assert.Equal(2, fixture.Controller.Snapshot.ContextEpoch.Value);
        Assert.Equal([1L, 2L], fixture.Effects.ClearEpochs);
        Assert.Single(fixture.Effects.StoredHistory);
    }

    [Fact]
    public async Task EndRejectsPendingGenerationWithoutWaitingForBlockedClearEffect()
    {
        await using var fixture = new Fixture();
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        fixture.Effects.BlockClear = true;
        Task clearing = fixture.Controller.ClearContextAsync(TestContext.Current.CancellationToken);
        await fixture.Effects.ClearEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Task<GenerationPermit> begin = fixture.Controller.BeginGenerationAsync(NewRequest(), TestContext.Current.CancellationToken);
        Task ending = fixture.Controller.EndAsync(TestContext.Current.CancellationToken);

        try
        {
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => begin.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
        }
        finally
        {
            fixture.Effects.ClearRelease.TrySetResult();
            await Task.WhenAll(clearing, ending).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        Assert.Equal(SessionLifecycleState.Completed, fixture.Controller.Snapshot.State);
    }

    [Fact]
    public async Task CompletedOrSupersededRequestIdentityCannotBeReused()
    {
        await using var fixture = new Fixture();
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        RequestId firstId = NewRequest();
        using var first = await fixture.Controller.BeginGenerationAsync(firstId, TestContext.Current.CancellationToken);
        Assert.True(fixture.Controller.CompleteGeneration(first.Fence));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Controller.BeginGenerationAsync(firstId, TestContext.Current.CancellationToken));

        RequestId secondId = NewRequest();
        using var second = await fixture.Controller.BeginGenerationAsync(secondId, TestContext.Current.CancellationToken);
        using var third = await fixture.Controller.BeginGenerationAsync(NewRequest(), TestContext.Current.CancellationToken);
        Assert.False(fixture.Controller.IsCurrent(second.Fence));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => fixture.Controller.BeginGenerationAsync(secondId, TestContext.Current.CancellationToken));
        Assert.True(fixture.Controller.IsCurrent(third.Fence));
    }

    [Fact]
    public async Task EndFencesExistingAndNewRequestsAndFinalizesOnce()
    {
        await using var fixture = new Fixture();
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        using var old = await fixture.Controller.BeginGenerationAsync(NewRequest(), TestContext.Current.CancellationToken);
        fixture.Effects.BlockDrain = true;
        fixture.Clock.AdvanceTo(100_000);

        Task ending = fixture.Controller.EndAsync(TestContext.Current.CancellationToken);
        await fixture.Effects.DrainEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        fixture.Clock.AdvanceTo(200_000);
        Task repeatedEnd = fixture.Controller.EndAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.False(repeatedEnd.IsCompleted);
            var fence = Assert.Single(fixture.Effects.Fences);
            Assert.Equal(CaptureStopReason.End, fence.Reason);
            Assert.Equal(100_000, fence.Cutoff.Offset.Value);
            Assert.True(old.CancellationToken.IsCancellationRequested);
            Assert.False(fixture.Controller.IsCurrent(old.Fence));
            Assert.False(fixture.Controller.CompleteGeneration(old.Fence));
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() =>
                fixture.Controller.BeginGenerationAsync(NewRequest(), TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken));
        }
        finally
        {
            fixture.Effects.DrainResult.TrySetResult(new DrainOutcome(true));
            await Task.WhenAll(ending, repeatedEnd).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
        await fixture.Controller.EndAsync(TestContext.Current.CancellationToken);
        Assert.Equal(SessionLifecycleState.Completed, fixture.Controller.Snapshot.State);
        Assert.Equal(1, fixture.Effects.FinalizeCalls);
    }

    [Fact]
    public async Task FailedDurableStartRequiresRecoveryWithoutOpeningCapture()
    {
        await using var fixture = new Fixture();
        fixture.Effects.FailStart = true;

        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionLifecycleState.RecoveryRequired, fixture.Controller.Snapshot.State);
        Assert.Equal(EventErrorCategory.StorageUnavailable, fixture.Controller.Snapshot.Error);
        Assert.Equal(0, fixture.Effects.OpenCalls);
        Assert.False(fixture.Effects.Acquiring);
        Assert.False(fixture.Effects.Uploading);
    }

    [Fact]
    public async Task FailedFinalizationNeverClaimsCompletedAndStopsCapture()
    {
        await using var fixture = new Fixture();
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        fixture.Effects.FailFinalize = true;

        await fixture.Controller.EndAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionLifecycleState.RecoveryRequired, fixture.Controller.Snapshot.State);
        Assert.Equal(EventErrorCategory.StorageUnavailable, fixture.Controller.Snapshot.Error);
        Assert.False(fixture.Effects.Acquiring);
        Assert.False(fixture.Effects.Uploading);
        Assert.Equal(1, fixture.Effects.FinalizeCalls);
    }

    [Fact]
    public async Task UncooperativeDrainIsBoundedAndCannotBeFinalized()
    {
        await using var fixture = new Fixture(TimeSpan.FromMilliseconds(100));
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        fixture.Effects.BlockDrain = true;

        await fixture.Controller.EndAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Equal(SessionLifecycleState.RecoveryRequired, fixture.Controller.Snapshot.State);
        Assert.Equal(EventErrorCategory.ProviderUnavailable, fixture.Controller.Snapshot.Error);
        Assert.Null(fixture.Effects.FinalizedDrain);
        Assert.Equal(0, fixture.Effects.FinalizeCalls);
        Assert.False(fixture.Effects.Acquiring);
        Assert.False(fixture.Effects.Uploading);
    }

    [Fact]
    public async Task ExplicitlyIncompleteDrainFinalizesWithoutClaimingCompleteTranscript()
    {
        await using var fixture = new Fixture();
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        fixture.Effects.DrainComplete = false;

        await fixture.Controller.EndAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionLifecycleState.Completed, fixture.Controller.Snapshot.State);
        var outcome = Assert.IsType<DrainOutcome>(fixture.Effects.FinalizedDrain);
        Assert.False(outcome.Complete);
        Assert.Equal(1, fixture.Effects.FinalizeCalls);
    }

    [Fact]
    public async Task PersistenceFailureStopsCaptureAndInvalidatesOutstandingWork()
    {
        await using var fixture = new Fixture();
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        using var old = await fixture.Controller.BeginGenerationAsync(NewRequest(), TestContext.Current.CancellationToken);
        fixture.Effects.QueueUnsent(2);
        var lease = Assert.IsType<FakeEffects.FakeCaptureLease>(fixture.Effects.LatestLease);

        await fixture.Controller.ReportPersistenceFailureAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionLifecycleState.RecoveryRequired, fixture.Controller.Snapshot.State);
        Assert.False(fixture.Effects.Acquiring);
        Assert.False(fixture.Effects.Uploading);
        Assert.False(fixture.Controller.IsCurrent(old.Fence));
        Assert.Equal(0, fixture.Effects.UnsentFrames);
        Assert.Contains(CaptureStopReason.Recovery, lease.Stops);
        await lease.Disposed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task StorageFailureStopsCaptureEvenWhileAnUnrelatedEffectIsBlocked()
    {
        await using var fixture = new Fixture();
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        fixture.Effects.BlockHide = true;
        Task hiding = fixture.Controller.HideAsync(TestContext.Current.CancellationToken);
        await fixture.Effects.HideEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Task recovery = fixture.Controller.ReportPersistenceFailureAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.Equal(SessionLifecycleState.RecoveryRequired, fixture.Controller.Snapshot.State);
            await recovery.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.False(fixture.Effects.Acquiring);
            Assert.False(fixture.Effects.Uploading);
        }
        finally
        {
            fixture.Effects.HideRelease.TrySetResult();
            await hiding.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await recovery.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task FailedShutdownDoesNotPretendCaptureStopped()
    {
        await using var fixture = new Fixture();
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        fixture.Effects.FailShutdown = true;

        await fixture.Controller.ReportPersistenceFailureAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionLifecycleState.RecoveryRequired, fixture.Controller.Snapshot.State);
        Assert.NotEqual(SessionCaptureState.Stopped, fixture.Controller.Snapshot.Capture);
        Assert.True(fixture.Effects.Acquiring);
    }

    [Fact]
    public async Task StopTimeoutDoesNotReportAnUnconfirmedLeaseAsStopped()
    {
        await using var fixture = new Fixture(TimeSpan.FromMilliseconds(100));
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        fixture.Effects.BlockStop = true;
        Task ending = fixture.Controller.EndAsync(TestContext.Current.CancellationToken);
        try
        {
            await fixture.Effects.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await ending.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(SessionLifecycleState.RecoveryRequired, fixture.Controller.Snapshot.State);
            Assert.Equal(EventErrorCategory.DeviceUnavailable, fixture.Controller.Snapshot.Error);
            Assert.NotEqual(SessionCaptureState.Stopped, fixture.Controller.Snapshot.Capture);
            Assert.Equal(0, fixture.Effects.FinalizeCalls);
            Assert.False(fixture.Effects.Uploading);
        }
        finally
        {
            fixture.Effects.StopRelease.TrySetResult();
        }
    }

    [Fact]
    public async Task PersistenceFailureDuringPendingLeaseStopDoesNotClaimStopped()
    {
        await using var fixture = new Fixture(TimeSpan.FromMilliseconds(100));
        await fixture.Controller.StartAsync(TestContext.Current.CancellationToken);
        fixture.Effects.BlockStop = true;
        Task pausing = fixture.Controller.PauseAsync(TestContext.Current.CancellationToken);
        await fixture.Effects.StopEntered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Task recovery = fixture.Controller.ReportPersistenceFailureAsync(TestContext.Current.CancellationToken);
        try
        {
            Assert.NotEqual(SessionCaptureState.Stopped, fixture.Controller.Snapshot.Capture);
            await recovery.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(SessionLifecycleState.RecoveryRequired, fixture.Controller.Snapshot.State);
            Assert.Equal(SessionCaptureState.Failed, fixture.Controller.Snapshot.Capture);
            Assert.False(fixture.Effects.Uploading);
        }
        finally
        {
            fixture.Effects.StopRelease.TrySetResult();
            await pausing.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            await recovery.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task UnclosedSessionIsMarkedInterruptedWithoutStartingCapture()
    {
        await using var fixture = new Fixture();

        await fixture.Controller.MarkInterruptedAsync(TestContext.Current.CancellationToken);
        await fixture.Controller.MarkInterruptedAsync(TestContext.Current.CancellationToken);

        Assert.Equal(SessionLifecycleState.Interrupted, fixture.Controller.Snapshot.State);
        Assert.Equal(0, fixture.Effects.OpenCalls);
        Assert.Equal(1, fixture.Effects.InterruptedCalls);
        Assert.False(fixture.Effects.Acquiring);
    }

    private static RequestId NewRequest() => new(Guid.NewGuid());

    private sealed class Fixture : IAsyncDisposable
    {
        public Fixture(TimeSpan? drainTimeout = null)
        {
            var id = new SessionId(Guid.NewGuid());
            Clock = new FakeClock(id);
            Effects = new FakeEffects();
            Controller = new SessionLifecycleCoordinator(id, Clock, Effects, drainTimeout ?? TimeSpan.FromSeconds(2));
        }

        public FakeClock Clock { get; }
        public FakeEffects Effects { get; }
        public SessionLifecycleCoordinator Controller { get; }
        public ValueTask DisposeAsync() => Controller.DisposeAsync();
    }

    private sealed class FakeClock : ISessionClock
    {
        private long _offset;

        public FakeClock(SessionId session)
        {
            Anchor = new SessionClockAnchor(session, new TimelinePosition(new TimelineEpochId(Guid.NewGuid()), new SessionOffset(0)),
                new UtcTimestamp(DateTimeOffset.UnixEpoch));
        }

        public SessionClockAnchor Anchor { get; }
        public void AdvanceTo(long value) => _offset = value;
        public TimelinePosition GetPosition() => new(Anchor.Origin.TimelineEpoch, new SessionOffset(_offset));
        public UtcTimestamp GetUtcNow() => new(DateTimeOffset.UnixEpoch.AddTicks(_offset * 10));
    }

    private sealed class PendingOpen
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ICaptureLease> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FakeEffects : ISessionLifecycleEffects
    {
        private readonly ConcurrentQueue<PendingOpen> _blockedOpens = new();
        public int PersistCalls { get; private set; }
        public int OpenCalls { get; private set; }
        public int PauseCalls { get; private set; }
        public int ClearCalls { get; private set; }
        public int FinalizeCalls { get; private set; }
        public int InterruptedCalls { get; private set; }
        public bool FailStart { get; set; }
        public bool FailFinalize { get; set; }
        public bool BlockDrain { get; set; }
        public bool BlockHide { get; set; }
        public bool BlockClear { get; set; }
        public bool BlockStop { get; set; }
        public bool FailShutdown { get; set; }
        public bool DrainComplete { get; set; } = true;
        public TaskCompletionSource DrainEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<DrainOutcome> DrainResult { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HideEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource HideRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ClearEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ClearRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopRelease { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Acquiring { get; private set; }
        public bool Uploading { get; private set; }
        public int UnsentFrames { get; private set; }
        public int DroppedFrames { get; private set; }
        public TimelinePosition? PauseCutoff { get; private set; }
        public DrainOutcome? FinalizedDrain { get; private set; }
        public FakeCaptureLease? LatestLease { get; private set; }
        public List<string> StoredHistory { get; } = [];
        public List<long> ClearEpochs { get; } = [];
        public List<(CaptureStopReason Reason, TimelinePosition Cutoff)> Fences { get; } = [];

        public PendingOpen BlockNextOpen()
        {
            var pending = new PendingOpen();
            _blockedOpens.Enqueue(pending);
            return pending;
        }

        public FakeCaptureLease NewLease() => LatestLease = new FakeCaptureLease(this);
        public void QueueUnsent(int count) => UnsentFrames += count;
        public Task PersistStartAsync(SessionId id, CancellationToken cancellationToken)
        {
            PersistCalls++;
            return FailStart ? Task.FromException(new IOException("Synthetic storage failure.")) : Task.CompletedTask;
        }

        public Task<ICaptureLease> OpenCaptureAsync(SessionId id, CancellationToken cancellationToken)
        {
            OpenCalls++;
            if (_blockedOpens.TryDequeue(out var pending))
            {
                pending.Entered.SetResult();
                return pending.Result.Task; // Deliberately ignores cancellation.
            }
            return Task.FromResult<ICaptureLease>(NewLease());
        }

        public Task PauseAsync(SessionId id, TimelinePosition cutoff, CancellationToken cancellationToken)
        {
            PauseCalls++;
            PauseCutoff = cutoff;
            StopAndDiscard();
            return Task.CompletedTask;
        }

        public void FenceCapture(SessionId id, CaptureStopReason reason, TimelinePosition cutoff)
        {
            Fences.Add((reason, cutoff));
            if (FailShutdown) throw new IOException("Synthetic capture fence failure.");
            StopAndDiscard();
        }

        public Task<DrainOutcome> StopAndDrainAsync(SessionId id, TimelinePosition cutoff, TimeSpan timeout, CancellationToken cancellationToken)
        {
            StopAndDiscard();
            if (BlockDrain) { DrainEntered.TrySetResult(); return DrainResult.Task; }
            return Task.FromResult(new DrainOutcome(DrainComplete));
        }

        public Task FinalizeAndEnqueueAsync(SessionId id, DrainOutcome outcome, CancellationToken cancellationToken)
        {
            FinalizeCalls++;
            FinalizedDrain = outcome;
            return FailFinalize ? Task.FromException(new IOException("Synthetic storage failure.")) : Task.CompletedTask;
        }

        public Task ClearDerivedContextAsync(SessionId id, ContextEpoch epoch, TimelinePosition boundary, CancellationToken cancellationToken)
        {
            ClearCalls++;
            ClearEpochs.Add(epoch.Value);
            if (BlockClear)
            {
                ClearEntered.TrySetResult();
                return ClearRelease.Task;
            }
            return Task.CompletedTask;
        }

        public Task HideAsync(SessionId id, CancellationToken cancellationToken)
        {
            if (!BlockHide) return Task.CompletedTask;
            HideEntered.TrySetResult();
            return HideRelease.Task;
        }
        public Task StopForRecoveryAsync(SessionId id, CancellationToken cancellationToken)
        {
            if (FailShutdown) return Task.FromException(new IOException("Synthetic capture shutdown failure."));
            StopAndDiscard();
            return Task.CompletedTask;
        }
        public Task MarkInterruptedAsync(SessionId id, CancellationToken cancellationToken) { InterruptedCalls++; return Task.CompletedTask; }
        private void StopAndDiscard() { Acquiring = false; Uploading = false; DroppedFrames += UnsentFrames; UnsentFrames = 0; }

        public sealed class FakeCaptureLease(FakeEffects owner) : ICaptureLease
        {
            private bool _stopped;
            public int Activations { get; private set; }
            public List<CaptureStopReason> Stops { get; } = [];
            public TaskCompletionSource Disposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
            public void Activate() { Activations++; owner.Acquiring = true; owner.Uploading = true; }
            public void BeginDeviceReconnect() { owner.Acquiring = false; owner.Uploading = false; }
            public void FinishDeviceReconnect() { if (!_stopped) Activate(); }
            public Task StopAsync(CaptureStopReason reason, CancellationToken cancellationToken)
            {
                Stops.Add(reason);
                if (owner.FailShutdown) return Task.FromException(new IOException("Synthetic capture shutdown failure."));
                _stopped = true;
                if (owner.BlockStop)
                {
                    owner.StopEntered.TrySetResult();
                    return owner.StopRelease.Task;
                }
                return Task.CompletedTask;
            }
            public ValueTask DisposeAsync()
            {
                if (owner.FailShutdown) return ValueTask.FromException(new IOException("Synthetic disposal failure."));
                Disposed.TrySetResult();
                return ValueTask.CompletedTask;
            }
        }
    }
}
