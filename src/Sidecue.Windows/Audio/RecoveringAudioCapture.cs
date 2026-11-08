using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Threading.Channels;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;

namespace Sidecue.Windows.Audio;

/// <summary>Signals relevant system-default endpoint changes without exposing device identifiers.</summary>
public interface IAudioEndpointChangeMonitor : IDisposable
{
    event Action<CaptureSourceKind>? DefaultEndpointChanged;
}

/// <summary>
/// Owns a sequence of normalized capture runs for one selected source. Each reopened run has
/// a distinct stream epoch. The caller owns the monitor and must dispose it after this session.
/// </summary>
public sealed class RecoveringAudioCapture : IAsyncDisposable
{
    private const int StatusCapacity = 64;
    private const int MaximumOpenAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan StopDeadline = TimeSpan.FromSeconds(2);
    private readonly IAudioCaptureSource _source;
    private readonly CaptureRequest _request;
    private readonly ISessionClock _clock;
    private readonly IAudioEndpointChangeMonitor _monitor;
    private readonly Channel<bool> _changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite, SingleReader = true, AllowSynchronousContinuations = false
    });
    private readonly Channel<OwnedAudioFrame> _frames = Channel.CreateUnbounded<OwnedAudioFrame>(new UnboundedChannelOptions
    {
        SingleWriter = true, AllowSynchronousContinuations = false
    });
    // One additional slot is reserved for the final status after loss saturation.
    private readonly Channel<CaptureStatusEvent> _statuses = Channel.CreateBounded<CaptureStatusEvent>(new BoundedChannelOptions(StatusCapacity + 1)
    {
        FullMode = BoundedChannelFullMode.Wait, AllowSynchronousContinuations = false
    });
    private readonly object _frameGate = new();
    private readonly object _statusGate = new();
    private readonly object _lifetimeGate = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly Task _worker;
    private Task? _dispose;
    private CaptureStatusEvent? _terminal;
    private CaptureStopReason _stopReason = CaptureStopReason.End;
    private long _queuedBytes;
    private long _queuedDuration;
    private long _generation;
    private int _stopRequested;
    private int _frameReader;
    private int _statusReader;
    private int _disposed;

    public RecoveringAudioCapture(IAudioCaptureSource source, CaptureRequest request, ISessionClock clock,
        IAudioEndpointChangeMonitor monitor)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(monitor);
        if (clock.Anchor != request.ClockAnchor) { throw new ArgumentException("The capture clock anchor does not match.", nameof(clock)); }
        if (!string.Equals(request.Selection.SelectorId, NAudioCaptureBackendFactory.SystemDefaultSelector, StringComparison.Ordinal))
        {
            throw new NotSupportedException("Recovery supports only the explicit system-default selector.");
        }
        _source = source;
        _request = request;
        _clock = clock;
        _monitor = monitor;
        // Subscribe before starting the worker. A generation fences changes during OpenAsync.
        _monitor.DefaultEndpointChanged += OnDefaultChanged;
        _worker = Task.Run(WorkerAsync);
    }

    public CaptureStatusEvent? TerminalStatus => Volatile.Read(ref _terminal);

    public async IAsyncEnumerable<OwnedAudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _frameReader, 1) != 0) { throw new InvalidOperationException("Only one frame reader is supported."); }
        while (await _frames.Reader.WaitToReadAsync(ct).ConfigureAwait(false))
        {
            while (true)
            {
                OwnedAudioFrame? frame;
                lock (_frameGate)
                {
                    if (!_frames.Reader.TryRead(out frame)) { break; }
                    AccountRemoval(frame);
                }
                yield return frame;
            }
        }
    }

    public async IAsyncEnumerable<CaptureStatusEvent> ReadStatusAsync([EnumeratorCancellation] CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.Exchange(ref _statusReader, 1) != 0) { throw new InvalidOperationException("Only one status reader is supported."); }
        await foreach (var status in _statuses.Reader.ReadAllAsync(ct).ConfigureAwait(false)) { yield return status; }
    }

    public async Task StopAsync(CaptureStopReason reason, CancellationToken ct)
    {
        if (!Enum.IsDefined(reason)) { throw new ArgumentOutOfRangeException(nameof(reason)); }
        lock (_lifetimeGate)
        {
            if (Interlocked.Exchange(ref _stopRequested, 1) == 0)
            {
                _stopReason = reason;
                if (reason == CaptureStopReason.Pause)
                {
                    lock (_frameGate)
                    {
                        while (_frames.Reader.TryRead(out var frame)) { AccountRemoval(frame); frame.Dispose(); }
                    }
                }
                _stopping.Cancel();
            }
        }
        await _worker.WaitAsync(ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            _dispose ??= DisposeCoreAsync();
            return new ValueTask(_dispose);
        }
    }

    private async Task DisposeCoreAsync()
    {
        Volatile.Write(ref _disposed, 1);
        try { await StopAsync(CaptureStopReason.Pause, CancellationToken.None).ConfigureAwait(false); }
        finally
        {
            _monitor.DefaultEndpointChanged -= OnDefaultChanged;
            lock (_frameGate)
            {
                while (_frames.Reader.TryRead(out var frame)) { AccountRemoval(frame); frame.Dispose(); }
            }
            _stopping.Dispose();
        }
    }

    private void OnDefaultChanged(CaptureSourceKind kind)
    {
        if (kind != _request.Selection.Kind || Volatile.Read(ref _stopRequested) != 0) { return; }
        Interlocked.Increment(ref _generation);
        _changes.Writer.TryWrite(true);
    }

    private async Task WorkerAsync()
    {
        var currentSource = _request.Source;
        TimelinePosition? gapStart = null;
        SourceIdentity? gapSource = null;
        var attempts = 0;
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                while (_changes.Reader.TryRead(out _)) { }
                var generation = Interlocked.Read(ref _generation);
                var request = new CaptureRequest(_request.Version, currentSource, _request.ClockAnchor,
                    _request.Selection, _request.BufferPolicy);
                var attemptStarted = Position();
                IAudioCaptureRun raw;
                try
                {
                    var opening = _source.OpenAsync(request, _stopping.Token);
                    try { raw = await opening.WaitAsync(_stopping.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
                    {
                        // Some native opens cannot be interrupted. Dispose any late result.
                        _ = DisposeLateOpenAsync(opening);
                        break;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    var category = WasapiCaptureSource.ClassifyBackendError(exception);
                    if (category != EventErrorCategory.DeviceUnavailable || ++attempts >= MaximumOpenAttempts)
                    {
                        Fail(currentSource, category, gapStart);
                        break;
                    }
                    gapStart ??= attemptStarted;
                    gapSource ??= currentSource;
                    Publish(new CaptureStatusEvent(_request.Version, currentSource, Position(), CaptureState.Degraded, category));
                    currentSource = Next(currentSource);
                    await Task.Delay(RetryDelay, _stopping.Token).ConfigureAwait(false);
                    continue;
                }

                var transferred = false;
                var cleanupDeferred = false;
                AudioNormalizationRun? normalized = null;
                try
                {
                    if (_stopping.IsCancellationRequested)
                    {
                        if (!await StopWithinDeadlineAsync(raw, _stopReason).ConfigureAwait(false)) { cleanupDeferred = true; Fail(currentSource, EventErrorCategory.Unknown, gapStart); }
                        break;
                    }
                    if (generation != Interlocked.Read(ref _generation))
                    {
                        gapStart ??= attemptStarted;
                        gapSource ??= currentSource;
                        if (!await StopWithinDeadlineAsync(raw, CaptureStopReason.Recovery).ConfigureAwait(false))
                        {
                            cleanupDeferred = true;
                            Fail(currentSource, EventErrorCategory.Unknown, gapStart);
                            break;
                        }
                        currentSource = Next(currentSource);
                        if (++attempts >= MaximumOpenAttempts) { Fail(currentSource, EventErrorCategory.DeviceUnavailable, gapStart); break; }
                        continue;
                    }
                    normalized = new AudioNormalizationRun(raw, request, request.BufferPolicy);
                    transferred = true;
                    var startedAt = Stopwatch.GetTimestamp();
                    var state = new EpochState(currentSource, gapStart, gapSource);
                    var frameTask = PumpFramesAsync(normalized, state);
                    var statusTask = PumpStatusesAsync(normalized, state);
                    using var waiting = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
                    var changed = _changes.Reader.WaitToReadAsync(waiting.Token).AsTask();
                    var finished = await Task.WhenAny(frameTask, statusTask, changed).ConfigureAwait(false);
                    waiting.Cancel();
                    bool changeRequested = finished == changed && !_stopping.IsCancellationRequested;
                    if (changeRequested || _stopping.IsCancellationRequested || finished.IsFaulted || finished.IsCanceled)
                    {
                        if (!await StopWithinDeadlineAsync(normalized, _stopping.IsCancellationRequested ? _stopReason : CaptureStopReason.Recovery).ConfigureAwait(false))
                        {
                            cleanupDeferred = true;
                            _ = ObservePumpsAsync(frameTask, statusTask);
                            gapStart ??= state.LastEnd ?? attemptStarted;
                            gapSource ??= currentSource;
                            Fail(currentSource, EventErrorCategory.Unknown, gapStart);
                            break;
                        }
                    }
                    await Task.WhenAll(frameTask, statusTask).ConfigureAwait(false);
                    var terminal = normalized.TerminalStatus;
                    gapStart = state.GapStart;
                    gapSource = state.GapStart is null ? null : state.PreviousSource;
                    if (_stopping.IsCancellationRequested)
                    {
                        if (terminal is { State: CaptureState.Failed }) { SetTerminal(terminal); }
                        break;
                    }
                    if (terminal is { State: CaptureState.Failed } && terminal.Error != EventErrorCategory.DeviceUnavailable)
                    {
                        SetTerminal(terminal);
                        break;
                    }
                    if (!changeRequested && terminal is not { State: CaptureState.Failed, Error: EventErrorCategory.DeviceUnavailable })
                    {
                        if (terminal is { State: CaptureState.Failed }) { SetTerminal(terminal); }
                        else { Fail(currentSource, EventErrorCategory.Unknown, state.LastEnd); }
                        break;
                    }
                    if (terminal is { State: CaptureState.Failed })
                    {
                        // A recoverable epoch failure is not the terminal failure of the session.
                        Publish(new CaptureStatusEvent(terminal.Version, terminal.Source, terminal.Position,
                            CaptureState.Degraded, terminal.Error, terminal.Gap));
                    }
                    // One packet is not evidence of a stable endpoint. Reset the consecutive
                    // recovery budget only after two seconds of elapsed capture and some output.
                    if (state.LastEnd is not null && Stopwatch.GetElapsedTime(startedAt) >= TimeSpan.FromSeconds(2)) { attempts = 0; }
                    gapStart ??= state.LastEnd ?? attemptStarted;
                    gapSource ??= currentSource;
                    if (++attempts >= MaximumOpenAttempts) { Fail(currentSource, EventErrorCategory.DeviceUnavailable, gapStart); break; }
                    currentSource = Next(currentSource);
                    await Task.Delay(RetryDelay, _stopping.Token).ConfigureAwait(false);
                }
                finally
                {
                    if (!cleanupDeferred)
                    {
                        if (normalized is not null)
                        {
                            if (!await DisposeWithinDeadlineAsync(normalized).ConfigureAwait(false))
                            {
                                Fail(currentSource, EventErrorCategory.Unknown, gapStart);
                                _stopping.Cancel();
                            }
                        }
                        else if (!transferred)
                        {
                            if (!await DisposeWithinDeadlineAsync(raw).ConfigureAwait(false))
                            {
                                Fail(currentSource, EventErrorCategory.Unknown, gapStart);
                                _stopping.Cancel();
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
        catch (Exception) { Fail(currentSource, EventErrorCategory.Unknown, gapStart); }
        finally
        {
            var position = Position();
            if (gapStart is not null)
            {
                try { PublishGap(gapSource ?? currentSource, gapStart, position); }
                catch (InvalidOperationException)
                {
                    // If even the pending gap cannot be queued, retain a conservative
                    // old-source terminal envelope rather than losing its identity.
                    var missingSource = gapSource ?? currentSource;
                    var end = position.Offset.Value < gapStart.Offset.Value ? gapStart : position;
                    var gap = new CaptureGap(_request.Version, missingSource,
                        new TimelineInterval(_request.ClockAnchor.Origin, end), GapReason.CaptureFailure, TimingCertainty.Unknown);
                    Interlocked.Exchange(ref _terminal, new CaptureStatusEvent(_request.Version, missingSource, end,
                        CaptureState.Failed, EventErrorCategory.Unknown, gap));
                }
            }
            if (TerminalStatus is null)
            {
                var state = _stopReason == CaptureStopReason.Pause ? CaptureState.Paused : CaptureState.Stopped;
                if (TerminalStatus is null) { SetTerminal(new CaptureStatusEvent(_request.Version, currentSource, position, state)); }
            }
            lock (_statusGate)
            {
                if (TerminalStatus is { } final) { _statuses.Writer.TryWrite(final); }
                _statuses.Writer.TryComplete();
            }
            _frames.Writer.TryComplete();
        }
    }

    private async Task PumpFramesAsync(AudioNormalizationRun run, EpochState state)
    {
        await foreach (var frame in run.ReadFramesAsync(CancellationToken.None).ConfigureAwait(false))
        {
            try
            {
                if (frame.Metadata.Source != state.Source || frame.Metadata.Interval.TimelineEpoch != _request.ClockAnchor.Origin.TimelineEpoch)
                {
                    throw new InvalidOperationException("A capture epoch returned another source or timeline.");
                }
                if (state.GapStart is { } gap)
                {
                    if (frame.Metadata.Interval.Start.Offset.Value < gap.Offset.Value)
                    {
                        // Different estimated clocks can overlap at a device boundary.
                        // Discard and report the unsupported output rather than emit time backwards.
                        var end = frame.Metadata.Interval.End.Offset.Value > gap.Offset.Value ? frame.Metadata.Interval.End : gap;
                        PublishGap(state.Source, frame.Metadata.Interval.Start, end);
                        frame.Dispose();
                        continue;
                    }
                    PublishGap(state.PreviousSource ?? state.Source, gap, frame.Metadata.Interval.Start);
                    state.GapStart = null;
                }
                state.LastEnd = frame.Metadata.Interval.End;
                Enqueue(frame);
            }
            catch
            {
                frame.Dispose();
                throw;
            }
        }
    }

    private async Task PumpStatusesAsync(AudioNormalizationRun run, EpochState state)
    {
        await foreach (var status in run.ReadStatusAsync(CancellationToken.None).ConfigureAwait(false))
        {
            if (status.Source != state.Source || status.Position.TimelineEpoch != _request.ClockAnchor.Origin.TimelineEpoch)
            {
                throw new InvalidOperationException("A capture status belongs to another source or timeline.");
            }
            // Terminal status is inspected once after the frame and status streams close.
            if (status.State is not (CaptureState.Failed or CaptureState.Stopped or CaptureState.Paused)) { Publish(status); }
        }
    }

    private void Enqueue(OwnedAudioFrame frame)
    {
        lock (_frameGate)
        {
            if (Volatile.Read(ref _stopRequested) != 0 && _stopReason == CaptureStopReason.Pause) { frame.Dispose(); return; }
            while (_queuedBytes + frame.Metadata.PayloadByteCount > _request.BufferPolicy.MaximumBytes ||
                _queuedDuration + frame.Metadata.Interval.DurationMicroseconds > _request.BufferPolicy.MaximumDurationMicroseconds)
            {
                if (!_frames.Reader.TryRead(out var dropped)) { frame.Dispose(); throw new InvalidOperationException("The normalized frame exceeds the queue limit."); }
                AccountRemoval(dropped);
                var metadata = dropped.Metadata;
                dropped.Dispose();
                Publish(new CaptureStatusEvent(_request.Version, metadata.Source, metadata.Interval.End, CaptureState.Degraded,
                    gap: new CaptureGap(_request.Version, metadata.Source, metadata.Interval, GapReason.QueueOverflow, TimingCertainty.Estimated)));
            }
            _queuedBytes += frame.Metadata.PayloadByteCount;
            _queuedDuration += frame.Metadata.Interval.DurationMicroseconds;
            if (!_frames.Writer.TryWrite(frame)) { AccountRemoval(frame); frame.Dispose(); }
        }
    }

    private void AccountRemoval(OwnedAudioFrame frame)
    {
        _queuedBytes -= frame.Metadata.PayloadByteCount;
        _queuedDuration -= frame.Metadata.Interval.DurationMicroseconds;
    }

    private void PublishGap(SourceIdentity source, TimelinePosition start, TimelinePosition end)
    {
        if (end.Offset.Value < start.Offset.Value) { end = start; }
        var gap = new CaptureGap(_request.Version, source, new TimelineInterval(start, end), GapReason.DeviceUnavailable, TimingCertainty.Unknown);
        Publish(new CaptureStatusEvent(_request.Version, source, end, CaptureState.Degraded, gap: gap));
    }

    private void Publish(CaptureStatusEvent status)
    {
        lock (_statusGate)
        {
            if (_statuses.Reader.Count >= StatusCapacity || !_statuses.Writer.TryWrite(status))
            {
                throw new InvalidOperationException("The recovery status queue is full.");
            }
        }
    }

    private void Fail(SourceIdentity source, EventErrorCategory category, TimelinePosition? start)
    {
        var end = Position();
        if (start is not null && start.Offset.Value > end.Offset.Value) { end = start; }
        // A conservative envelope retains uncertainty if the status queue saturated.
        var gap = new CaptureGap(_request.Version, source, new TimelineInterval(_request.ClockAnchor.Origin, end), GapReason.CaptureFailure, TimingCertainty.Unknown);
        SetTerminal(new CaptureStatusEvent(_request.Version, source, end, CaptureState.Failed, category, gap));
    }

    private void SetTerminal(CaptureStatusEvent status)
    {
        Interlocked.CompareExchange(ref _terminal, status, null);
    }

    private TimelinePosition Position() => _clock.GetPosition();
    private static SourceIdentity Next(SourceIdentity source) => new(source.SessionId, source.SourceId, new StreamEpoch(checked(source.StreamEpoch.Value + 1)));

    private static async Task DisposeLateOpenAsync(Task<IAudioCaptureRun> opening)
    {
        try { await (await opening.ConfigureAwait(false)).DisposeAsync().ConfigureAwait(false); }
        catch (Exception) { /* Opening or cleanup failed after cancellation. */ }
    }

    private static async Task<bool> StopWithinDeadlineAsync(IAudioCaptureRun run, CaptureStopReason reason)
    {
        // A third-party/native implementation can block before returning its Task.
        var stop = Task.Run(() => run.StopAsync(reason, CancellationToken.None));
        try { await stop.WaitAsync(StopDeadline).ConfigureAwait(false); return true; }
        catch (TimeoutException)
        {
            _ = DisposeAfterStopAsync(run, stop);
            return false;
        }
    }

    private static async Task ObservePumpsAsync(Task frameTask, Task statusTask)
    {
        try { await Task.WhenAll(frameTask, statusTask).ConfigureAwait(false); }
        catch (Exception) { }
    }

    private static async Task DisposeAfterStopAsync(IAudioCaptureRun run, Task stop)
    {
        try { await stop.ConfigureAwait(false); }
        catch (Exception) { }
        finally
        {
            try { await run.DisposeAsync().ConfigureAwait(false); }
            catch (Exception) { }
        }
    }

    private static async Task<bool> DisposeWithinDeadlineAsync(IAudioCaptureRun run)
    {
        var disposal = Task.Run(async () => await run.DisposeAsync().ConfigureAwait(false));
        try { await disposal.WaitAsync(StopDeadline).ConfigureAwait(false); return true; }
        catch (TimeoutException)
        {
            _ = ObserveDisposalAsync(disposal);
            return false;
        }
    }

    private static async Task ObserveDisposalAsync(Task disposal)
    {
        try { await disposal.ConfigureAwait(false); }
        catch (Exception) { }
    }

    private sealed class EpochState(SourceIdentity source, TimelinePosition? gapStart, SourceIdentity? previousSource)
    {
        internal SourceIdentity Source { get; } = source;
        internal SourceIdentity? PreviousSource { get; } = previousSource;
        internal TimelinePosition? GapStart { get; set; } = gapStart;
        internal TimelinePosition? LastEnd { get; set; }
    }
}
