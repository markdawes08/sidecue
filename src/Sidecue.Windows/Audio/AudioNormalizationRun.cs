using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;

namespace Sidecue.Windows.Audio;

/// <summary>Owns one capture run and normalizes it independently on a background worker.</summary>
public sealed class AudioNormalizationRun : IAudioCaptureRun
{
    private const int TargetRate = 16_000;
    private const int PreferredSamples = 1_600;
    private const int StatusCapacity = 16;
    private readonly IAudioCaptureRun _source;
    private readonly CaptureRequest _request;
    private readonly AudioBufferPolicy _outputPolicy;
    private readonly int _chunkSamples;
    private readonly Channel<OwnedAudioFrame> _frames = Channel.CreateUnbounded<OwnedAudioFrame>(
        new UnboundedChannelOptions { SingleWriter = true, SingleReader = false, AllowSynchronousContinuations = false });
    private readonly object _frameGate = new();
    private readonly object _statusGate = new();
    private readonly LinkedList<CaptureStatusEvent> _statuses = new();
    private readonly SemaphoreSlim _statusSignal = new(0, 1);
    private readonly object _lifetimeGate = new();
    private readonly Task _worker;
    private Task? _stopping;
    private Task? _disposing;
    private CaptureStatusEvent? _terminal;
    private CaptureStatusEvent? _sourceTerminal;
    private long _queuedBytes;
    private long _queuedDuration;
    private long _lastObservedOffset;
    private bool _statusClosed;
    private int _readerStarted;
    private int _discard;
    private int _disposed;
    private int _processingFailed;

