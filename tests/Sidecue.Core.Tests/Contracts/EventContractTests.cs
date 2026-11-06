using Sidecue.Core.Contracts;
using Xunit;

namespace Sidecue.Core.Tests.Contracts;

public sealed class EventContractTests
{
    [Fact]
    public void DefaultIdentifiersCannotEnterEventEnvelopes()
    {
        Assert.ThrowsAny<ArgumentException>(() => new TranscriptEventHeader(
            ContractVersion.Current, ContractSamples.SourceIdentity(), default, new EventSequence(0)));
        Assert.ThrowsAny<ArgumentException>(() => new PartialSegment(
            TranscriptHeader(), default, new SegmentRevision(0), ContractSamples.Interval(), "synthetic text"));
        Assert.ThrowsAny<ArgumentException>(() => new AnswerFence(default, new ContextEpoch(0), ContractSamples.Request));
        Assert.ThrowsAny<ArgumentException>(() => new AnswerFence(ContractSamples.Session, new ContextEpoch(0), default));
    }

    [Fact]
    public void UnsupportedAndDefaultContractVersionsCannotEnterEnvelopes()
    {
        ContractVersion[] versions = [default, new(2)];
        foreach (var version in versions)
        {
            Assert.ThrowsAny<ArgumentException>(() => new TranscriptEventHeader(
                version, ContractSamples.SourceIdentity(), ContractSamples.Event, new EventSequence(0)));
            Assert.ThrowsAny<ArgumentException>(() => new AnswerEventHeader(version, Fence(), new AnswerSequence(0)));
            Assert.ThrowsAny<ArgumentException>(() => new AudioFrameMetadata(version, ContractSamples.SourceIdentity(),
                new FrameSequence(0), ContractSamples.Interval(),
                new AudioFormat(16_000, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian), 1_600));
        }
    }

    [Fact]
    public void AmendmentMustAdvanceTheFinalizedRevision()
    {
        var intervals = new[] { ContractSamples.Interval() };

        Assert.ThrowsAny<ArgumentException>(() => new FinalAmendment(TranscriptHeader(), ContractSamples.Segment,
            new SegmentRevision(3), new SegmentRevision(3), intervals, TimingCertainty.Exact, "synthetic correction"));
        Assert.ThrowsAny<ArgumentException>(() => new FinalAmendment(TranscriptHeader(), ContractSamples.Segment,
            new SegmentRevision(2), new SegmentRevision(3), intervals, TimingCertainty.Exact, "synthetic correction"));
    }

    [Fact]
    public void SpeakerLabelCannotCrossSourcesOrConnectionEpochs()
    {
        SpeakerKey[] mismatches =
        [
            new(new SourceId(Guid.Parse("77777777-7777-7777-7777-777777777777")), new StreamEpoch(0), "speaker-0"),
            new(ContractSamples.Source, new StreamEpoch(1), "speaker-0")
        ];

        Assert.All(mismatches, speaker => Assert.ThrowsAny<ArgumentException>(() => new PartialSegment(
            TranscriptHeader(), ContractSamples.Segment, new SegmentRevision(0), ContractSamples.Interval(),
            "synthetic text", speaker)));
    }

    [Fact]
    public void FinalizedIntervalsAreCopiedBeforeCallerMutation()
    {
        var original = ContractSamples.Interval(0, 100);
        var intervals = new List<TimelineInterval> { original };
        var final = new FinalSegment(TranscriptHeader(), ContractSamples.Segment, new SegmentRevision(1),
            intervals, TimingCertainty.Exact, "synthetic final");
        var amendment = new FinalAmendment(TranscriptHeader(), ContractSamples.Segment, new SegmentRevision(2),
            new SegmentRevision(1), intervals, TimingCertainty.Exact, "synthetic correction");

        intervals[0] = ContractSamples.Interval(500, 600);
        intervals.Clear();

        Assert.Equal(original, Assert.Single(final.Intervals));
        Assert.Equal(original, Assert.Single(amendment.Intervals));
    }

