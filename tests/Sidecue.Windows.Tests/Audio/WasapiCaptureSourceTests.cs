using System.Buffers;
using NAudio.Wave;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;
using Sidecue.Windows.Audio;
using Xunit;
using CaptureState = Sidecue.Core.Contracts.CaptureState;

namespace Sidecue.Windows.Tests.Audio;

public sealed class WasapiCaptureSourceTests
{
    [Fact]
    public void EndpointMixFormatMustBePreciselyRepresentable()
    {
        var pcm = NAudioCaptureBackendFactory.MapFormat(new WaveFormat(48_000, 16, 2));
        var floating = NAudioCaptureBackendFactory.MapFormat(WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2));
        var packed = new WaveFormatExtensible(48_000, 32, 2, AudioMediaSubtypes.MEDIASUBTYPE_PCM, 24, 0);
        var unknown = new WaveFormatExtensible(48_000, 32, 2,
            Guid.Parse("55555555-5555-5555-5555-555555555555"), 32, 0);

        Assert.Equal(AudioSampleRepresentation.Pcm16, pcm.Representation);
        Assert.Equal(4, pcm.BytesPerFrame);
        Assert.Equal(AudioSampleRepresentation.FloatingPoint32, floating.Representation);
        Assert.Equal(8, floating.BytesPerFrame);
        Assert.Throws<NotSupportedException>(() => NAudioCaptureBackendFactory.MapFormat(packed));
        Assert.Throws<NotSupportedException>(() => NAudioCaptureBackendFactory.MapFormat(unknown));
    }

    [Fact]
    public async Task OpenAndStartFailuresReleaseAcquiredBackend()
    {
        var clock = new FakeClock();
        var openFailure = new FakeFactory { OpenFailure = new InvalidOperationException("synthetic open failure") };
        var source = new WasapiCaptureSource(clock, openFailure);

        await Assert.ThrowsAnyAsync<Exception>(() => source.OpenAsync(Request(clock), CancellationToken.None));
        Assert.Equal(1, openFailure.OpenCount);

        var backend = new FakeBackend { StartFailure = new InvalidOperationException("synthetic start failure") };
        var startFailure = new FakeFactory { Backend = backend };
        source = new WasapiCaptureSource(clock, startFailure);

        await Assert.ThrowsAnyAsync<Exception>(() => source.OpenAsync(Request(clock), CancellationToken.None));
        Assert.Equal(1, startFailure.OpenCount);
        Assert.Equal(1, backend.DisposeCount);
    }

    [Fact]
    public async Task BackendStoppingDuringStartDoesNotReportLiveAfterFailure()
    {
        var clock = new FakeClock();
        var backend = new FakeBackend { StartStopError = new UnauthorizedAccessException("synthetic capture access denial") };
        var source = new WasapiCaptureSource(clock, new FakeFactory { Backend = backend });
        await using var run = await source.OpenAsync(Request(clock), CancellationToken.None);
        await run.StopAsync(CaptureStopReason.End, CancellationToken.None);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var statuses = new List<CaptureStatusEvent>();
        await foreach (var status in run.ReadStatusAsync(timeout.Token)) { statuses.Add(status); }

        Assert.Equal(CaptureState.Failed, run.TerminalStatus?.State);
        Assert.Equal(EventErrorCategory.PermissionDenied, run.TerminalStatus?.Error);
        Assert.DoesNotContain(statuses, status => status.State == CaptureState.Live);
        Assert.Equal(1, backend.DisposeCount);
        Assert.False(backend.CleanupDuringStart);
    }

    [Fact]
    public async Task MalformedCallbackFailsWithExplicitUnknownCaptureLossAndReleasesBackend()
    {
        var clock = new FakeClock();
        var backend = new FakeBackend();
        using var pool = new TrackingPool();
        var source = new WasapiCaptureSource(clock, new FakeFactory { Backend = backend }, pool);
        var request = Request(clock);
        var run = await source.OpenAsync(request, CancellationToken.None);

        // Two PCM16 mono samples require four bytes. This malformed borrowed
        // packet must become an explicit unknown loss, not silently disappear.
        backend.Deliver([1, 2, 3], 2, AudioFrameAttributes.None, 100, 200);
        await run.StopAsync(CaptureStopReason.End, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        await run.DisposeAsync();

        Assert.Equal(CaptureState.Failed, run.TerminalStatus?.State);
        Assert.Equal(GapReason.CaptureFailure, run.TerminalStatus?.Gap?.Reason);
        Assert.Equal(TimingCertainty.Unknown, run.TerminalStatus?.Gap?.Certainty);
        Assert.Equal(request.Source, run.TerminalStatus?.Gap?.Source);
        Assert.Equal(1, backend.DisposeCount);
        Assert.Equal(pool.RentedCount, pool.ReturnedCount);
    }

    [Fact]
    public async Task OpeningCancellationReleasesAnythingAlreadyAcquired()
    {
        var clock = new FakeClock();
        var neverOpened = new FakeFactory();
        var source = new WasapiCaptureSource(clock, neverOpened);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.OpenAsync(Request(clock), cancelled.Token));
        Assert.Equal(0, neverOpened.OpenCount);

        using var duringStart = new CancellationTokenSource();
        var backend = new FakeBackend { OnStart = duringStart.Cancel };
        source = new WasapiCaptureSource(clock, new FakeFactory { Backend = backend });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.OpenAsync(Request(clock), duringStart.Token));
        Assert.Equal(1, backend.DisposeCount);
    }

    [Theory]
    [InlineData(CaptureSourceKind.Microphone)]
    [InlineData(CaptureSourceKind.RemotePlayback)]
    public async Task CallbackCopiesBorrowedBytesAndPreservesSourceAndRawTiming(CaptureSourceKind kind)
    {
        var clock = new FakeClock();
        var backend = new FakeBackend();
        var factory = new FakeFactory { Backend = backend };
        using var pool = new TrackingPool();
        var source = new WasapiCaptureSource(clock, factory, pool);
        var request = Request(clock, kind);
        await using var run = await source.OpenAsync(request, CancellationToken.None);
        var borrowed = new byte[] { 1, 2, 3, 4 };

        backend.Deliver(borrowed, 2, AudioFrameAttributes.Discontinuity, 1_024, 5_000);
        Array.Fill(borrowed, (byte)9);
        await using var frames = run.ReadFramesAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        using var frame = frames.Current;

        Assert.Equal(request.Selection, factory.LastSelection);
        Assert.Equal(request.Source, frame.Metadata.Source);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, frame.Payload.ToArray());
        Assert.Equal(2, frame.Metadata.SampleCount);
        Assert.True(frame.Metadata.Flags.HasFlag(AudioFrameAttributes.Discontinuity));
        Assert.Equal(1_024, Assert.IsType<RawClockPosition>(frame.Metadata.DevicePosition).Ticks);
        Assert.Equal(5_000, Assert.IsType<RawClockPosition>(frame.Metadata.PerformanceCounter).Ticks);
        Assert.Equal(10_000_000, frame.Metadata.PerformanceCounter.TicksPerSecond);
        await using var statuses = run.ReadStatusAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
        Assert.True(await statuses.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.Equal(CaptureState.Live, statuses.Current.State);
        Assert.True(await statuses.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.Equal(GapReason.CaptureDiscontinuity, statuses.Current.Gap?.Reason);
        Assert.Equal(TimingCertainty.Unknown, statuses.Current.Gap?.Certainty);
    }

    [Fact]
    public async Task DisposalBeforeEnumerationReturnsQueuedBuffersAndReleasesBackend()
    {
        var clock = new FakeClock();
        var backend = new FakeBackend();
        using var pool = new TrackingPool();
        var source = new WasapiCaptureSource(clock, new FakeFactory { Backend = backend }, pool);
        var run = await source.OpenAsync(Request(clock), CancellationToken.None);
        backend.Deliver([1, 2, 3, 4], 2, AudioFrameAttributes.None, 100, 200);
        backend.Deliver([5, 6, 7, 8], 2, AudioFrameAttributes.None, 102, 300);
        Assert.Equal(2, pool.RentedCount);

        await run.DisposeAsync();
        await run.DisposeAsync();

        Assert.True(backend.StopCount >= 1);
        Assert.Equal(1, backend.DisposeCount);
        Assert.Equal(pool.RentedCount, pool.ReturnedCount);
        Assert.Equal(0, pool.ReturnedDirtyCount);
        Assert.NotNull(run.TerminalStatus);
    }

    [Fact]
    public async Task CancelledPauseStillStopsAndDiscardsUnsentFrames()
    {
        var clock = new FakeClock();
        var backend = new FakeBackend();
        using var pool = new TrackingPool();
        var source = new WasapiCaptureSource(clock, new FakeFactory { Backend = backend }, pool);
        var run = await source.OpenAsync(Request(clock), CancellationToken.None);
        backend.Deliver([1, 2, 3, 4], 2, AudioFrameAttributes.None, 100, 200);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();

        try { await run.StopAsync(CaptureStopReason.Pause, cancelled.Token); }
        catch (OperationCanceledException) { }
        await run.DisposeAsync();

        Assert.True(backend.StopCount >= 1);
        Assert.Equal(1, backend.DisposeCount);
        Assert.Equal(pool.RentedCount, pool.ReturnedCount);
        Assert.Equal(CaptureState.Paused, run.TerminalStatus?.State);
    }

    [Fact]
    public async Task StopDuringAnInFlightCallbackDoesNotPublishAfterStop()
    {
        var clock = new FakeClock();
        var backend = new FakeBackend();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var pool = new TrackingPool { OnRent = () => { entered.Set(); release.Wait(); } };
        var source = new WasapiCaptureSource(clock, new FakeFactory { Backend = backend }, pool);
        var run = await source.OpenAsync(Request(clock), CancellationToken.None);
        var callback = Task.Run(() => backend.Deliver([1, 2, 3, 4], 2, AudioFrameAttributes.None, 100, 200), TestContext.Current.CancellationToken);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
            await using var frames = run.ReadFramesAsync(TestContext.Current.CancellationToken).GetAsyncEnumerator(TestContext.Current.CancellationToken);
            var pendingRead = frames.MoveNextAsync().AsTask();
            await run.StopAsync(CaptureStopReason.Pause, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            Assert.False(await pendingRead.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        }
        finally
        {
            release.Set();
            await callback.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
            await run.DisposeAsync();
        }
        Assert.Equal(pool.RentedCount, pool.ReturnedCount);
    }

    [Fact]
    public async Task EndDuringAnInFlightCallbackDrainsOrExplicitlyReportsThePacket()
    {
        var testToken = TestContext.Current.CancellationToken;
        var clock = new FakeClock();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var callbackExited = new ManualResetEventSlim();
        using var pool = new TrackingPool { OnRent = () => { entered.Set(); release.Wait(); } };
        var backend = new FakeBackend { OnDispose = () => callbackExited.Wait(testToken) };
        var source = new WasapiCaptureSource(clock, new FakeFactory { Backend = backend }, pool);
        var request = Request(clock);
        var run = await source.OpenAsync(request, testToken);
        var frames = new List<OwnedAudioFrame>();
        var callback = Task.Run(() =>
        {
            try { backend.Deliver([1, 2, 3, 4], 2, AudioFrameAttributes.None, 100, 200); }
            finally { callbackExited.Set(); }
        }, testToken);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(3), testToken));
            var stopping = run.StopAsync(CaptureStopReason.End, testToken);
            Assert.False(stopping.IsCompleted);
            release.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(3), testToken);
            await callback.WaitAsync(TimeSpan.FromSeconds(3), testToken);

            await foreach (var frame in run.ReadFramesAsync(testToken)) { frames.Add(frame); }
            var statuses = new List<CaptureStatusEvent>();
            await foreach (var status in run.ReadStatusAsync(testToken)) { statuses.Add(status); }
            if (frames.Count == 0)
            {
                var gap = statuses.Select(status => status.Gap).FirstOrDefault(item => item is not null)
                    ?? run.TerminalStatus?.Gap;
                Assert.NotNull(gap);
                Assert.Equal(request.Source, gap.Source);
            }
            else
            {
                var frame = Assert.Single(frames);
                Assert.Equal(new byte[] { 1, 2, 3, 4 }, frame.Payload.ToArray());
            }
        }
        finally
        {
            release.Set();
            await callback.WaitAsync(TimeSpan.FromSeconds(3), testToken);
            foreach (var frame in frames) { frame.Dispose(); }
            await run.DisposeAsync();
        }
        Assert.Equal(pool.RentedCount, pool.ReturnedCount);
        Assert.Equal(0, pool.ReturnedDirtyCount);
    }

    [Fact]
    public async Task CancellingAReaderDoesNotDisposeTheCaptureRun()
    {
        var clock = new FakeClock();
        var backend = new FakeBackend();
        var source = new WasapiCaptureSource(clock, new FakeFactory { Backend = backend });
        await using var run = await source.OpenAsync(Request(clock), CancellationToken.None);
        using var cancelled = new CancellationTokenSource();
        await using var frames = run.ReadFramesAsync(cancelled.Token).GetAsyncEnumerator(cancelled.Token);
        var pendingRead = frames.MoveNextAsync().AsTask();
        cancelled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await pendingRead.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));
        Assert.Equal(0, backend.DisposeCount);
        await run.StopAsync(CaptureStopReason.End, CancellationToken.None);
        Assert.Equal(CaptureState.Stopped, run.TerminalStatus?.State);
    }

    [Fact]
    public async Task OverflowIsBoundedReportsGapAndReturnsEveryDroppedBuffer()
    {
        var clock = new FakeClock();
        var backend = new FakeBackend();
        using var pool = new TrackingPool();
        var source = new WasapiCaptureSource(clock, new FakeFactory { Backend = backend }, pool);
        var request = Request(clock, maximumBytes: 8);
        var run = await source.OpenAsync(request, CancellationToken.None);
        for (var index = 0; index < 5; index++)
        {
            backend.Deliver([1, 2, 3, 4], 2, AudioFrameAttributes.None, (ulong)(100 + (index * 2)), (ulong)(200 + index));
        }
        await run.StopAsync(CaptureStopReason.End, CancellationToken.None);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var frames = new List<OwnedAudioFrame>();
        await foreach (var frame in run.ReadFramesAsync(timeout.Token)) { frames.Add(frame); }
        var statuses = new List<CaptureStatusEvent>();
        await foreach (var status in run.ReadStatusAsync(timeout.Token)) { statuses.Add(status); }
        Assert.InRange(frames.Count, 0, 2);
        Assert.Contains(statuses, status => status.Gap is { Reason: GapReason.QueueOverflow } gap && gap.Source == request.Source);
        Assert.All(frames, frame => frame.Dispose());
        await run.DisposeAsync();
        Assert.Equal(pool.RentedCount, pool.ReturnedCount);
        Assert.Equal(0, pool.ReturnedDirtyCount);
        Assert.Equal(CaptureState.Stopped, run.TerminalStatus?.State);
    }

    [Fact]
    public async Task SaturatedGapStatusPathFailsClosedAndReleasesBuffers()
    {
        var clock = new FakeClock();
        var backend = new FakeBackend();
        using var pool = new TrackingPool();
        var source = new WasapiCaptureSource(clock, new FakeFactory { Backend = backend }, pool);
        var request = Request(clock, maximumBytes: 8);
        var run = await source.OpenAsync(request, CancellationToken.None);
        for (var index = 0; index < 40; index++)
        {
            backend.Deliver([1, 2, 3, 4], 2, AudioFrameAttributes.None, (ulong)(100 + (index * 2)), (ulong)(200 + index));
        }
        await run.StopAsync(CaptureStopReason.End, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(CaptureState.Failed, run.TerminalStatus?.State);
        Assert.Equal(GapReason.QueueOverflow, run.TerminalStatus?.Gap?.Reason);
        Assert.Equal(request.Source, run.TerminalStatus?.Gap?.Source);
        await run.DisposeAsync();
        Assert.Equal(CaptureState.Failed, run.TerminalStatus?.State);
        Assert.Equal(GapReason.CaptureFailure, run.TerminalStatus?.Gap?.Reason);
        Assert.Equal(TimingCertainty.Unknown, run.TerminalStatus?.Gap?.Certainty);
        Assert.True(pool.RentedCount > 0);
        Assert.Equal(pool.RentedCount, pool.ReturnedCount);
        Assert.Equal(0, pool.ReturnedDirtyCount);
        Assert.Equal(1, backend.DisposeCount);
    }

    private static CaptureRequest Request(FakeClock clock, CaptureSourceKind kind = CaptureSourceKind.Microphone, int maximumBytes = 1_024)
    {
        var sourceId = kind == CaptureSourceKind.Microphone
            ? new SourceId(Guid.Parse("22222222-2222-2222-2222-222222222222"))
            : new SourceId(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        return new CaptureRequest(ContractVersion.Current, new SourceIdentity(clock.Anchor.SessionId, sourceId, new StreamEpoch(4)),
            clock.Anchor, new CaptureSourceSelection(kind, "synthetic-default"), new AudioBufferPolicy(2_000_000, maximumBytes));
    }

    private sealed class FakeClock : ISessionClock
    {
        private long _elapsed;
        public SessionClockAnchor Anchor { get; } = new(
            new SessionId(Guid.Parse("11111111-1111-1111-1111-111111111111")),
            new TimelinePosition(new TimelineEpochId(Guid.Parse("44444444-4444-4444-4444-444444444444")), new SessionOffset(0)),
            new UtcTimestamp(new DateTimeOffset(2026, 11, 6, 0, 0, 0, TimeSpan.Zero)));
        public TimelinePosition GetPosition() => new(Anchor.Origin.TimelineEpoch, new SessionOffset(1_000_000 + Interlocked.Add(ref _elapsed, 1_000)));
        public UtcTimestamp GetUtcNow() => Anchor.UtcAtOrigin;
    }

    private sealed class FakeFactory : IAudioCaptureBackendFactory
    {
        public FakeBackend Backend { get; init; } = new();
        public Exception? OpenFailure { get; init; }
        public int OpenCount { get; private set; }
        public CaptureSourceSelection? LastSelection { get; private set; }
        public IAudioCaptureBackend Open(CaptureSourceSelection selection)
        {
            OpenCount++;
            LastSelection = selection;
            if (OpenFailure is not null) { throw OpenFailure; }
            return Backend;
        }
    }

    private sealed class FakeBackend : IAudioCaptureBackend
    {
        private CapturePacketCallback? _packet;
        private Action<Exception?>? _stopped;
        private int _starting;
        public AudioFormat Format { get; } = new(16_000, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian);
        public Exception? StartFailure { get; init; }
        public Exception? StartStopError { get; init; }
        public Action? OnStart { get; init; }
        public Action? OnDispose { get; init; }
        public int StopCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool CleanupDuringStart { get; private set; }
        public void Start(CapturePacketCallback packet, Action<Exception?> stopped)
        {
            Volatile.Write(ref _starting, 1);
            try
            {
                _packet = packet;
                _stopped = stopped;
                if (StartFailure is not null) { throw StartFailure; }
                if (StartStopError is not null) { stopped(StartStopError); }
                OnStart?.Invoke();
            }
            finally { Volatile.Write(ref _starting, 0); }
        }
        public void RequestStop() { CleanupDuringStart |= Volatile.Read(ref _starting) != 0; StopCount++; _stopped?.Invoke(null); }
        public void Dispose() { CleanupDuringStart |= Volatile.Read(ref _starting) != 0; OnDispose?.Invoke(); DisposeCount++; GC.SuppressFinalize(this); }
        public void Deliver(byte[] data, int sampleCount, AudioFrameAttributes flags, ulong position, ulong qpc) =>
            (_packet ?? throw new InvalidOperationException("Capture was not started."))(data, sampleCount, flags, position, qpc);
    }

    private sealed class TrackingPool : MemoryPool<byte>
    {
        private int _rented;
        private int _returned;
        private int _returnedDirty;
        public override int MaxBufferSize => int.MaxValue;
        public int RentedCount => Volatile.Read(ref _rented);
        public int ReturnedCount => Volatile.Read(ref _returned);
        public int ReturnedDirtyCount => Volatile.Read(ref _returnedDirty);
        public Action? OnRent { get; init; }
        public override IMemoryOwner<byte> Rent(int minBufferSize = -1)
        {
            Interlocked.Increment(ref _rented);
            OnRent?.Invoke();
            var buffer = new byte[Math.Max(1, minBufferSize)];
            return new Owner(buffer, () =>
            {
                if (buffer.Any(static value => value != 0)) { Interlocked.Increment(ref _returnedDirty); }
                Interlocked.Increment(ref _returned);
            });
        }
        protected override void Dispose(bool disposing) { }

        private sealed class Owner(byte[] buffer, Action returned) : IMemoryOwner<byte>
        {
            private byte[]? _buffer = buffer;
            public Memory<byte> Memory => _buffer ?? throw new ObjectDisposedException(nameof(Owner));
            public void Dispose()
            {
                if (Interlocked.Exchange(ref _buffer, null) is not null) { returned(); }
                GC.SuppressFinalize(this);
            }
        }
    }
}
