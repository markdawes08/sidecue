using System.Buffers;
using System.Buffers.Binary;
using NAudio.Dsp;
using Sidecue.Core.Contracts;

namespace Sidecue.Windows.Audio;

/// <summary>Per-source, worker-only conversion with state retained across contiguous packets.</summary>
internal sealed class StreamingAudioNormalizer : IDisposable
{
    private const int TargetRate = 16_000;
    private const int MaximumInputFrames = 2_048;
    private readonly CaptureRequest _request;
    private readonly int _chunkSamples;
    private readonly Action<OwnedAudioFrame> _emit;
    private readonly Action<CaptureGap> _gap;
    private readonly short[] _pending;
    private AudioFormat? _inputFormat;
    private WdlResampler? _resampler;
    private TimelinePosition? _segmentStart;
    private TimelinePosition? _lastInputEnd;
    private TimelinePosition? _lastOutputEnd;
    private RawClockPosition? _lastDevicePosition;
    private int _lastInputSampleCount;
    private bool _lastDevicePositionTrusted;
    private long _lastInputSequence = -1;
    private long _emittedSamples;
    private long _outputSequence;
    private int _pendingCount;
    private bool _allZero = true;
    private bool _boundaryPending;
    private AudioFrameAttributes _segmentFlags;
    private bool _disposed;

    internal StreamingAudioNormalizer(CaptureRequest request, int chunkSamples,
        Action<OwnedAudioFrame> emit, Action<CaptureGap> gap)
    {
        _request = request;
        _chunkSamples = chunkSamples;
        _emit = emit;
        _gap = gap;
        _pending = new short[chunkSamples];
    }

    internal void Process(OwnedAudioFrame frame)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var metadata = frame.Metadata;
        if (metadata.Source != _request.Source || metadata.Interval.TimelineEpoch != _request.ClockAnchor.Origin.TimelineEpoch)
        {
            throw new InvalidOperationException("Capture identity or timeline changed during normalization.");
        }
        if (metadata.Sequence.Value <= _lastInputSequence)
        {
            throw new InvalidOperationException("Capture frame sequence must increase.");
        }
        var format = metadata.Format;
        if (format.SampleRate is < 8_000 or > 192_000 || format.Channels > 8 || format.ByteOrder != AudioByteOrder.LittleEndian)
        {
            throw new NotSupportedException("The capture format cannot be normalized by this adapter.");
        }

        bool discontinuity = _lastInputSequence >= 0 && metadata.Sequence.Value != _lastInputSequence + 1;
        discontinuity |= (metadata.Flags & AudioFrameAttributes.Discontinuity) != 0;
        discontinuity |= _inputFormat is not null && _inputFormat != format;
        // A native timestamp error affects only that run of packets. Reset at
        // transitions so one bad packet does not poison all future output.
        discontinuity |= _lastInputSequence >= 0 &&
            ((_segmentFlags & AudioFrameAttributes.TimestampError) != (metadata.Flags & AudioFrameAttributes.TimestampError));
        if (_lastInputSequence < 0 && metadata.Sequence.Value != 0) { discontinuity = true; }
        var trustedDevicePosition = metadata.DevicePosition is { } currentDevice &&
            currentDevice.TicksPerSecond == format.SampleRate &&
            (metadata.Flags & AudioFrameAttributes.TimestampError) == 0;
        var canCompareDevice = trustedDevicePosition && _lastDevicePositionTrusted && _lastDevicePosition is not null &&
            _inputFormat == format;
        if (canCompareDevice &&
            (Int128)metadata.DevicePosition!.Ticks != (Int128)_lastDevicePosition!.Ticks + _lastInputSampleCount)
        {
            discontinuity = true;
        }
        var uncertain = ((metadata.Flags | _segmentFlags) &
            (AudioFrameAttributes.TimestampError | AudioFrameAttributes.SessionTimestampEstimated)) != 0;
        // Only matching device-frame positions justify tolerating small clock
        // alignment jitter; otherwise even a small hole remains uncertain loss.
        var tolerance = uncertain && canCompareDevice && !discontinuity ? 1_000 : 1;
        if (_lastInputEnd is not null && metadata.Interval.Start.Offset.Value < _lastInputEnd.Offset.Value - tolerance)
        {
            if ((metadata.Flags & AudioFrameAttributes.SessionTimestampEstimated) != 0)
            {
                // Callback-time estimates can overlap during a batch. Their true
                // placement is unknown, so do not move the packet or blend it
                // into previously emitted audio. Report and discard it instead.
                var previousEnd = _lastInputEnd;
                CloseSegment();
                var lossEnd = metadata.Interval.End.Offset.Value > previousEnd.Offset.Value ? metadata.Interval.End : previousEnd;
                _gap(new CaptureGap(_request.Version, _request.Source,
                    new TimelineInterval(metadata.Interval.Start, lossEnd), GapReason.CaptureDiscontinuity, TimingCertainty.Unknown));
                _boundaryPending = true;
                _lastInputSequence = metadata.Sequence.Value;
                _lastInputEnd = lossEnd;
                _lastDevicePosition = metadata.DevicePosition;
                _lastDevicePositionTrusted = trustedDevicePosition;
                _lastInputSampleCount = metadata.SampleCount;
                return;
            }
            throw new InvalidOperationException("Overlapping source time cannot be normalized without inventing a timeline.");
        }
        if (_lastInputEnd is not null && metadata.Interval.Start.Offset.Value - _lastInputEnd.Offset.Value > tolerance)
        {
            // Even a correlated clock is an estimate. Material disagreement is a
            // boundary, not evidence that the interval in between was captured.
            discontinuity = true;
        }
        if (discontinuity)
        {
            var previousEnd = _lastInputEnd;
            CloseSegment();
            if (_lastOutputEnd is not null && metadata.Interval.Start.Offset.Value < _lastOutputEnd.Offset.Value)
            {
                throw new InvalidOperationException("A new segment would overlap normalized output.");
            }
            var gapStart = previousEnd is not null && previousEnd.Offset.Value <= metadata.Interval.Start.Offset.Value
                ? previousEnd : metadata.Interval.Start;
            _gap(new CaptureGap(_request.Version, _request.Source,
                new TimelineInterval(gapStart, metadata.Interval.Start), GapReason.CaptureDiscontinuity, TimingCertainty.Unknown));
            _boundaryPending = true;
        }

