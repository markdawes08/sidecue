using System.Diagnostics;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;

namespace Sidecue.Windows.Audio;

/// <summary>
/// Opts into a fixed relationship between the Windows performance counter and a session timeline.
/// </summary>
/// <remarks>
/// <see cref="QpcAtOrigin"/> and <see cref="ISessionClock.Anchor"/> describe the same instant.
/// Implementations must use the Windows QPC time base at its native rate for the whole clock lifetime.
/// The raw value uses 100-nanosecond units, as returned by WASAPI, not Stopwatch ticks or UTC.
/// </remarks>
public interface IQpcSessionClock : ISessionClock
{
    RawClockPosition QpcAtOrigin { get; }
}

/// <summary>A session clock whose monotonic origin is explicitly anchored to Windows QPC.</summary>
public sealed class QpcSessionClock : IQpcSessionClock
{
    private const long QpcUnitsPerSecond = 10_000_000;
    private readonly long _originTimestamp;

    public QpcSessionClock(SessionId sessionId, TimelineEpochId timelineEpoch)
    {
        // Stopwatch is backed by QPC on Windows. One reading is shared by both
        // the monotonic session origin and the QPC correlation; UTC is history only.
        if (!Stopwatch.IsHighResolution) { throw new PlatformNotSupportedException("A high-resolution Windows performance counter is required."); }
        _originTimestamp = Stopwatch.GetTimestamp();
        if (_originTimestamp < 0) { throw new InvalidOperationException("The performance counter origin is negative."); }
        var origin = new TimelinePosition(timelineEpoch, new SessionOffset(0));
        Anchor = new SessionClockAnchor(sessionId, origin, new UtcTimestamp(DateTimeOffset.UtcNow));
        long qpc100Nanoseconds = checked((long)((Int128)_originTimestamp * QpcUnitsPerSecond / Stopwatch.Frequency));
        QpcAtOrigin = new RawClockPosition(qpc100Nanoseconds, QpcUnitsPerSecond);
    }

    public SessionClockAnchor Anchor { get; }
    public RawClockPosition QpcAtOrigin { get; }

    public TimelinePosition GetPosition()
    {
        var delta = Stopwatch.GetTimestamp() - _originTimestamp;
        if (delta < 0) { throw new InvalidOperationException("The performance counter moved before the clock origin."); }
        var microseconds = checked((long)((Int128)delta * 1_000_000 / Stopwatch.Frequency));
        return new TimelinePosition(Anchor.Origin.TimelineEpoch, new SessionOffset(microseconds));
    }

    public UtcTimestamp GetUtcNow() => new(DateTimeOffset.UtcNow);
}
