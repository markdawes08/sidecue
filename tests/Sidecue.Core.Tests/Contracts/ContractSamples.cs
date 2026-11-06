using Sidecue.Core.Contracts;

namespace Sidecue.Core.Tests.Contracts;

internal static class ContractSamples
{
    internal static SessionId Session { get; } = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    internal static SourceId Source { get; } = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    internal static TimelineEpochId Timeline { get; } = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    internal static EventId Event { get; } = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    internal static SegmentId Segment { get; } = new(Guid.Parse("55555555-5555-5555-5555-555555555555"));
    internal static RequestId Request { get; } = new(Guid.Parse("66666666-6666-6666-6666-666666666666"));

    internal static SourceIdentity SourceIdentity(long streamEpoch = 0) => new(Session, Source, new StreamEpoch(streamEpoch));

    internal static TimelinePosition Position(long microseconds, TimelineEpochId? epoch = null) =>
        new(epoch ?? Timeline, new SessionOffset(microseconds));

    internal static TimelineInterval Interval(long start = 0, long end = 100_000, TimelineEpochId? epoch = null) =>
        new(Position(start, epoch), Position(end, epoch));
}