    public AudioNormalizationRun(IAudioCaptureRun source, CaptureRequest request, AudioBufferPolicy outputPolicy)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(outputPolicy);
        var byDuration = (long)((Int128)outputPolicy.MaximumDurationMicroseconds * TargetRate / 1_000_000);
        var chunkSamples = Math.Min(PreferredSamples, Math.Min(byDuration, outputPolicy.MaximumBytes / 2L));
        if (chunkSamples < 1) { throw new ArgumentException("Output limits must hold at least one 16-bit sample.", nameof(outputPolicy)); }
        _source = source;
        _request = request;
        _outputPolicy = outputPolicy;
        _chunkSamples = (int)chunkSamples;
        _lastObservedOffset = request.ClockAnchor.Origin.Offset.Value;
        _worker = Task.Run(RunAsync);
    }

    public CaptureStatusEvent? TerminalStatus => Volatile.Read(ref _terminal);

    public async IAsyncEnumerable<OwnedAudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _readerStarted, 1) != 0) { throw new InvalidOperationException("Only one normalized frame consumer is supported."); }
        while (await _frames.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (true)
            {
                OwnedAudioFrame? frame;
                lock (_frameGate)
                {
                    if (!_frames.Reader.TryRead(out frame)) { break; }
                    RemoveAccounting(frame);
                }
                if (Volatile.Read(ref _discard) != 0) { frame.Dispose(); continue; }
                yield return frame;
            }
        }
    }

    public async IAsyncEnumerable<CaptureStatusEvent> ReadStatusAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (true)
        {
            CaptureStatusEvent? next = null;
            bool closed;
            lock (_statusGate)
            {
                if (_statuses.First is { } item) { next = item.Value; _statuses.RemoveFirst(); }
                closed = _statusClosed && _statuses.Count == 0;
            }
            if (next is not null) { yield return next; continue; }
            if (closed) { yield break; }
            await _statusSignal.WaitAsync(ct).ConfigureAwait(false);
        }
    }

    public async Task StopAsync(CaptureStopReason reason, CancellationToken ct)
    {
        if (!Enum.IsDefined(reason)) { throw new ArgumentOutOfRangeException(nameof(reason)); }
        Task stop;
        lock (_lifetimeGate)
        {
            if (_stopping is null)
            {
                if (reason == CaptureStopReason.Pause) { Volatile.Write(ref _discard, 1); }
                _stopping = StopCoreAsync(reason);
            }
            stop = _stopping;
        }
        await stop.WaitAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            _disposing ??= DisposeCoreAsync();
            return new ValueTask(_disposing);
        }
    }

    private async Task StopCoreAsync(CaptureStopReason reason)
    {
        try { await _source.StopAsync(reason, CancellationToken.None).ConfigureAwait(false); }
        finally { await _worker.ConfigureAwait(false); }
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, 1);
        Volatile.Write(ref _discard, 1);
        try { await StopAsync(CaptureStopReason.Pause, CancellationToken.None).ConfigureAwait(false); }
        finally
        {
            try { await _source.DisposeAsync().ConfigureAwait(false); }
            finally { Drain(); }
        }
    }

    private async Task RunAsync()
    {
        var statusTask = CopyStatusesAsync();
        using var processor = new StreamingAudioNormalizer(_request, _chunkSamples, Enqueue, PublishGap);
        try
        {
            await foreach (var frame in _source.ReadFramesAsync(CancellationToken.None).ConfigureAwait(false))
            {
                using (frame)
                {
                    if (frame.Metadata.Interval.TimelineEpoch == _request.ClockAnchor.Origin.TimelineEpoch)
                    {
                        ObserveOffset(frame.Metadata.Interval.End.Offset.Value);
                    }
                    if (Volatile.Read(ref _processingFailed) != 0) { SetFailure(null); }
                    else if (Volatile.Read(ref _discard) == 0) { processor.Process(frame); }
                }
            }
            if (Volatile.Read(ref _discard) == 0 && Volatile.Read(ref _processingFailed) == 0) { processor.Complete(); }
        }
        catch (Exception exception)
        {
            SetFailure((exception as StatusPathFullException)?.Gap);
            try { await _source.StopAsync(CaptureStopReason.Recovery, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { /* The terminal failure is already retained. */ }
        }
        finally
        {
            try { await statusTask.ConfigureAwait(false); }
            catch (Exception exception) { SetFailure((exception as StatusPathFullException)?.Gap); }
            _frames.Writer.TryComplete();
            var terminal = TerminalStatus ?? _sourceTerminal ?? _source.TerminalStatus;
            if (terminal is not null) { Interlocked.CompareExchange(ref _terminal, terminal, null); }
            lock (_statusGate)
            {
                // Reserve one extra slot for the final state. Never evict a gap
                // merely because the bounded status queue is full.
                if (TerminalStatus is { } final) { _statuses.AddLast(final); }
                _statusClosed = true;
                SignalStatus();
            }
        }
    }

    private async Task CopyStatusesAsync()
    {
        try
        {
            await foreach (var status in _source.ReadStatusAsync(CancellationToken.None).ConfigureAwait(false))
            {
                if (status.Source != _request.Source || status.Position.TimelineEpoch != _request.ClockAnchor.Origin.TimelineEpoch)
                {
                    throw new InvalidOperationException("Capture status source or timeline changed.");
                }
                if (status.State is CaptureState.Failed or CaptureState.Stopped or CaptureState.Paused)
                {
                    _sourceTerminal = status;
                    if (status.State == CaptureState.Failed) { Interlocked.CompareExchange(ref _terminal, status, null); }
                    continue;
                }
                PublishStatus(status);
            }
        }
        catch (Exception exception)
        {
            SetFailure((exception as StatusPathFullException)?.Gap);
            try { await _source.StopAsync(CaptureStopReason.Recovery, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception) { /* Preserve the original failure. */ }
        }
    }

    private void Enqueue(OwnedAudioFrame frame)
    {
        lock (_frameGate)
        {
            if (Volatile.Read(ref _discard) != 0) { frame.Dispose(); return; }
            while (_queuedBytes + frame.Metadata.PayloadByteCount > _outputPolicy.MaximumBytes ||
                   _queuedDuration + frame.Metadata.Interval.DurationMicroseconds > _outputPolicy.MaximumDurationMicroseconds)
            {
                if (!_frames.Reader.TryRead(out var dropped))
                {
                    PublishGap(new CaptureGap(_request.Version, _request.Source, frame.Metadata.Interval,
                        GapReason.QueueOverflow, TimingCertainty.Estimated));
                    frame.Dispose();
                    return;
                }
                RemoveAccounting(dropped);
                var interval = dropped.Metadata.Interval;
                dropped.Dispose();
                PublishGap(new CaptureGap(_request.Version, _request.Source, interval, GapReason.QueueOverflow, TimingCertainty.Estimated));
            }
            _queuedBytes += frame.Metadata.PayloadByteCount;
            _queuedDuration += frame.Metadata.Interval.DurationMicroseconds;
            if (!_frames.Writer.TryWrite(frame)) { RemoveAccounting(frame); frame.Dispose(); }
        }
    }

    private void PublishGap(CaptureGap gap) => PublishStatus(new CaptureStatusEvent(_request.Version, _request.Source,
        gap.Interval.End, CaptureState.Degraded, gap: gap));

    private void PublishStatus(CaptureStatusEvent status)
    {
        bool overflow = false;
        lock (_statusGate)
        {
            if (_statusClosed) { return; }
            if (status.Gap is { } gap && _statuses.Last?.Value.Gap is { } previous && previous.Reason == gap.Reason &&
                previous.Interval.Start.Offset.Value <= gap.Interval.End.Offset.Value &&
                gap.Interval.Start.Offset.Value <= previous.Interval.End.Offset.Value)
            {
                var start = previous.Interval.Start.Offset.Value <= gap.Interval.Start.Offset.Value ? previous.Interval.Start : gap.Interval.Start;
                var end = previous.Interval.End.Offset.Value >= gap.Interval.End.Offset.Value ? previous.Interval.End : gap.Interval.End;
                var merged = new CaptureGap(_request.Version, _request.Source, new TimelineInterval(start, end), gap.Reason, TimingCertainty.Unknown);
                _statuses.Last.Value = new CaptureStatusEvent(_request.Version, _request.Source, end, CaptureState.Degraded, gap: merged);
            }
            else if (_statuses.Count < StatusCapacity) { _statuses.AddLast(status); }
            else if (status.Gap is not null) { overflow = true; }
            // Terminal is retained separately; redundant non-gap health can be coalesced.
            SignalStatus();
        }
        if (overflow) { throw new StatusPathFullException(status.Gap!); }
    }

    private void SetFailure(CaptureGap? gap)
    {
        Volatile.Write(ref _processingFailed, 1);
        if (gap is not null) { ObserveOffset(gap.Interval.End.Offset.Value); }
        while (true)
        {
            var previous = TerminalStatus;
            var end = Math.Max(Interlocked.Read(ref _lastObservedOffset), previous?.Gap?.Interval.End.Offset.Value ?? 0);
            end = Math.Max(end, _request.ClockAnchor.Origin.Offset.Value);
            var position = new TimelinePosition(_request.ClockAnchor.Origin.TimelineEpoch, new SessionOffset(end));
            var envelope = new CaptureGap(_request.Version, _request.Source,
                new TimelineInterval(_request.ClockAnchor.Origin, position), GapReason.CaptureFailure, TimingCertainty.Unknown);
            var failure = new CaptureStatusEvent(_request.Version, _request.Source, position,
                CaptureState.Failed, EventErrorCategory.Unknown, envelope);
            if (ReferenceEquals(Interlocked.CompareExchange(ref _terminal, failure, previous), previous)) { return; }
        }
    }

    private void ObserveOffset(long offset)
    {
        var current = Interlocked.Read(ref _lastObservedOffset);
        while (offset > current)
        {
            var previous = Interlocked.CompareExchange(ref _lastObservedOffset, offset, current);
            if (previous == current) { return; }
            current = previous;
        }
    }

    private sealed class StatusPathFullException(CaptureGap gap) : Exception("The bounded capture status path is full.")
    {
        internal CaptureGap Gap { get; } = gap;
    }

    private void SignalStatus()
    {
        if (_statusSignal.CurrentCount == 0) { _statusSignal.Release(); }
    }

    private void Drain()
    {
        lock (_frameGate)
        {
            while (_frames.Reader.TryRead(out var frame)) { RemoveAccounting(frame); frame.Dispose(); }
        }
    }

    private void RemoveAccounting(OwnedAudioFrame frame)
    {
        _queuedBytes -= frame.Metadata.PayloadByteCount;
        _queuedDuration -= frame.Metadata.Interval.DurationMicroseconds;
    }
}
