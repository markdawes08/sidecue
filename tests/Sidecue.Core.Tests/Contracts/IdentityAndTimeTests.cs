using Sidecue.Core.Contracts;
using Xunit;

namespace Sidecue.Core.Tests.Contracts;

public sealed class IdentityAndTimeTests
{
    [Fact]
    public void EmptyGuidCannotConstructAnIdentifier()
    {
        Action[] constructors =
        [
            () => _ = new SessionId(Guid.Empty),
            () => _ = new SourceId(Guid.Empty),
            () => _ = new TimelineEpochId(Guid.Empty),
            () => _ = new EventId(Guid.Empty),
            () => _ = new SegmentId(Guid.Empty),
            () => _ = new RequestId(Guid.Empty),
            () => _ = new ProfileVersionId(Guid.Empty)
        ];

        Assert.All(constructors, constructor => Assert.ThrowsAny<ArgumentException>(constructor));
    }

    [Fact]
    public void NegativeCountersAndOffsetsAreRejected()
    {
        Action[] constructors =
        [
            () => _ = new StreamEpoch(-1),
            () => _ = new FrameSequence(-1),
            () => _ = new SegmentRevision(-1),
            () => _ = new TranscriptCommitVersion(-1),
            () => _ = new ContextEpoch(-1),
            () => _ = new EventSequence(-1),
            () => _ = new AnswerSequence(-1),
            () => _ = new SessionOffset(-1),
            () => _ = new RawClockPosition(-1, 10_000_000),
            () => _ = new RawClockPosition(0, 0)
        ];

        Assert.All(constructors, constructor => Assert.Throws<ArgumentOutOfRangeException>(constructor));
    }

    [Fact]
    public void DefaultIdentifiersCannotEnterSourceOrTimelineRecords()
    {
        Assert.ThrowsAny<ArgumentException>(() => new SourceIdentity(default, ContractSamples.Source, new StreamEpoch(0)));
        Assert.ThrowsAny<ArgumentException>(() => new SourceIdentity(ContractSamples.Session, default, new StreamEpoch(0)));
        Assert.ThrowsAny<ArgumentException>(() => new TimelinePosition(default, new SessionOffset(0)));
    }

    [Fact]
    public void ReopeningASourceCannotCollideWithItsPreviousIdentity()
    {
        var original = ContractSamples.SourceIdentity();
        var reopened = ContractSamples.SourceIdentity(1);
        var anotherSource = new SourceIdentity(ContractSamples.Session,
            new SourceId(Guid.Parse("77777777-7777-7777-7777-777777777777")), new StreamEpoch(0));
        var anotherSession = new SourceIdentity(
            new SessionId(Guid.Parse("88888888-8888-8888-8888-888888888888")), ContractSamples.Source, new StreamEpoch(0));

        var keys = new HashSet<SourceIdentity> { original, reopened, anotherSource, anotherSession };

        Assert.Equal(4, keys.Count);
    }

    [Fact]
    public void IntervalRejectsReversedOffsets()
    {
        Assert.ThrowsAny<ArgumentException>(() => ContractSamples.Interval(100, 99));
    }

    [Fact]
    public void IntervalRejectsOffsetsFromDifferentProcessLifetimes()
    {
        var restartedTimeline = new TimelineEpochId(Guid.Parse("99999999-9999-9999-9999-999999999999"));

        Assert.ThrowsAny<ArgumentException>(() =>
            new TimelineInterval(ContractSamples.Position(0), ContractSamples.Position(100, restartedTimeline)));
    }

    [Fact]
    public void HistoryTimeNormalizesToUtcWithoutBecomingAnElapsedOffset()
    {
        var local = new DateTimeOffset(2026, 11, 6, 10, 30, 0, TimeSpan.FromHours(-5));

        var timestamp = new UtcTimestamp(local);

        Assert.Equal(TimeSpan.Zero, timestamp.Value.Offset);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 15, 30, 0, TimeSpan.Zero), timestamp.Value);
    }
}