    [Fact]
    public void FinalizedIntervalsRequireOrderedSupportInOneTimeline()
    {
        var anotherEpoch = new TimelineEpochId(Guid.Parse("99999999-9999-9999-9999-999999999999"));
        TimelineInterval[][] invalidIntervals =
        [
            [],
            [ContractSamples.Interval(0, 100), ContractSamples.Interval(50, 150)],
            [ContractSamples.Interval(100, 200), ContractSamples.Interval(0, 50)],
            [ContractSamples.Interval(0, 100), ContractSamples.Interval(100, 200, anotherEpoch)]
        ];

        Assert.All(invalidIntervals, intervals => Assert.ThrowsAny<ArgumentException>(() => new FinalSegment(
            TranscriptHeader(), ContractSamples.Segment, new SegmentRevision(1), intervals,
            TimingCertainty.Exact, "synthetic final")));
    }

    [Fact]
    public void AnswerFenceDistinguishesEachStalenessBoundary()
    {
        var active = Fence();
        var otherSession = new AnswerFence(new SessionId(Guid.Parse("88888888-8888-8888-8888-888888888888")),
            active.ContextEpoch, active.RequestId);
        var afterClear = new AnswerFence(active.SessionId, new ContextEpoch(1), active.RequestId);
        var replacementRequest = new AnswerFence(active.SessionId, active.ContextEpoch,
            new RequestId(Guid.Parse("77777777-7777-7777-7777-777777777777")));

        var keys = new HashSet<AnswerFence> { active, Fence(), otherSession, afterClear, replacementRequest };

        Assert.Equal(4, keys.Count);
    }

    [Fact]
    public void AnswerEvidenceIsCopiedBeforeCallerMutation()
    {
        var citation = new CitationId("synthetic-segment-1");
        var citations = new List<CitationId> { citation };
        var update = new AnswerEvidenceUpdated(AnswerHeader(), citations);
        var completed = new AnswerCompleted(AnswerHeader(), "synthetic answer", citations, "fixture", "fixture-model");

        citations[0] = new CitationId("synthetic-segment-2");
        citations.Clear();

        Assert.Equal(citation, Assert.Single(update.CitationIds));
        Assert.Equal(citation, Assert.Single(completed.VerifiedCitationIds));
    }

    [Fact]
    public void DiagnosticFormattingDoesNotExposeMeetingContent()
    {
        const string sensitiveText = "Synthetic confidential meeting content";
        object[] events =
        [
            new PartialSegment(TranscriptHeader(), ContractSamples.Segment, new SegmentRevision(0),
                ContractSamples.Interval(), sensitiveText),
            new FinalSegment(TranscriptHeader(), ContractSamples.Segment, new SegmentRevision(1),
                [ContractSamples.Interval()], TimingCertainty.Exact, sensitiveText),
            new FinalAmendment(TranscriptHeader(), ContractSamples.Segment, new SegmentRevision(2), new SegmentRevision(1),
                [ContractSamples.Interval()], TimingCertainty.Exact, sensitiveText),
            new AnswerTextDelta(AnswerHeader(), sensitiveText),
            new AnswerCompleted(AnswerHeader(), sensitiveText, [], "fixture", "fixture-model")
        ];

        Assert.All(events, item => Assert.DoesNotContain(sensitiveText, item.ToString(), StringComparison.Ordinal));
    }

    private static TranscriptEventHeader TranscriptHeader() =>
        new(ContractVersion.Current, ContractSamples.SourceIdentity(), ContractSamples.Event, new EventSequence(0));

    private static AnswerFence Fence() => new(ContractSamples.Session, new ContextEpoch(0), ContractSamples.Request);

    private static AnswerEventHeader AnswerHeader() => new(ContractVersion.Current, Fence(), new AnswerSequence(0));
}