        if (_resampler is null)
        {
            _inputFormat = format;
            _segmentStart = metadata.Interval.Start;
            _emittedSamples = 0;
            _segmentFlags = AudioFrameAttributes.None;
            _resampler = new WdlResampler();
            _resampler.SetMode(true, 2, false);
            _resampler.SetFilterParms();
            _resampler.SetFeedMode(true);
            _resampler.SetRates(format.SampleRate, TargetRate);
        }
        _segmentFlags |= metadata.Flags & ~(AudioFrameAttributes.Silence | AudioFrameAttributes.Discontinuity);
        _lastInputSequence = metadata.Sequence.Value;
        _lastInputEnd = metadata.Interval.End;
        _lastDevicePosition = metadata.DevicePosition;
        _lastDevicePositionTrusted = trustedDevicePosition;
        _lastInputSampleCount = metadata.SampleCount;
        if (_pendingCount == _chunkSamples && PendingIsCovered()) { EmitPending(); }

        var payload = frame.Payload.Span;
        for (var inputOffset = 0; inputOffset < metadata.SampleCount;)
        {
            var inputFrames = Math.Min(MaximumInputFrames, metadata.SampleCount - inputOffset);
            var requested = _resampler.ResamplePrepare(inputFrames, 1, out var input);
            if (requested != inputFrames) { throw new InvalidOperationException("Resampler could not accept the complete input chunk."); }
            for (var index = 0; index < inputFrames; index++)
            {
                input[index] = Downmix(payload, inputOffset + index, format);
            }
            var maximumOutput = checked((int)(((long)inputFrames * TargetRate + format.SampleRate - 1) / format.SampleRate) + 64);
            var scratch = ArrayPool<float>.Shared.Rent(maximumOutput);
            try
            {
                int count = _resampler.ResampleOut(scratch.AsSpan(0, maximumOutput), inputFrames, maximumOutput, 1);
                for (var index = 0; index < count; index++)
                {
                    if (_pendingCount == _chunkSamples)
                    {
                        if (!PendingIsCovered()) { throw new InvalidOperationException("Normalized samples extend beyond supported source time."); }
                        EmitPending();
                    }
                    float value = scratch[index];
                    if (!float.IsFinite(value)) { throw new InvalidOperationException("The capture packet contains a non-finite sample."); }
                    value = Math.Clamp(value, -1f, 1f);
                    short sample = value <= -1f ? short.MinValue : (short)Math.Round(value * short.MaxValue);
                    _pending[_pendingCount++] = sample;
                    _allZero &= sample == 0;
                    if (_pendingCount == _chunkSamples && PendingIsCovered()) { EmitPending(); }
                }
            }
            finally
            {
                scratch.AsSpan().Clear();
                ArrayPool<float>.Shared.Return(scratch);
            }
            inputOffset += inputFrames;
        }
    }

    internal void Complete() => CloseSegment();

    private void CloseSegment()
    {
        if (_resampler is null || _segmentStart is null) { return; }
        if (_pendingCount != 0)
        {
            if (PendingIsCovered()) { EmitPending(); }
            else
            {
                // Never assign output samples to unsupported source time.
                Array.Clear(_pending, 0, _pendingCount);
                _pendingCount = 0;
                _allZero = true;
                var boundary = _lastInputEnd ?? _segmentStart;
                _gap(new CaptureGap(_request.Version, _request.Source,
                    new TimelineInterval(boundary, boundary), GapReason.CaptureFailure, TimingCertainty.Unknown));
            }
        }
        var representedEnd = PositionAt(_emittedSamples);
        if (_lastInputEnd is not null && representedEnd.Offset.Value < _lastInputEnd.Offset.Value)
        {
            // Do not fabricate filter-tail samples when there is no subsequent input.
            _gap(new CaptureGap(_request.Version, _request.Source,
                new TimelineInterval(representedEnd, _lastInputEnd), GapReason.CaptureDiscontinuity, TimingCertainty.Unknown));
        }
        _resampler.Reset();
        _resampler = null;
        _inputFormat = null;
        _segmentStart = null;
        _segmentFlags = AudioFrameAttributes.None;
    }

    private void EmitPending()
    {
        var owner = new ClearedArrayOwner(_pendingCount * 2);
        try
        {
            for (var index = 0; index < _pendingCount; index++)
            {
                BinaryPrimitives.WriteInt16LittleEndian(owner.Memory.Span.Slice(index * 2, 2), _pending[index]);
            }
            var start = PositionAt(_emittedSamples);
            var end = PositionAt(checked(_emittedSamples + _pendingCount));
            var flags = _segmentFlags | (_allZero ? AudioFrameAttributes.Silence : AudioFrameAttributes.None) |
                (_boundaryPending ? AudioFrameAttributes.Discontinuity : AudioFrameAttributes.None);
            var metadata = new AudioFrameMetadata(_request.Version, _request.Source,
                new FrameSequence(_outputSequence++), new TimelineInterval(start, end),
                new AudioFormat(TargetRate, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian), _pendingCount, flags);
            var frame = new OwnedAudioFrame(metadata, owner, _pendingCount * 2);
            owner = null!;
            _emittedSamples += _pendingCount;
            _lastOutputEnd = end;
            Array.Clear(_pending, 0, _pendingCount);
            _pendingCount = 0;
            _allZero = true;
            _boundaryPending = false;
            try { _emit(frame); }
            catch { frame.Dispose(); throw; }
        }
        finally { owner?.Dispose(); }
    }

    private TimelinePosition PositionAt(long outputSample) => new(_request.ClockAnchor.Origin.TimelineEpoch,
        new SessionOffset(checked(_segmentStart!.Offset.Value + (long)((Int128)outputSample * 1_000_000 / TargetRate))));

    private bool PendingIsCovered() => _lastInputEnd is not null &&
        (Int128)PositionAt(checked(_emittedSamples + _pendingCount)).Offset.Value <= (Int128)_lastInputEnd.Offset.Value + 1;

    private static float Downmix(ReadOnlySpan<byte> bytes, int frameIndex, AudioFormat format)
    {
        double sum = 0;
        int offset = checked(frameIndex * format.BytesPerFrame);
        for (var channel = 0; channel < format.Channels; channel++, offset += format.BytesPerSample)
        {
            float sample = format.Representation switch
            {
                AudioSampleRepresentation.Pcm16 => BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(offset, 2)) / 32768f,
                AudioSampleRepresentation.Pcm24 => ReadPcm24(bytes.Slice(offset, 3)) / 8388608f,
                AudioSampleRepresentation.Pcm32 => (float)(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4)) / 2147483648d),
                AudioSampleRepresentation.FloatingPoint32 => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.Slice(offset, 4))),
                _ => throw new NotSupportedException("Unsupported audio representation.")
            };
            if (!float.IsFinite(sample)) { throw new InvalidOperationException("The capture packet contains a non-finite sample."); }
            sum += Math.Clamp(sample, -1f, 1f);
        }
        return (float)(sum / format.Channels);
    }

    private static int ReadPcm24(ReadOnlySpan<byte> bytes)
    {
        int value = bytes[0] | (bytes[1] << 8) | (bytes[2] << 16);
        return (value << 8) >> 8;
    }

    public void Dispose()
    {
        if (_disposed) { return; }
        _disposed = true;
        Array.Clear(_pending);
        _resampler?.Reset();
        _resampler = null;
        GC.SuppressFinalize(this);
    }

    private sealed class ClearedArrayOwner(int length) : IMemoryOwner<byte>
    {
        private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(length);
        public Memory<byte> Memory => Volatile.Read(ref _buffer) ?? throw new ObjectDisposedException(nameof(ClearedArrayOwner));
        public void Dispose()
        {
            var buffer = Interlocked.Exchange(ref _buffer, null);
            if (buffer is not null)
            {
                buffer.AsSpan().Clear();
                ArrayPool<byte>.Shared.Return(buffer);
            }
            GC.SuppressFinalize(this);
        }
    }
}
