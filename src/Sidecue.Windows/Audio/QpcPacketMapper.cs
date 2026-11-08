using Sidecue.Core.Contracts;

namespace Sidecue.Windows.Audio;

/// <summary>Maps a trusted native packet timestamp onto an explicitly QPC-based timeline.</summary>
internal static class QpcPacketMapper
{
    private const long QpcUnitsPerSecond = 10_000_000;
    // Independently rounded 100-ns QPC and sample-duration endpoints may each
    // differ by less than one microsecond from their ideal integer value.
    private const long EndpointRoundingToleranceMicroseconds = 2;

    internal static bool TryMap(SessionClockAnchor anchor, RawClockPosition? qpcAtOrigin,
        TimelinePosition observedNow, ulong packetQpc100Nanoseconds, long durationMicroseconds,
        AudioFrameAttributes nativeFlags, ulong? previousPacketQpc, out TimelineInterval? interval)
    {
        interval = null;
        if (qpcAtOrigin is null || qpcAtOrigin.TicksPerSecond != QpcUnitsPerSecond ||
            observedNow.TimelineEpoch != anchor.Origin.TimelineEpoch || durationMicroseconds < 0 ||
            (nativeFlags & AudioFrameAttributes.TimestampError) != 0 ||
            packetQpc100Nanoseconds > long.MaxValue ||
            packetQpc100Nanoseconds < (ulong)qpcAtOrigin.Ticks ||
            (previousPacketQpc is { } previous && packetQpc100Nanoseconds <= previous))
        {
            return false;
        }

        // Difference before conversion avoids overflow and avoids treating a raw
        // 100-nanosecond WASAPI QPC value as Stopwatch-frequency ticks.
        var deltaMicroseconds = ((Int128)packetQpc100Nanoseconds - qpcAtOrigin.Ticks) / 10;
        var startValue = (Int128)anchor.Origin.Offset.Value + deltaMicroseconds;
        var endValue = startValue + durationMicroseconds;
        if (startValue > observedNow.Offset.Value || endValue > long.MaxValue ||
            endValue > (Int128)observedNow.Offset.Value + EndpointRoundingToleranceMicroseconds) { return false; }

        interval = new TimelineInterval(
            new TimelinePosition(observedNow.TimelineEpoch, new SessionOffset((long)startValue)),
            new TimelinePosition(observedNow.TimelineEpoch, new SessionOffset((long)endValue)));
        return true;
    }
}
