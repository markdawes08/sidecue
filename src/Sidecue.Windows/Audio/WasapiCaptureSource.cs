using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Channels;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;

namespace Sidecue.Windows.Audio;

/// <summary>Opens independently owned microphone or render-loopback capture runs.</summary>
public sealed class WasapiCaptureSource : IAudioCaptureSource
{
    private readonly ISessionClock _clock;
    private readonly IAudioCaptureBackendFactory _factory;
    private readonly MemoryPool<byte> _pool;

    public WasapiCaptureSource(ISessionClock clock, IAudioCaptureBackendFactory factory)
        : this(clock, factory, MemoryPool<byte>.Shared) { }

    public WasapiCaptureSource(ISessionClock clock, IAudioCaptureBackendFactory factory, MemoryPool<byte> pool)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(pool);
        _clock = clock;
        _factory = factory;
        _pool = pool;
    }

    public async Task<IAudioCaptureRun> OpenAsync(CaptureRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.ClockAnchor != _clock.Anchor)
        {
            throw new ArgumentException("Capture must use the injected session clock anchor.", nameof(request));
        }
        ct.ThrowIfCancellationRequested();

        // Native construction captures SynchronizationContext. Open and start on
        // the pool so teardown never depends on the caller's WPF dispatcher.
        return await Task.Run(async () =>
        {
            ct.ThrowIfCancellationRequested();
            var backend = _factory.Open(request.Selection);
            CaptureRun? run = null;
            try
            {
                ct.ThrowIfCancellationRequested();
                run = new CaptureRun(request, _clock, backend, _pool);
                run.Start();
                ct.ThrowIfCancellationRequested();
                return (IAudioCaptureRun)run;
            }
            catch
            {
                if (run is not null) { await run.DisposeAsync().ConfigureAwait(false); }
                else { backend.Dispose(); }
                throw;
            }
        }, CancellationToken.None).ConfigureAwait(false);
    }

    private sealed class CaptureRun : IAudioCaptureRun
    {
        private const int StatusCapacity = 16;
        private const int MaximumEvictionsPerPacket = 16;
        private const long QpcTicksPerSecond = 10_000_000;
        private readonly CaptureRequest _request;
        private readonly ISessionClock _clock;
        private readonly IAudioCaptureBackend _backend;
        private readonly MemoryPool<byte> _pool;
        private readonly AudioFormat _format;
        private readonly RawClockPosition? _qpcAtOrigin;
        private readonly Channel<OwnedAudioFrame> _frames = Channel.CreateUnbounded<OwnedAudioFrame>(
            new UnboundedChannelOptions { SingleReader = false, SingleWriter = false, AllowSynchronousContinuations = false });
        private readonly Channel<CaptureStatusEvent> _statuses = Channel.CreateBounded<CaptureStatusEvent>(
            new BoundedChannelOptions(StatusCapacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false, AllowSynchronousContinuations = false });
        private readonly TaskCompletionSource _cleanup = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _disposeCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _startCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CaptureStatusEvent? _terminalStatus;
        private long _queuedBytes;
        private long _queuedDuration;
        private long _sequence;
        private ulong? _lastMappedQpc;
        private int _failureHasUnreportedLoss;
        private int _failureGapReason = -1;
        private int _accepting = 1;
        private int _callbackActive;
        private int _cleanupStarted;
        private int _allowEndInFlight;
        private int _disposed;
        private int _readerStarted;
        private int _discardQueued;

        internal CaptureRun(CaptureRequest request, ISessionClock clock, IAudioCaptureBackend backend, MemoryPool<byte> pool)
        {
            _request = request;
            _clock = clock;
            _backend = backend;
            _pool = pool;
            _format = backend.Format;
            // This relation is opt-in. An arbitrary ISessionClock is not
            // assumed to use QPC or to advance at its rate.
            _qpcAtOrigin = (clock as IQpcSessionClock)?.QpcAtOrigin;
        }

        public CaptureStatusEvent? TerminalStatus => Volatile.Read(ref _terminalStatus);

        internal void Start()
        {
            try
            {
                _backend.Start(OnPacket, OnBackendStopped);
                if (Volatile.Read(ref _cleanupStarted) == 0 && TerminalStatus is null)
                {
                    PublishStatus(new CaptureStatusEvent(_request.Version, _request.Source, GetPosition(), CaptureState.Live));
                }
            }
            finally { _startCompleted.TrySetResult(); }
        }

        public async IAsyncEnumerable<OwnedAudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            if (Interlocked.Exchange(ref _readerStarted, 1) != 0)
            {
                throw new InvalidOperationException("A capture run supports one frame consumer.");
            }
            while (await _frames.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                while (_frames.Reader.TryRead(out var frame))
                {
                    RemoveFromAccounting(frame);
                    if (Volatile.Read(ref _discardQueued) != 0)
                    {
                        if (TerminalStatus?.State == CaptureState.Failed) { RememberFailureGap(GapReason.CaptureFailure); }
                        frame.Dispose();
                        continue;
                    }
                    yield return frame;
                }
            }
        }

        public async IAsyncEnumerable<CaptureStatusEvent> ReadStatusAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var status in _statuses.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                yield return status;
            }
        }

        public async Task StopAsync(CaptureStopReason reason, CancellationToken ct)
        {
            if (!Enum.IsDefined(reason)) { throw new ArgumentOutOfRangeException(nameof(reason)); }
            BeginCleanup(reason);
            // Cancellation cancels only this wait, never the already-issued stop.
            await _cleanup.Task.WaitAsync(ct).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.CompareExchange(ref _disposed, 1, 0) == 0)
            {
                try
                {
                    Volatile.Write(ref _discardQueued, 1);
                    BeginCleanup(CaptureStopReason.Pause);
                    await _cleanup.Task.ConfigureAwait(false);
                    DrainFrames(recordFailureLoss: TerminalStatus?.State == CaptureState.Failed);
                    RefreshFailureGap();
                    _disposeCompleted.TrySetResult();
                }
                catch (Exception exception) { _disposeCompleted.TrySetException(exception); }
            }
            await _disposeCompleted.Task.ConfigureAwait(false);
        }

        private void OnPacket(ReadOnlySpan<byte> data, int sampleCount, AudioFrameAttributes flags,
            ulong devicePosition, ulong qpc100Nanoseconds)
        {
            if (Volatile.Read(ref _accepting) == 0) { return; }
            // One backend has one native callback thread. Fail closed if an
            // implementation violates that contract; never block a callback.
            if (Interlocked.Exchange(ref _callbackActive, 1) != 0)
            {
                RememberFailureGap(GapReason.CaptureFailure);
                Fail(EventErrorCategory.Unknown);
                return;
            }
            try
            {
                if (!CanEnqueueInFlight())
                {
                    if (TerminalStatus?.State == CaptureState.Failed) { RememberFailureGap(GapReason.CaptureFailure); }
                    return;
                }
                if (sampleCount <= 0 || data.Length != checked(sampleCount * _format.BytesPerFrame))
                {
                    RememberFailureGap(GapReason.CaptureFailure);
                    Fail(EventErrorCategory.CorruptData);
                    return;
                }

                var position = GetPosition();
                long duration = checked((long)(((Int128)sampleCount * 1_000_000) / _format.SampleRate));
                TimelineInterval interval;
                if (QpcPacketMapper.TryMap(_request.ClockAnchor, _qpcAtOrigin, position, qpc100Nanoseconds,
                    duration, flags, _lastMappedQpc, out var mapped))
                {
                    interval = mapped!;
                    _lastMappedQpc = qpc100Nanoseconds;
                }
                else
                {
                    // A clock without an explicit correlation or a bad native timestamp
                    // falls back to callback time, labeled as a session estimate.
                    long end = position.Offset.Value;
                    if (end < checked(_request.ClockAnchor.Origin.Offset.Value + duration))
                    {
                        // Do not invent a future timestamp for a pre-origin packet.
                        RememberFailureGap(GapReason.CaptureFailure);
                        Fail(EventErrorCategory.Unknown);
                        return;
                    }
                    interval = new TimelineInterval(
                        new TimelinePosition(position.TimelineEpoch, new SessionOffset(end - duration)),
                        position);
                }
                var startPosition = interval.Start;
                var deviceClock = devicePosition <= long.MaxValue ? new RawClockPosition((long)devicePosition, _format.SampleRate) : null;
                var qpcClock = qpc100Nanoseconds <= long.MaxValue ? new RawClockPosition((long)qpc100Nanoseconds, QpcTicksPerSecond) : null;
                // The native flag stays independent: an estimate does not itself
                // mean WASAPI reported a timestamp error.
                flags |= AudioFrameAttributes.SessionTimestampEstimated;
                var metadata = new AudioFrameMetadata(_request.Version, _request.Source,
                    new FrameSequence(_sequence++), interval, _format, sampleCount, flags, deviceClock, qpcClock);

                if ((flags & AudioFrameAttributes.Discontinuity) != 0)
                {
                    var unknown = new CaptureGap(_request.Version, _request.Source,
                        new TimelineInterval(startPosition, startPosition), GapReason.CaptureDiscontinuity, TimingCertainty.Unknown);
                    if (!PublishStatus(new CaptureStatusEvent(_request.Version, _request.Source,
                        startPosition, CaptureState.Degraded, gap: unknown)))
                    {
                        RememberFailureGap(GapReason.CaptureFailure);
                        Fail(EventErrorCategory.Unknown, unknown);
                        return;
                    }
                }

                if (metadata.PayloadByteCount > _request.BufferPolicy.MaximumBytes ||
                    duration > _request.BufferPolicy.MaximumDurationMicroseconds)
                {
                    ReportDropped(interval);
                    return;
                }

                // The WASAPI span is borrowed only for this callback.
                IMemoryOwner<byte>? unwrappedOwner = _pool.Rent(metadata.PayloadByteCount);
                IMemoryOwner<byte>? owner = null;
                OwnedAudioFrame? frame = null;
                try
                {
                    owner = new ClearingMemoryOwner(unwrappedOwner, metadata.PayloadByteCount);
                    unwrappedOwner = null;
                    data.CopyTo(owner.Memory.Span);
                    frame = new OwnedAudioFrame(metadata, owner, metadata.PayloadByteCount);
                    owner = null;
                    Enqueue(frame);
                    frame = null;
                }
                finally
                {
                    frame?.Dispose();
                    owner?.Dispose();
                    if (unwrappedOwner is not null)
                    {
                        try { unwrappedOwner.Memory.Span[..Math.Min(metadata.PayloadByteCount, unwrappedOwner.Memory.Length)].Clear(); }
                        finally { unwrappedOwner.Dispose(); }
                    }
                }
            }
            catch (Exception)
            {
                RememberFailureGap(GapReason.CaptureFailure);
                Fail(EventErrorCategory.Unknown);
            }
            finally { Volatile.Write(ref _callbackActive, 0); }
        }

        private void Enqueue(OwnedAudioFrame frame)
        {
            int bytes = frame.Metadata.PayloadByteCount;
            long duration = frame.Metadata.Interval.DurationMicroseconds;
            var evictions = 0;
            while (CanEnqueueInFlight() &&
                (Interlocked.Read(ref _queuedBytes) + bytes > _request.BufferPolicy.MaximumBytes ||
                 Interlocked.Read(ref _queuedDuration) + duration > _request.BufferPolicy.MaximumDurationMicroseconds))
            {
                if (evictions == MaximumEvictionsPerPacket)
                {
                    RememberFailureGap(GapReason.QueueOverflow);
                    frame.Dispose();
                    Fail(EventErrorCategory.Unknown);
                    return;
                }
                if (!_frames.Reader.TryRead(out var discarded))
                {
                    ReportDropped(frame.Metadata.Interval);
                    frame.Dispose();
                    return;
                }
                RemoveFromAccounting(discarded);
                var discardedInterval = discarded.Metadata.Interval;
                try { discarded.Dispose(); }
                finally { ReportDropped(discardedInterval); }
                evictions++;
            }

            if (!CanEnqueueInFlight())
            {
                if (TerminalStatus?.State == CaptureState.Failed) { RememberFailureGap(GapReason.QueueOverflow); }
                frame.Dispose();
                return;
            }
            Interlocked.Add(ref _queuedBytes, bytes);
            Interlocked.Add(ref _queuedDuration, duration);
            if (!_frames.Writer.TryWrite(frame))
            {
                RemoveFromAccounting(frame);
                if (TerminalStatus?.State == CaptureState.Failed) { RememberFailureGap(GapReason.QueueOverflow); }
                frame.Dispose();
            }
        }

        private void ReportDropped(TimelineInterval interval)
        {
            var gap = new CaptureGap(_request.Version, _request.Source, interval, GapReason.QueueOverflow, TimingCertainty.Estimated);
            if (!PublishStatus(new CaptureStatusEvent(_request.Version, _request.Source, interval.End, CaptureState.Degraded, gap: gap)))
            {
                // Keep this last gap in the terminal status when the separate
                // status path fills; stopping avoids silently losing more gaps.
                RememberFailureGap(GapReason.QueueOverflow);
                Fail(EventErrorCategory.Unknown, gap);
            }
        }

        private bool PublishStatus(CaptureStatusEvent status) => _statuses.Writer.TryWrite(status);

        private void OnBackendStopped(Exception? error)
        {
            if (error is not null) { Fail(ClassifyBackendError(error)); }
            else if (Volatile.Read(ref _cleanupStarted) == 0) { Fail(EventErrorCategory.DeviceUnavailable); }
        }

        private static EventErrorCategory ClassifyBackendError(Exception exception) =>
            exception is UnauthorizedAccessException ||
            exception is COMException { HResult: unchecked((int)0x80070005) }
                ? EventErrorCategory.PermissionDenied
                : EventErrorCategory.DeviceUnavailable;

        private void Fail(EventErrorCategory error, CaptureGap? gap = null)
        {
            var position = gap?.Interval.End ?? SafePosition();
            Interlocked.CompareExchange(ref _terminalStatus,
                new CaptureStatusEvent(_request.Version, _request.Source, position, CaptureState.Failed, error, gap), null);
            BeginCleanup(CaptureStopReason.Recovery);
        }

        private void BeginCleanup(CaptureStopReason reason)
        {
            if (Interlocked.CompareExchange(ref _cleanupStarted, 1, 0) != 0) { return; }
            if (reason == CaptureStopReason.End) { Volatile.Write(ref _allowEndInFlight, 1); }
            Volatile.Write(ref _accepting, 0);
            if (reason == CaptureStopReason.Pause) { Volatile.Write(ref _discardQueued, 1); }
            if (reason != CaptureStopReason.End) { _frames.Writer.TryComplete(); }
            _ = Task.Run(async () =>
            {
                // A backend can report an immediate failure inside Start.
                // Its cleanup must not race the still-executing Start call.
                await _startCompleted.Task.ConfigureAwait(false);
                try
                {
                    try { _backend.RequestStop(); }
                    finally { _backend.Dispose(); }
                    if (reason == CaptureStopReason.Pause) { DrainFrames(recordFailureLoss: TerminalStatus?.State == CaptureState.Failed); }
                    var state = reason == CaptureStopReason.Pause ? CaptureState.Paused : CaptureState.Stopped;
                    Interlocked.CompareExchange(ref _terminalStatus,
                        new CaptureStatusEvent(_request.Version, _request.Source, SafePosition(), state), null);
                }
                catch (Exception exception)
                {
                    Interlocked.CompareExchange(ref _terminalStatus,
                        new CaptureStatusEvent(_request.Version, _request.Source, SafePosition(), CaptureState.Failed, ClassifyBackendError(exception)), null);
                }
                finally
                {
                    // End retains a packet that entered before its cutoff. The
                    // backend join above finishes it before the writer closes.
                    _frames.Writer.TryComplete();
                    RefreshFailureGap();
                    var terminal = TerminalStatus;
                    if (terminal is not null) { _statuses.Writer.TryWrite(terminal); }
                    _statuses.Writer.TryComplete();
                    _cleanup.TrySetResult();
                }
            });
        }

        private bool CanEnqueueInFlight() =>
            TerminalStatus?.State != CaptureState.Failed &&
            (Volatile.Read(ref _accepting) != 0 || Volatile.Read(ref _allowEndInFlight) != 0);

        private void DrainFrames(bool recordFailureLoss)
        {
            while (_frames.Reader.TryRead(out var frame))
            {
                RemoveFromAccounting(frame);
                if (recordFailureLoss || TerminalStatus?.State == CaptureState.Failed) { RememberFailureGap(GapReason.CaptureFailure); }
                try { frame.Dispose(); }
                catch (Exception) { Fail(EventErrorCategory.Unknown); }
            }
        }

        private void RememberFailureGap(GapReason reason)
        {
            int previous = Interlocked.CompareExchange(ref _failureGapReason, (int)reason, -1);
            if (previous != -1 && previous != (int)reason)
            {
                Volatile.Write(ref _failureGapReason, (int)GapReason.CaptureFailure);
            }
            Interlocked.Exchange(ref _failureHasUnreportedLoss, 1);
        }

        private void RefreshFailureGap()
        {
            var terminal = TerminalStatus;
            if (terminal?.State != CaptureState.Failed || Volatile.Read(ref _failureHasUnreportedLoss) == 0) { return; }
            var endPosition = SafePosition();
            if (terminal.Gap is not null && terminal.Gap.Interval.End.Offset.Value > endPosition.Offset.Value)
            {
                endPosition = terminal.Gap.Interval.End;
            }
            var interval = new TimelineInterval(_request.ClockAnchor.Origin, endPosition);
            // Conservative envelope: the known losses may be disjoint and are
            // not asserted to cover this entire interval.
            var gap = new CaptureGap(_request.Version, _request.Source, interval,
                (GapReason)Volatile.Read(ref _failureGapReason), TimingCertainty.Unknown);
            var position = terminal.Position.Offset.Value >= interval.End.Offset.Value ? terminal.Position : interval.End;
            Volatile.Write(ref _terminalStatus, new CaptureStatusEvent(_request.Version, _request.Source,
                position, CaptureState.Failed, terminal.Error, gap));
        }

        private sealed class ClearingMemoryOwner(IMemoryOwner<byte> inner, int length) : IMemoryOwner<byte>
        {
            private IMemoryOwner<byte>? _inner = inner;

            public Memory<byte> Memory => Volatile.Read(ref _inner)?.Memory ?? throw new ObjectDisposedException(nameof(ClearingMemoryOwner));

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _inner, null);
                if (owner is not null)
                {
                    try { owner.Memory.Span[..Math.Min(length, owner.Memory.Length)].Clear(); }
                    finally { owner.Dispose(); }
                }
                GC.SuppressFinalize(this);
            }
        }

        private void RemoveFromAccounting(OwnedAudioFrame frame)
        {
            Interlocked.Add(ref _queuedBytes, -frame.Metadata.PayloadByteCount);
            Interlocked.Add(ref _queuedDuration, -frame.Metadata.Interval.DurationMicroseconds);
        }

        private TimelinePosition GetPosition()
        {
            var position = _clock.GetPosition();
            if (position.TimelineEpoch != _request.ClockAnchor.Origin.TimelineEpoch ||
                position.Offset.Value < _request.ClockAnchor.Origin.Offset.Value)
            {
                throw new InvalidOperationException("The session clock returned a different timeline or a position before its origin.");
            }
            return position;
        }

        private TimelinePosition SafePosition()
        {
            try { return GetPosition(); }
            catch (Exception) { return _request.ClockAnchor.Origin; }
        }
    }
}
