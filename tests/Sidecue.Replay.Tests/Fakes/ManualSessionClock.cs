using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;

namespace Sidecue.Replay.Tests.Fakes;

/// <summary>A harness-driven clock. No wall clock or delays participate in replay.</summary>
internal sealed class ManualSessionClock : ISessionClock
{
    private long _offset;

    public ManualSessionClock(SessionClockAnchor anchor)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        Anchor = anchor;
        _offset = anchor.Origin.Offset.Value;
    }

    public SessionClockAnchor Anchor { get; }

    public void AdvanceTo(long offsetMicroseconds)
    {
        if (offsetMicroseconds < _offset)
        {
            throw new ArgumentOutOfRangeException(nameof(offsetMicroseconds), "Replay time cannot move backward.");
        }
        _offset = offsetMicroseconds;
    }

    public TimelinePosition GetPosition() => new(Anchor.Origin.TimelineEpoch, new SessionOffset(_offset));

    public UtcTimestamp GetUtcNow() =>
        new(Anchor.UtcAtOrigin.Value.AddTicks(checked((_offset - Anchor.Origin.Offset.Value) * 10)));
}
