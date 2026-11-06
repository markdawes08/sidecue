namespace Sidecue.Core.Contracts;

/// <summary>Calendar/history time. Never a source of elapsed capture time.</summary>
public readonly record struct UtcTimestamp
{
    public UtcTimestamp(DateTimeOffset value) => Value = value.ToUniversalTime();
    public DateTimeOffset Value { get; }
}

/// <summary>An integer microsecond offset, usable only with its timeline epoch.</summary>
public readonly record struct SessionOffset
{
    public SessionOffset(long value) { ArgumentOutOfRangeException.ThrowIfNegative(value); Value = value; }
    public long Value { get; }
}

/// <summary>Optional device/QPC provenance. Normalize at the Windows boundary.</summary>
public sealed record RawClockPosition
{
    public RawClockPosition(long ticks, long ticksPerSecond)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ticks);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ticksPerSecond);
        Ticks = ticks;
        TicksPerSecond = ticksPerSecond;
    }

    public long Ticks { get; }
    public long TicksPerSecond { get; }
}

public sealed record TimelinePosition
{
    public TimelinePosition(TimelineEpochId timelineEpoch, SessionOffset offset)
    {
        ContractGuard.Guid(timelineEpoch.Value, nameof(timelineEpoch));
        TimelineEpoch = timelineEpoch;
        Offset = offset;
    }

    public TimelineEpochId TimelineEpoch { get; }
    public SessionOffset Offset { get; }
}

/// <summary>An ordered half-open interval within one monotonic clock lifetime.</summary>
public sealed record TimelineInterval
{
    public TimelineInterval(TimelinePosition start, TimelinePosition end)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);
        if (start.TimelineEpoch != end.TimelineEpoch)
        {
            throw new ArgumentException("An interval cannot cross timeline epochs.", nameof(end));
        }
        if (end.Offset.Value < start.Offset.Value)
        {
            throw new ArgumentException("An interval cannot end before it starts.", nameof(end));
        }
        Start = start;
        End = end;
    }

    public TimelinePosition Start { get; }
    public TimelinePosition End { get; }
    public TimelineEpochId TimelineEpoch => Start.TimelineEpoch;
    public long DurationMicroseconds => End.Offset.Value - Start.Offset.Value;
}
