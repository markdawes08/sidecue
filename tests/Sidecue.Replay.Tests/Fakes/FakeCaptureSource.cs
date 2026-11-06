using System.Buffers;
using System.Runtime.CompilerServices;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;

namespace Sidecue.Replay.Tests.Fakes;

/// <summary>Finite synthetic audio, allocated only after the scripted run is validated.</summary>
internal sealed class FakeCaptureSource : IAudioCaptureSource
{
    private readonly ManualSessionClock _clock;
    private readonly AudioFrameMetadata[] _frames;

    public FakeCaptureSource(ManualSessionClock clock, IEnumerable<AudioFrameMetadata> frames)
    {
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(frames);
        _clock = clock;
        _frames = frames.ToArray();
        foreach (var frame in _frames) { ArgumentNullException.ThrowIfNull(frame); }
    }

    public int OwnersCreated { get; private set; }
    public int OwnersDisposed { get; private set; }

    public Task<IAudioCaptureRun> OpenAsync(CaptureRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ct.ThrowIfCancellationRequested();
        if (request.ClockAnchor != _clock.Anchor)
        {
            throw new ArgumentException("The scripted capture clock does not match the request.", nameof(request));
        }

        long bytes = 0;
        long duration = 0;
        AudioFrameMetadata? previous = null;
        foreach (var metadata in _frames)
        {
            if (metadata.Source != request.Source ||
                metadata.Interval.TimelineEpoch != request.ClockAnchor.Origin.TimelineEpoch ||
                metadata.Interval.Start.Offset.Value < request.ClockAnchor.Origin.Offset.Value ||
                (previous is not null && (metadata.Sequence.Value <= previous.Sequence.Value ||
                    metadata.Interval.Start.Offset.Value < previous.Interval.End.Offset.Value)))
            {
                throw new ArgumentException("Scripted frames must match the ordered source timeline.", nameof(request));
            }
            bytes = checked(bytes + metadata.PayloadByteCount);
            duration = checked(duration + metadata.Interval.DurationMicroseconds);
            previous = metadata;
        }
        if (bytes > request.BufferPolicy.MaximumBytes || duration > request.BufferPolicy.MaximumDurationMicroseconds)
        {
            throw new ArgumentException("The finite capture script exceeds its buffer policy.", nameof(request));
        }

        var owned = new Queue<OwnedAudioFrame>();
        try
        {
            foreach (var metadata in _frames)
            {
                ct.ThrowIfCancellationRequested();
                var owner = new CountingOwner(metadata.PayloadByteCount, () => OwnersDisposed++);
                OwnersCreated++;
                try { owned.Enqueue(new OwnedAudioFrame(metadata, owner, metadata.PayloadByteCount)); }
                catch { owner.Dispose(); throw; }
            }
            return Task.FromResult<IAudioCaptureRun>(new Run(_clock, request.Source, owned));
        }
        catch
        {
            while (owned.TryDequeue(out var frame)) { frame.Dispose(); }
            throw;
        }
    }

    private sealed class CountingOwner(int length, Action onDispose) : IMemoryOwner<byte>
    {
        private byte[]? _buffer = new byte[length];

        public Memory<byte> Memory => _buffer ?? throw new ObjectDisposedException(nameof(CountingOwner));

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _buffer, null) is not null) { onDispose(); }
        }
    }

    // Single-threaded reference fake, not a native callback or concurrent queue implementation.
    private sealed class Run(ManualSessionClock clock, SourceIdentity source, Queue<OwnedAudioFrame> frames) : IAudioCaptureRun
    {
        private bool _readStarted;
        private bool _disposed;
        public CaptureStatusEvent? TerminalStatus { get; private set; }

        public async IAsyncEnumerable<OwnedAudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_readStarted) { throw new InvalidOperationException("A capture run has one frame consumer."); }
            _readStarted = true;
            await Task.CompletedTask.ConfigureAwait(false);
            while (TerminalStatus is null && frames.Count != 0)
            {
                ct.ThrowIfCancellationRequested();
                // Remove the lease before yielding: it now belongs to the consumer.
                yield return frames.Dequeue();
            }
        }

        public async IAsyncEnumerable<CaptureStatusEvent> ReadStatusAsync([EnumeratorCancellation] CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await Task.CompletedTask.ConfigureAwait(false);
            yield return TerminalStatus ?? new CaptureStatusEvent(ContractVersion.Current, source,
                clock.GetPosition(), CaptureState.Live);
        }

        public Task StopAsync(CaptureStopReason reason, CancellationToken ct)
        {
            if (!Enum.IsDefined(reason)) { throw new ArgumentOutOfRangeException(nameof(reason)); }
            Stop(reason == CaptureStopReason.Pause ? CaptureState.Paused : CaptureState.Stopped);
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Stop(CaptureState.Stopped);
            _disposed = true;
            return ValueTask.CompletedTask;
        }

        private void Stop(CaptureState state)
        {
            TerminalStatus ??= new CaptureStatusEvent(ContractVersion.Current, source, clock.GetPosition(), state);
            while (frames.TryDequeue(out var frame)) { frame.Dispose(); }
        }
    }
}
