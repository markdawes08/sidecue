using Sidecue.Core.Contracts;

namespace Sidecue.Application.Tests.Contracts;

internal static class ContractSamples
{
    internal static SessionId Session { get; } = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    internal static SessionId OtherSession { get; } = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    internal static SourceId Source { get; } = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    internal static TimelineEpochId Timeline { get; } = new(Guid.Parse("44444444-4444-4444-4444-444444444444"));
    internal static RequestId Request { get; } = new(Guid.Parse("55555555-5555-5555-5555-555555555555"));
    internal static ProfileVersionId Profile { get; } = new(Guid.Parse("66666666-6666-6666-6666-666666666666"));
    internal static SegmentId Segment { get; } = new(Guid.Parse("77777777-7777-7777-7777-777777777777"));
    internal static EventId Event { get; } = new(Guid.Parse("88888888-8888-8888-8888-888888888888"));
    internal static SourceIdentity Identity(SessionId? session = null) => new(session ?? Session, Source, new StreamEpoch(0));
    internal static TimelinePosition Position(long offset = 0) => new(Timeline, new SessionOffset(offset));
    internal static TimelineInterval Interval() => new(Position(), Position(100_000));
    internal static TranscriptEventHeader Header(SessionId? session = null) =>
        new(ContractVersion.Current, Identity(session), Event, new EventSequence(0));
    internal static FinalSegment Final(SessionId? session = null) =>
        new(Header(session), Segment, new SegmentRevision(1), [Interval()], TimingCertainty.Exact, "Synthetic finalized text");
}
