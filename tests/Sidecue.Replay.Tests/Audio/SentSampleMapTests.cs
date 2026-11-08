using Sidecue.Application.Timing;
using Sidecue.Core.Contracts;
using Xunit;

namespace Sidecue.Replay.Tests.Audio;

public sealed class SentSampleMapTests
{
    private static readonly SessionId Session = new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly SourceId SourceId = new(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly TimelineEpochId Timeline = new(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    private static readonly SourceIdentity Source = new(Session, SourceId, new StreamEpoch(4));
    private static readonly AudioFormat Format = new(16_000, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian);

    [Fact]
    public void DroppedMiddleIntervalDoesNotShiftLaterProviderTime()
    {
        var map = NewMap();
        map.RecordConfirmed(Frame(0, 0, 1_000_000), TimingCertainty.Exact);
        // The middle captured interval was never sent, so it is never recorded.
        map.RecordConfirmed(Frame(2, 2_000_000, 3_000_000), TimingCertainty.Exact);

        Assert.Equal(32_000, map.ConfirmedSamples);
        Assert.Equal(2_250_000, map.MapPoint(20_000).Position?.Offset.Value);
        Assert.Equal(2_000_000, map.MapPoint(16_000).Position?.Offset.Value);
        Assert.Equal(3_000_000, map.MapPoint(32_000).Position?.Offset.Value);
        var spanning = map.Map(14_400, 17_600);
        Assert.Equal(TimingCertainty.Exact, spanning.Certainty);
        Assert.False(spanning.ContainsUncertainSend);
        Assert.Equal(2, spanning.SupportedIntervals.Count);
        AssertInterval(spanning.SupportedIntervals[0], 900_000, 1_000_000);
        AssertInterval(spanning.SupportedIntervals[1], 2_000_000, 2_100_000);
    }

    [Fact]
    public void UnknownSendDoesNotAdvanceAndSealsTheConnectionMap()
    {
        var map = NewMap();
        map.RecordConfirmed(Frame(0, 0, 1_000_000), TimingCertainty.Exact);
        var ambiguous = Frame(1, 1_000_000, 2_000_000);
        map.RecordUncertain(ambiguous);

        Assert.Equal(16_000, map.ConfirmedSamples);
        Assert.True(map.IsSealed);
        Assert.Same(ambiguous, map.UncertainSend);
        Assert.Throws<InvalidOperationException>(() => map.RecordConfirmed(Frame(2, 2_000_000, 3_000_000), TimingCertainty.Exact));
        Assert.Throws<InvalidOperationException>(() => map.RecordUncertain(Frame(2, 2_000_000, 3_000_000)));
        var spanning = map.Map(14_400, 17_600);
        Assert.True(spanning.ContainsUncertainSend);
        Assert.Equal(TimingCertainty.Unknown, spanning.Certainty);
        AssertInterval(Assert.Single(spanning.SupportedIntervals), 900_000, 1_000_000);
        var onlyUncertain = map.Map(16_000, 17_600);
        Assert.Empty(onlyUncertain.SupportedIntervals);
        Assert.Equal(TimingCertainty.Unknown, onlyUncertain.Certainty);
        Assert.Null(map.MapPoint(16_000).Position);
        Assert.Equal(TimingCertainty.Unknown, map.MapPoint(16_000).Certainty);
        Assert.Throws<ArgumentOutOfRangeException>(() => map.Map(16_000, 32_001));
        Assert.Equal(500_000, map.MapPoint(8_000).Position?.Offset.Value);
    }

    [Fact]
    public void TimingUncertaintyNeverBecomesExact()
    {
        var map = NewMap();
        map.RecordConfirmed(Frame(0, 0, 1_000_000, flags: AudioFrameAttributes.SessionTimestampEstimated), TimingCertainty.Exact);
        map.RecordConfirmed(Frame(1, 1_000_000, 2_000_000, flags: AudioFrameAttributes.TimestampError), TimingCertainty.Exact);

        Assert.Equal(TimingCertainty.Estimated, map.Map(0, 16_000).Certainty);
        Assert.Equal(TimingCertainty.Unknown, map.Map(16_000, 32_000).Certainty);
        Assert.Equal(TimingCertainty.Unknown, map.Map(8_000, 24_000).Certainty);
        Assert.Equal(TimingCertainty.Unknown, map.MapPoint(16_000).Certainty);
    }

    [Fact]
    public void NativeDiscontinuityDoesNotMergeAndMakesCrossingUncertain()
    {
        var map = NewMap();
        map.RecordConfirmed(Frame(0, 0, 1_000_000), TimingCertainty.Exact);
        map.RecordConfirmed(Frame(1, 1_000_000, 2_000_000, flags: AudioFrameAttributes.Discontinuity), TimingCertainty.Exact);

        Assert.Equal(2, map.IntervalCount);
        var crossing = map.Map(8_000, 24_000);
        Assert.Equal(2, crossing.SupportedIntervals.Count);
        Assert.Equal(TimingCertainty.Unknown, crossing.Certainty);
        Assert.Equal(TimingCertainty.Exact, map.Map(16_000, 24_000).Certainty);
    }

    [Fact]
    public void SkippedSequenceWithTouchingTimestampsKeepsUnknownLossBoundary()
    {
        var map = NewMap();
        map.RecordConfirmed(Frame(0, 0, 1_000_000), TimingCertainty.Exact);
        map.RecordConfirmed(Frame(2, 1_000_000, 2_000_000), TimingCertainty.Exact);

        Assert.Equal(2, map.IntervalCount);
        var spanning = map.Map(8_000, 24_000);
        Assert.Equal(2, spanning.SupportedIntervals.Count);
        Assert.Equal(TimingCertainty.Unknown, spanning.Certainty);
        Assert.Equal(TimingCertainty.Exact, map.Map(16_000, 24_000).Certainty);
    }

    [Fact]
    public void IncorrectIdentityFormatOrderAndOverlapAreRejectedWithoutAdvancing()
    {
        var map = NewMap();
        map.RecordConfirmed(Frame(1, 0, 1_000_000), TimingCertainty.Exact);
        var otherSource = new SourceIdentity(Session, new SourceId(Guid.NewGuid()), Source.StreamEpoch);
        var otherEpoch = new SourceIdentity(Session, SourceId, new StreamEpoch(5));
        var otherFormat = new AudioFormat(16_000, 1, AudioSampleRepresentation.Pcm24, AudioByteOrder.LittleEndian);
        var otherTimeline = new TimelineEpochId(Guid.NewGuid());

        Assert.Throws<ArgumentException>(() => map.RecordConfirmed(Frame(2, 1_000_000, 2_000_000, source: otherSource), TimingCertainty.Exact));
        Assert.Throws<ArgumentException>(() => map.RecordConfirmed(Frame(2, 1_000_000, 2_000_000, source: otherEpoch), TimingCertainty.Exact));
        Assert.Throws<ArgumentException>(() => map.RecordConfirmed(Frame(2, 1_000_000, 2_000_000, format: otherFormat), TimingCertainty.Exact));
        Assert.Throws<ArgumentException>(() => map.RecordConfirmed(Frame(2, 1_000_000, 2_000_000, timeline: otherTimeline), TimingCertainty.Exact));
        Assert.Throws<ArgumentException>(() => map.RecordConfirmed(Frame(1, 1_000_000, 2_000_000), TimingCertainty.Exact));
        Assert.Throws<ArgumentException>(() => map.RecordConfirmed(Frame(2, 500_000, 1_500_000), TimingCertainty.Exact));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.RecordConfirmed(Frame(2, 1_000_000, 2_000_000), (TimingCertainty)99));
        Assert.Equal(16_000, map.ConfirmedSamples);
        Assert.False(map.IsSealed);
        Assert.Equal(1_000_000, map.MapPoint(16_000).Position?.Offset.Value);
    }

    [Fact]
    public void BoundedMapCoalescesCompatibleIntervalsAndRejectsExcessWithoutMutation()
    {
        var map = NewMap(maximumIntervals: 1);
        map.RecordConfirmed(Frame(0, 0, 1_000_000), TimingCertainty.Exact);
        map.RecordConfirmed(Frame(1, 1_000_000, 2_000_000), TimingCertainty.Exact);
        Assert.Equal(1, map.IntervalCount);
        Assert.Equal(32_000, map.ConfirmedSamples);
        Assert.Equal(1_500_000, map.MapPoint(24_000).Position?.Offset.Value);

        Assert.Throws<InvalidOperationException>(() => map.RecordConfirmed(Frame(2, 3_000_000, 4_000_000), TimingCertainty.Exact));
        Assert.Equal(32_000, map.ConfirmedSamples);
        Assert.Equal(1, map.IntervalCount);
        Assert.Equal(2_000_000, map.MapPoint(32_000).Position?.Offset.Value);
    }

    [Fact]
    public void FractionalMicrosecondBoundariesAreDeterministicAndBounded()
    {
        var format = new AudioFormat(44_100, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian);
        var map = new SentSampleMap(Source, Timeline, format, 2);
        var frame = new AudioFrameMetadata(ContractVersion.Current, Source, new FrameSequence(0), Interval(0, 45), format, 2);
        map.RecordConfirmed(frame, TimingCertainty.Exact);

        Assert.Equal(22, map.MapPoint(1).Position?.Offset.Value);
        AssertInterval(Assert.Single(map.Map(0, 1).SupportedIntervals), 0, 22);
        AssertInterval(Assert.Single(map.Map(1, 2).SupportedIntervals), 22, 45);
        Assert.Throws<ArgumentOutOfRangeException>(() => map.Map(0, long.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(() => map.Map(2, 2));
    }

    private static SentSampleMap NewMap(int maximumIntervals = 8) => new(Source, Timeline, Format, maximumIntervals);

    private static AudioFrameMetadata Frame(long sequence, long start, long end,
        SourceIdentity? source = null, AudioFormat? format = null, TimelineEpochId? timeline = null,
        AudioFrameAttributes flags = AudioFrameAttributes.None) => new(ContractVersion.Current,
            source ?? Source, new FrameSequence(sequence), Interval(start, end, timeline), format ?? Format, 16_000, flags);

    private static TimelineInterval Interval(long start, long end, TimelineEpochId? timeline = null) => new(
        new TimelinePosition(timeline ?? Timeline, new SessionOffset(start)),
        new TimelinePosition(timeline ?? Timeline, new SessionOffset(end)));

    private static void AssertInterval(TimelineInterval interval, long start, long end)
    {
        Assert.Equal(start, interval.Start.Offset.Value);
        Assert.Equal(end, interval.End.Offset.Value);
    }
}
