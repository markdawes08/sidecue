using Sidecue.Application.Transcripts;
using Sidecue.Core.Contracts;
using Xunit;

namespace Sidecue.Replay.Tests.Transcripts;

public sealed class TranscriptSegmentProjectionTests
{
    private static readonly SourceIdentity Source =
        new(new SessionId(new Guid("11111111-1111-1111-1111-111111111111")),
            new SourceId(new Guid("22222222-2222-2222-2222-222222222222")), new StreamEpoch(0));
    private static readonly SegmentId Segment = new(new Guid("33333333-3333-3333-3333-333333333333"));
    private static readonly TimelineEpochId Timeline = new(new Guid("44444444-4444-4444-4444-444444444444"));
    private static readonly TimelineInterval Interval =
        new(new TimelinePosition(Timeline, new SessionOffset(100)),
            new TimelinePosition(Timeline, new SessionOffset(200)));

    [Fact]
    public void ProvisionalRevisionsYieldOnlyToNewerPartialsOrCommittedEvidence()
    {
        var projection = new TranscriptSegmentProjection(Source, Segment);
        Assert.True(projection.TryApplyPartial(Partial(Source, 1, 1, "First synthetic partial.")));
        var latest = Partial(Source, 2, 3, "Current synthetic partial.");
        Assert.True(projection.TryApplyPartial(latest));
        Assert.False(projection.TryApplyPartial(Partial(Source, 3, 2, "Older synthetic partial.")));
        Assert.False(projection.TryApplyPartial(Partial(Source, 4, 3, "Equal synthetic revision.")));
        Assert.Same(latest, projection.Partial);
        Assert.Null(projection.CommittedRevision);

        var final = new FinalSegment(Header(Source, 5), Segment, new SegmentRevision(2),
            [Interval], TimingCertainty.Exact, "Synthetic final.");
        projection.ObserveCommitted(Entry(final, 1));
        Assert.False(projection.TryApplyPartial(Partial(Source, 6, 4, "Late synthetic partial.")));
        Assert.Null(projection.Partial);
        Assert.Equal(new SegmentRevision(2), projection.CommittedRevision);
    }

    [Fact]
    public void RehydratedProjectionKeepsNewestCommittedRevisionInItsStreamEpoch()
    {
        var projection = new TranscriptSegmentProjection(Source, Segment);
        var final = new FinalSegment(Header(Source, 1), Segment, new SegmentRevision(1),
            [Interval], TimingCertainty.Exact, "First synthetic final.");
        var amendment = new FinalAmendment(Header(Source, 2), Segment, new SegmentRevision(2),
            final.Revision, [Interval], TimingCertainty.Exact, "Amended synthetic final.");

        projection.ObserveCommitted(Entry(amendment, 2));
        projection.ObserveCommitted(Entry(final, 1));
        var nextEpoch = new SourceIdentity(Source.SessionId, Source.SourceId, new StreamEpoch(1));
        Assert.Throws<ArgumentException>(() => projection.TryApplyPartial(Partial(nextEpoch, 3, 3, "Other epoch.")));
        var wrongEpoch = new FinalSegment(Header(nextEpoch, 4), Segment, new SegmentRevision(3),
            [Interval], TimingCertainty.Exact, "Other epoch final.");
        Assert.Throws<ArgumentException>(() => projection.ObserveCommitted(Entry(wrongEpoch, 3)));
        var gap = new TranscriptGap(Header(Source, 5), Interval, GapReason.TransportDisconnected, TimingCertainty.Unknown);
        Assert.Throws<ArgumentException>(() => projection.ObserveCommitted(Entry(gap, 3)));
        Assert.Null(projection.Partial);
        Assert.Equal(amendment.Revision, projection.CommittedRevision);
    }

    private static PartialSegment Partial(SourceIdentity source, int eventNumber, long revision, string text) =>
        new(Header(source, eventNumber), Segment, new SegmentRevision(revision), Interval, text);

    private static TranscriptEventHeader Header(SourceIdentity source, int eventNumber) =>
        new(ContractVersion.Current, source, new EventId(new Guid(eventNumber, 0, 0, new byte[8])),
            new EventSequence(eventNumber));

    private static CommittedTranscriptEntry Entry(TranscriptEvent value, long version) =>
        new(new TranscriptCommitVersion(version), new TranscriptWrite(ContractVersion.Current, value));
}
