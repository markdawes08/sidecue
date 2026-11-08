using System.Buffers;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;
using Sidecue.Windows.Audio;
using Xunit;

namespace Sidecue.Windows.Tests.Audio;

public sealed class RecoveringAudioCaptureTests
{
    [Fact]
    public async Task DefaultChangeReopensWithNewEpochAndPreservesGap()
    {
        var clock = new TestClock();
        var request = Request(clock);
        using var monitor = new Monitor();
        var source = new FakeSource();
        await using var session = new RecoveringAudioCapture(source, request, clock, monitor);
        var first = await source.NextAsync();
        await using var frames = session.ReadFramesAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        first.Emit(100_000);
        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        var initial = frames.Current.Metadata;
        frames.Current.Dispose();
        monitor.Signal(CaptureSourceKind.RemotePlayback);
        monitor.Signal(CaptureSourceKind.Microphone);
        var second = await source.NextAsync();
        Assert.Equal(initial.Source.SourceId, second.Request.Source.SourceId);
        Assert.Equal(initial.Source.SessionId, second.Request.Source.SessionId);
        Assert.Equal(initial.Source.StreamEpoch.Value + 1, second.Request.Source.StreamEpoch.Value);
        Assert.Equal(request.ClockAnchor, second.Request.ClockAnchor);
        Assert.Equal(request.Selection, second.Request.Selection);
        second.Emit(600_000);
        SourceIdentity observed;
        do
        {
            Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            observed = frames.Current.Metadata.Source;
            frames.Current.Dispose();
        }
        while (observed == first.Request.Source);
        Assert.Equal(second.Request.Source, observed);
        await session.StopAsync(CaptureStopReason.End, TestContext.Current.CancellationToken);
        var statuses = await CollectStatusesAsync(session);
        Assert.Contains(statuses, status => status.Gap is { Reason: GapReason.DeviceUnavailable, Certainty: TimingCertainty.Unknown } gap && gap.Source == first.Request.Source);
        Assert.Equal(1, first.DisposeCount);
        Assert.Equal(1, second.DisposeCount);
    }

    [Fact]
    public async Task RepeatedImmediateDeviceFailureStopsAtBoundedAttemptCount()
    {
        var clock = new TestClock();
        var request = Request(clock);
        using var monitor = new Monitor();
        var source = new FakeSource { FailImmediately = true };
        await using var session = new RecoveringAudioCapture(source, request, clock, monitor);
        var statuses = await CollectStatusesAsync(session);
        Assert.Equal(3, source.Opens);
        Assert.Equal(CaptureState.Failed, session.TerminalStatus?.State);
        Assert.Equal(EventErrorCategory.DeviceUnavailable, session.TerminalStatus?.Error);
        Assert.Contains(statuses, status => status.Gap?.Reason == GapReason.CaptureFailure);
    }

    [Fact]
    public async Task OnePacketBeforeEachInvalidationStillExhaustsRetryBudget()
    {
        var clock = new TestClock();
        var request = Request(clock);
        using var monitor = new Monitor();
        var source = new FakeSource { FailImmediately = true, EmitBeforeFailure = true };
        await using var session = new RecoveringAudioCapture(source, request, clock, monitor);
        _ = await CollectStatusesAsync(session);
        Assert.Equal(3, source.Opens);
        Assert.Equal(EventErrorCategory.DeviceUnavailable, session.TerminalStatus?.Error);
    }

