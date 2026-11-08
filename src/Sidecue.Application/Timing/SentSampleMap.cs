using Sidecue.Core.Contracts;

namespace Sidecue.Application.Timing;

/// <summary>
/// Maps audio actually sent on one connection to supported source intervals.
/// The owner must serialize access with that connection's send operations.
/// </summary>
/// <remarks>
/// Calling code records a frame only after a confirmed send, never when it is
/// merely captured or queued. An unknown send seals this map: a new connection
/// epoch needs a new map. The interval limit prevents unbounded provenance.
/// </remarks>
public sealed class SentSampleMap
{
    private readonly SourceIdentity _source;
    private readonly TimelineEpochId _timeline;
    private readonly AudioFormat _format;
    private readonly int _maximumIntervals;
    private readonly List<Segment> _segments = [];
    private long _confirmedSamples;
    private long? _uncertainEnd;
    private AudioFrameMetadata? _uncertainSend;
    private long? _lastSequence;

    public SentSampleMap(SourceIdentity source, TimelineEpochId timeline, AudioFormat format, int maximumIntervals)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(format);
        if (timeline.Value == Guid.Empty) { throw new ArgumentException("A valid timeline is required.", nameof(timeline)); }
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumIntervals);
        _source = source;
        _timeline = timeline;
        _format = format;
        _maximumIntervals = maximumIntervals;
    }

    public long ConfirmedSamples => _confirmedSamples;
    public bool IsSealed => _uncertainEnd.HasValue;
    public int IntervalCount => _segments.Count;
    public AudioFrameMetadata? UncertainSend => _uncertainSend;

    /// <summary>Records one successful, completed send and advances provider samples.</summary>
    public void RecordConfirmed(AudioFrameMetadata frame, TimingCertainty certainty)
    {
        Validate(frame);
        if (!Enum.IsDefined(certainty)) { throw new ArgumentOutOfRangeException(nameof(certainty)); }
        var end = checked(_confirmedSamples + frame.SampleCount);
        var effective = EffectiveCertainty(frame, certainty);
        var previous = _segments.Count == 0 ? null : _segments[^1];
        // A skipped captured frame with touching timestamps still represents a
        // loss. Its extent is unknown, so retain a boundary even if the clock
        // estimates made the two intervals appear contiguous.
        bool unlocatedGap = previous is not null && _lastSequence is { } sequence &&
            frame.Sequence.Value - sequence > 1 && previous.Interval.End.Offset == frame.Interval.Start.Offset;
        bool merge = previous is not null && !unlocatedGap && CanMerge(previous, frame, effective);
        if (!merge && _segments.Count == _maximumIntervals)
        {
            throw new InvalidOperationException("The sent-sample provenance limit has been reached.");
        }

        if (merge)
        {
            _segments[^1] = new Segment(previous!.StartSample, end,
                new TimelineInterval(previous.Interval.Start, frame.Interval.End), effective, previous.DiscontinuityBefore);
        }
        else
        {
            _segments.Add(new Segment(_confirmedSamples, end, frame.Interval, effective,
                unlocatedGap || (frame.Flags & AudioFrameAttributes.Discontinuity) != 0));
        }
        _confirmedSamples = end;
        _lastSequence = frame.Sequence.Value;
    }

    /// <summary>Records an ambiguous send without advancing confirmed provider time.</summary>
    public void RecordUncertain(AudioFrameMetadata frame)
    {
        Validate(frame);
        // The remote may have received none, some or all of this frame. No part
        // is a confirmed source interval, and continuing requires a new map.
        var uncertainEnd = checked(_confirmedSamples + frame.SampleCount);
        _uncertainSend = frame;
        _uncertainEnd = uncertainEnd;
    }

    /// <summary>Maps a non-empty half-open provider-sample range.</summary>
    public SentAudioRange Map(long startSample, long endSample)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startSample);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(endSample, startSample);
        if (endSample > (_uncertainEnd ?? _confirmedSamples))
        {
            throw new ArgumentOutOfRangeException(nameof(endSample), "The range exceeds recorded send outcomes.");
        }

        var supported = new List<TimelineInterval>();
        var certainty = TimingCertainty.Exact;
        foreach (var segment in _segments)
        {
            long start = Math.Max(startSample, segment.StartSample);
            long end = Math.Min(endSample, segment.EndSample);
            if (start >= end) { continue; }
            supported.Add(new TimelineInterval(PositionAt(segment, start), PositionAt(segment, end)));
            certainty = Weaker(certainty, segment.Certainty);
            if (segment.DiscontinuityBefore && startSample < segment.StartSample)
            {
                // The missing interval's extent is unknown, even if these
                // individual segment endpoints otherwise have precise timing.
                certainty = TimingCertainty.Unknown;
            }
        }

        bool containsUncertainSend = endSample > _confirmedSamples;
        if (containsUncertainSend) { certainty = TimingCertainty.Unknown; }
        return new SentAudioRange(supported, certainty, containsUncertainSend);
    }

    /// <summary>
    /// Maps a boundary to the following segment, except the final confirmed end.
    /// Points in an ambiguous send have no supported position.
    /// </summary>
    public SentAudioPoint MapPoint(long sampleOffset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleOffset);
        if (sampleOffset > (_uncertainEnd ?? _confirmedSamples))
        {
            throw new ArgumentOutOfRangeException(nameof(sampleOffset), "The point exceeds recorded send outcomes.");
        }
        if (_uncertainEnd.HasValue && sampleOffset >= _confirmedSamples)
        {
            return new SentAudioPoint(null, TimingCertainty.Unknown);
        }
        foreach (var segment in _segments)
        {
            if (sampleOffset >= segment.StartSample && sampleOffset < segment.EndSample)
            {
                return new SentAudioPoint(PositionAt(segment, sampleOffset), segment.Certainty);
            }
        }
        if (_segments.Count > 0 && sampleOffset == _confirmedSamples)
        {
            var last = _segments[^1];
            return new SentAudioPoint(last.Interval.End, last.Certainty);
        }
        throw new ArgumentOutOfRangeException(nameof(sampleOffset), "No confirmed sample supports this point.");
    }

    private void Validate(AudioFrameMetadata frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (IsSealed) { throw new InvalidOperationException("An uncertain send requires a new connection epoch."); }
        if (frame.Source != _source || frame.Interval.TimelineEpoch != _timeline || frame.Format != _format)
        {
            throw new ArgumentException("The frame must match this source, stream, timeline and format.", nameof(frame));
        }
        if (_lastSequence is { } sequence && frame.Sequence.Value <= sequence)
        {
            throw new ArgumentException("Sent frame sequences must increase.", nameof(frame));
        }
        if (_segments.Count > 0 && frame.Interval.Start.Offset.Value < _segments[^1].Interval.End.Offset.Value)
        {
            throw new ArgumentException("Sent source intervals must be ordered and nonoverlapping.", nameof(frame));
        }
    }

    private static TimingCertainty EffectiveCertainty(AudioFrameMetadata frame, TimingCertainty certainty)
    {
        if ((frame.Flags & AudioFrameAttributes.TimestampError) != 0) { return TimingCertainty.Unknown; }
        return (frame.Flags & AudioFrameAttributes.SessionTimestampEstimated) == 0
            ? certainty : Weaker(certainty, TimingCertainty.Estimated);
    }

    private static TimingCertainty Weaker(TimingCertainty left, TimingCertainty right) =>
        (TimingCertainty)Math.Max((int)left, (int)right);

    private static bool CanMerge(Segment previous, AudioFrameMetadata frame, TimingCertainty certainty)
    {
        if ((frame.Flags & AudioFrameAttributes.Discontinuity) != 0 || previous.Certainty != certainty ||
            previous.Interval.End.Offset != frame.Interval.Start.Offset) { return false; }
        long previousCount = previous.EndSample - previous.StartSample;
        // Identical rational slopes guarantee that coalescing cannot alter a
        // sample-boundary mapping; mismatched rounding is kept separate.
        return (Int128)previous.Interval.DurationMicroseconds * frame.SampleCount ==
            (Int128)frame.Interval.DurationMicroseconds * previousCount;
    }

    private static TimelinePosition PositionAt(Segment segment, long sample)
    {
        long relativeSample = sample - segment.StartSample;
        long count = segment.EndSample - segment.StartSample;
        long relativeMicros = checked((long)((Int128)relativeSample * segment.Interval.DurationMicroseconds / count));
        return new TimelinePosition(segment.Interval.TimelineEpoch,
            new SessionOffset(checked(segment.Interval.Start.Offset.Value + relativeMicros)));
    }

    private sealed record Segment(long StartSample, long EndSample, TimelineInterval Interval,
        TimingCertainty Certainty, bool DiscontinuityBefore);
}

/// <summary>A provider range's confirmed source support and uncertainty.</summary>
public sealed class SentAudioRange
{
    internal SentAudioRange(List<TimelineInterval> supportedIntervals, TimingCertainty certainty, bool containsUncertainSend)
    {
        SupportedIntervals = Array.AsReadOnly(supportedIntervals.ToArray());
        Certainty = certainty;
        ContainsUncertainSend = containsUncertainSend;
    }

    public IReadOnlyList<TimelineInterval> SupportedIntervals { get; }
    public TimingCertainty Certainty { get; }
    public bool ContainsUncertainSend { get; }
}

/// <summary>A source point, or an explicitly uncertain point with no confirmed position.</summary>
public sealed record SentAudioPoint(TimelinePosition? Position, TimingCertainty Certainty);