    [Fact]
    public async Task CancellationDuringNoncooperativeOpenStopsPromptlyAndDisposesLateResult()
    {
        var clock = new TestClock();
        var request = Request(clock);
        using var monitor = new Monitor();
        var source = new BlockingSource();
        await using var session = new RecoveringAudioCapture(source, request, clock, monitor);
        await source.Opened.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await session.StopAsync(CaptureStopReason.End, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var late = new FakeRun(request);
        source.Return(late);
        await late.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(CaptureState.Stopped, session.TerminalStatus?.State);
        await using var frames = session.ReadFramesAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.False(await frames.MoveNextAsync());
    }

    [Fact]
    public async Task FailedReopenRetainsThePreviousEpochGap()
    {
        var clock = new TestClock();
        var request = Request(clock);
        using var monitor = new Monitor();
        var source = new FakeSource { DenyAfterFirst = true };
        await using var session = new RecoveringAudioCapture(source, request, clock, monitor);
        var first = await source.NextAsync();
        await using var frames = session.ReadFramesAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        first.Emit(100_000);
        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        frames.Current.Dispose();
        monitor.Signal(CaptureSourceKind.Microphone);
        var statuses = await CollectStatusesAsync(session);
        Assert.Equal(EventErrorCategory.PermissionDenied, session.TerminalStatus?.Error);
        Assert.Contains(statuses, status => status.Gap is { Reason: GapReason.DeviceUnavailable } gap && gap.Source == first.Request.Source);
        Assert.Equal(CaptureState.Failed, statuses[^1].State);
    }

    [Fact]
    public async Task DefaultChangeDuringOpenDisposesStaleRunBeforeReopening()
    {
        var clock = new TestClock();
        var request = Request(clock);
        using var monitor = new Monitor();
        var source = new DelayedFirstSource();
        await using var session = new RecoveringAudioCapture(source, request, clock, monitor);
        await source.Opened.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        monitor.Signal(CaptureSourceKind.Microphone);
        var stale = new FakeRun(request);
        source.ReturnFirst(stale);
        var next = await source.Next.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(1, stale.DisposeCount);
        Assert.Equal(1, next.Request.Source.StreamEpoch.Value);
        await session.StopAsync(CaptureStopReason.End, TestContext.Current.CancellationToken);
        Assert.Equal(1, next.DisposeCount);
    }

    [Fact]
    public async Task PermissionFailureIsNotRetried()
    {
        var clock = new TestClock();
        var request = Request(clock);
        using var monitor = new Monitor();
        var source = new DeniedSource();
        await using var session = new RecoveringAudioCapture(source, request, clock, monitor);
        _ = await CollectStatusesAsync(session);
        Assert.Equal(1, source.Opens);
        Assert.Equal(EventErrorCategory.PermissionDenied, session.TerminalStatus?.Error);
    }

    [Fact]
    public async Task FatalFailureWinsOverSimultaneousEndpointChange()
    {
        var clock = new TestClock();
        var request = Request(clock);
        using var monitor = new Monitor();
        var source = new FakeSource();
        await using var session = new RecoveringAudioCapture(source, request, clock, monitor);
        var first = await source.NextAsync();
        first.Fail(EventErrorCategory.PermissionDenied);
        monitor.Signal(CaptureSourceKind.Microphone);
        _ = await CollectStatusesAsync(session);
        Assert.Equal(1, source.Opens);
        Assert.Equal(EventErrorCategory.PermissionDenied, session.TerminalStatus?.Error);
    }

    [Fact]
    public async Task OverlappingNewEpochOutputIsDiscardedAndReported()
    {
        var clock = new TestClock();
        var request = Request(clock);
        using var monitor = new Monitor();
        var source = new FakeSource();
        await using var session = new RecoveringAudioCapture(source, request, clock, monitor);
        var first = await source.NextAsync();
        await using var frames = session.ReadFramesAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        first.Emit(100_000);
        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        frames.Current.Dispose();
        monitor.Signal(CaptureSourceKind.Microphone);
        var second = await source.NextAsync();
        second.Emit(150_000);
        second.Emit(600_000);
        SourceIdentity observed;
        long start;
        do
        {
            Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            observed = frames.Current.Metadata.Source;
            start = frames.Current.Metadata.Interval.Start.Offset.Value;
            frames.Current.Dispose();
        }
        while (observed == first.Request.Source);
        Assert.Equal(second.Request.Source, observed);
        Assert.True(start >= 300_000);
        await session.StopAsync(CaptureStopReason.End, TestContext.Current.CancellationToken);
        var statuses = await CollectStatusesAsync(session);
        Assert.Contains(statuses, status => status.Gap is { Certainty: TimingCertainty.Unknown } gap && gap.Source == second.Request.Source && gap.Interval.Start.Offset.Value == 150_000);
    }

    [Fact]
    public async Task NoncooperativeStopDoesNotStartAnotherEpochAndEventuallyDisposes()
    {
        var clock = new TestClock();
        var request = Request(clock);
        using var monitor = new Monitor();
        var source = new FakeSource { BlockStop = true };
        await using var session = new RecoveringAudioCapture(source, request, clock, monitor);
        var first = await source.NextAsync();
        monitor.Signal(CaptureSourceKind.Microphone);
        await first.StopCalled.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await session.StopAsync(CaptureStopReason.End, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        Assert.Equal(1, source.Opens);
        Assert.Equal(CaptureState.Failed, session.TerminalStatus?.State);
        Assert.False(first.Disposed);
        first.ReleaseStop();
        await first.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(1, first.DisposeCount);
    }

    [Fact]
    public async Task NoncooperativeDisposalPreventsReopenAndEventuallyFinishes()
    {
        var clock = new TestClock();
        var request = Request(clock);
        using var monitor = new Monitor();
        var source = new FakeSource { BlockDispose = true };
        await using var session = new RecoveringAudioCapture(source, request, clock, monitor);
        var first = await source.NextAsync();
        monitor.Signal(CaptureSourceKind.Microphone);
        await first.DisposeCalled.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await session.StopAsync(CaptureStopReason.End, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        Assert.Equal(1, source.Opens);
        Assert.Equal(CaptureState.Failed, session.TerminalStatus?.State);
        first.ReleaseDispose();
        await first.DisposedSignal.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(1, first.DisposeCount);
    }

    [Fact]
    public async Task FullStatusQueueRetainsFinalFailureForStreamReader()
    {
        var clock = new TestClock();
        var request = Request(clock);
        using var monitor = new Monitor();
        var source = new FakeSource();
        await using var session = new RecoveringAudioCapture(source, request, clock, monitor);
        var first = await source.NextAsync();
        for (var index = 0; index < 100 && !first.StopCalled.Task.IsCompleted; index++)
        {
            first.EmitGap(index);
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }
        await first.StopCalled.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var statuses = await CollectStatusesAsync(session);
        Assert.Equal(CaptureState.Failed, session.TerminalStatus?.State);
        Assert.Equal(CaptureState.Failed, statuses[^1].State);
        Assert.NotNull(statuses[^1].Gap);
    }

    private static async Task<List<CaptureStatusEvent>> CollectStatusesAsync(RecoveringAudioCapture session)
    {
        var result = new List<CaptureStatusEvent>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        await foreach (var status in session.ReadStatusAsync(timeout.Token)) { result.Add(status); }
        return result;
    }

    private static CaptureRequest Request(TestClock clock) => new(ContractVersion.Current,
        new SourceIdentity(clock.Anchor.SessionId, new SourceId(Guid.NewGuid()), new StreamEpoch(0)), clock.Anchor,
        new CaptureSourceSelection(CaptureSourceKind.Microphone, NAudioCaptureBackendFactory.SystemDefaultSelector),
        new AudioBufferPolicy(2_000_000, 64_000));

    private sealed class Monitor : IAudioEndpointChangeMonitor
    {
        public event Action<CaptureSourceKind>? DefaultEndpointChanged;
        internal void Signal(CaptureSourceKind kind) => DefaultEndpointChanged?.Invoke(kind);
        public void Dispose() { }
    }

    private sealed class TestClock : ISessionClock
    {
        private long _value = 1_000_000;
        public SessionClockAnchor Anchor { get; } = new(new SessionId(Guid.NewGuid()),
            new TimelinePosition(new TimelineEpochId(Guid.NewGuid()), new SessionOffset(0)), new UtcTimestamp(DateTimeOffset.UnixEpoch));
        public TimelinePosition GetPosition() => new(Anchor.Origin.TimelineEpoch, new SessionOffset(Interlocked.Add(ref _value, 10_000)));
        public UtcTimestamp GetUtcNow() => new(DateTimeOffset.UnixEpoch);
    }

    private sealed class FakeSource : IAudioCaptureSource
    {
        private readonly Channel<FakeRun> _opened = Channel.CreateUnbounded<FakeRun>();
        private int _opens;
        internal bool FailImmediately { get; init; }
        internal bool EmitBeforeFailure { get; init; }
        internal bool DenyAfterFirst { get; init; }
        internal bool BlockStop { get; init; }
        internal bool BlockDispose { get; init; }
        internal int Opens => Volatile.Read(ref _opens);
        public Task<IAudioCaptureRun> OpenAsync(CaptureRequest request, CancellationToken ct)
        {
            var count = Interlocked.Increment(ref _opens);
            if (DenyAfterFirst && count > 1) { throw new UnauthorizedAccessException(); }
            var run = new FakeRun(request) { BlockStop = BlockStop, BlockDispose = BlockDispose };
            _opened.Writer.TryWrite(run);
            if (FailImmediately)
            {
                if (EmitBeforeFailure) { run.Emit(100_000); }
                run.Fail();
            }
            return Task.FromResult<IAudioCaptureRun>(run);
        }
        internal async Task<FakeRun> NextAsync() => await _opened.Reader.ReadAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
    }

    private sealed class BlockingSource : IAudioCaptureSource
    {
        private readonly TaskCompletionSource<IAudioCaptureRun> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IAudioCaptureRun> OpenAsync(CaptureRequest request, CancellationToken ct) { Opened.TrySetResult(); return _result.Task; }
        internal void Return(IAudioCaptureRun run) => _result.TrySetResult(run);
    }

    private sealed class DelayedFirstSource : IAudioCaptureSource
    {
        private readonly TaskCompletionSource<IAudioCaptureRun> _first = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _opens;
        internal TaskCompletionSource Opened { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource<FakeRun> Next { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IAudioCaptureRun> OpenAsync(CaptureRequest request, CancellationToken ct)
        {
            if (Interlocked.Increment(ref _opens) == 1) { Opened.TrySetResult(); return _first.Task; }
            var run = new FakeRun(request);
            Next.TrySetResult(run);
            return Task.FromResult<IAudioCaptureRun>(run);
        }
        internal void ReturnFirst(FakeRun run) => _first.TrySetResult(run);
    }

    private sealed class DeniedSource : IAudioCaptureSource
    {
        internal int Opens { get; private set; }
        public Task<IAudioCaptureRun> OpenAsync(CaptureRequest request, CancellationToken ct)
        {
            Opens++;
            throw new UnauthorizedAccessException();
        }
    }

    private sealed class FakeRun(CaptureRequest request) : IAudioCaptureRun
    {
        private readonly Channel<OwnedAudioFrame> _frames = Channel.CreateUnbounded<OwnedAudioFrame>();
        private readonly Channel<CaptureStatusEvent> _statuses = Channel.CreateUnbounded<CaptureStatusEvent>();
        private int _disposed;
        private long _sequence;
        private readonly TaskCompletionSource _releaseStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _releaseDispose = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CaptureRequest Request { get; } = request;
        internal bool BlockStop { get; init; }
        internal bool BlockDispose { get; init; }
        internal TaskCompletionSource StopCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource DisposeCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal bool Disposed => Volatile.Read(ref _disposed) != 0;
        internal int DisposeCount => Volatile.Read(ref _disposed);
        internal TaskCompletionSource DisposedSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CaptureStatusEvent? TerminalStatus { get; private set; }
        public IAsyncEnumerable<OwnedAudioFrame> ReadFramesAsync(CancellationToken ct) => _frames.Reader.ReadAllAsync(ct);
        public IAsyncEnumerable<CaptureStatusEvent> ReadStatusAsync(CancellationToken ct) => _statuses.Reader.ReadAllAsync(ct);

        internal void Emit(long offset)
        {
            var format = new AudioFormat(16_000, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian);
            var timeline = Request.ClockAnchor.Origin.TimelineEpoch;
            var start = new TimelinePosition(timeline, new SessionOffset(offset));
            var end = new TimelinePosition(timeline, new SessionOffset(offset + 200_000));
            var metadata = new AudioFrameMetadata(Request.Version, Request.Source, new FrameSequence(_sequence++), new TimelineInterval(start, end), format, 3_200, AudioFrameAttributes.None);
            _frames.Writer.TryWrite(new OwnedAudioFrame(metadata, new Owner(6_400), 6_400));
        }

        internal void Fail(EventErrorCategory category = EventErrorCategory.DeviceUnavailable) => Complete(CaptureState.Failed, category);
        internal void EmitGap(int index)
        {
            var position = new TimelinePosition(Request.ClockAnchor.Origin.TimelineEpoch, new SessionOffset(index * 1_000L));
            var gap = new CaptureGap(Request.Version, Request.Source, new TimelineInterval(position, position),
                GapReason.CaptureDiscontinuity, TimingCertainty.Unknown);
            _statuses.Writer.TryWrite(new CaptureStatusEvent(Request.Version, Request.Source, position, CaptureState.Degraded, gap: gap));
        }
        internal void ReleaseStop() => _releaseStop.TrySetResult();
        internal void ReleaseDispose() => _releaseDispose.TrySetResult();
        public async Task StopAsync(CaptureStopReason reason, CancellationToken ct)
        {
            StopCalled.TrySetResult();
            if (BlockStop) { await _releaseStop.Task.ConfigureAwait(false); }
            Complete(reason == CaptureStopReason.Pause ? CaptureState.Paused : CaptureState.Stopped, null);
        }
        private void Complete(CaptureState state, EventErrorCategory? error)
        {
            TerminalStatus ??= new CaptureStatusEvent(Request.Version, Request.Source, Request.ClockAnchor.Origin, state, error);
            _statuses.Writer.TryWrite(TerminalStatus);
            _statuses.Writer.TryComplete();
            _frames.Writer.TryComplete();
        }
        public async ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposed);
            DisposeCalled.TrySetResult();
            if (BlockDispose) { await _releaseDispose.Task.ConfigureAwait(false); }
            while (_frames.Reader.TryRead(out var frame)) { frame.Dispose(); }
            DisposedSignal.TrySetResult();
        }
    }

    private sealed class Owner(int length) : IMemoryOwner<byte>
    {
        private byte[]? _bytes = new byte[length];
        public Memory<byte> Memory => _bytes ?? throw new ObjectDisposedException(nameof(Owner));
        public void Dispose() { _bytes = null; GC.SuppressFinalize(this); }
    }
}
