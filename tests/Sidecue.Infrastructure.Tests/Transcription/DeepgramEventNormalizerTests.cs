using System.Text.Json;
using Sidecue.Core.Contracts;
using Sidecue.Infrastructure.Transcription;
using Xunit;

namespace Sidecue.Infrastructure.Tests.Transcription;

public sealed class DeepgramEventNormalizerTests
{
    private static readonly SessionId Session = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    private static readonly SourceId SourceId = new(Guid.Parse("20000000-0000-0000-0000-000000000001"));
    private static readonly TimelineEpochId Timeline = new(Guid.Parse("30000000-0000-0000-0000-000000000001"));
    private static readonly AudioFormat Format = new(16_000, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian);

    [Fact]
    public void InterimRevisionsAndFinalKeepSegmentIdentityAndDoNotWaitForEndpoint()
    {
        var normalizer = NewNormalizer();
        Confirm(normalizer, 0, 0);

        var first = Assert.IsType<PartialSegment>(Assert.Single(normalizer.Normalize(Fixture("partial_initial"))));
        var next = Assert.IsType<PartialSegment>(Assert.Single(normalizer.Normalize(Fixture("partial_revision"))));
        var final = Assert.IsType<FinalSegment>(Assert.Single(normalizer.Normalize(Fixture("final"))));

        Assert.Equal(first.SegmentId, next.SegmentId);
        Assert.Equal(first.SegmentId, final.SegmentId);
        Assert.True(next.Revision.Value > first.Revision.Value);
        Assert.True(final.Revision.Value > next.Revision.Value);
        Assert.Equal("Synthetic hello again", final.Text);
        Assert.NotNull(final.Speaker);
        Assert.Equal(SourceId, final.Speaker.SourceId);
        Assert.Equal(0, final.Speaker.StreamEpoch.Value);
        Assert.Equal("0", final.Speaker.ProviderSpeakerId);
    }

    [Fact]
    public void DuplicateFinalReusesIdentityButIdenticalLaterWordsAreDistinct()
    {
        var normalizer = NewNormalizer();
        Confirm(normalizer, 0, 0);
        Confirm(normalizer, 1, 1_000_000);
        var first = Assert.IsType<FinalSegment>(Assert.Single(normalizer.Normalize(Fixture("final"))));
        var replay = Assert.IsType<FinalSegment>(Assert.Single(normalizer.Normalize(Fixture("final"))));
        var later = Assert.IsType<FinalSegment>(Assert.Single(normalizer.Normalize(Fixture("same_text_later"))));

        Assert.Equal(first.Header.EventId, replay.Header.EventId);
        Assert.Equal(first.Header.Sequence, replay.Header.Sequence);
        Assert.Equal(first.SegmentId, replay.SegmentId);
        Assert.Equal(first.Text, later.Text);
        Assert.NotEqual(first.SegmentId, later.SegmentId);
        Assert.NotEqual(first.Header.EventId, later.Header.EventId);
    }

    [Fact]
    public void SpeechFinalWithoutIsFinalDoesNotPromotePartial()
    {
        var normalizer = NewNormalizer();
        for (int i = 0; i < 3; i++) Confirm(normalizer, i, i * 1_000_000L);

        var events = normalizer.Normalize(Fixture("endpoint_nonfinal"));

        Assert.Single(events.OfType<PartialSegment>());
        Assert.Single(events.OfType<UtteranceBoundary>());
        Assert.Empty(events.OfType<FinalSegment>());
    }

    [Fact]
    public void MissingSpeechFinalOnInterimIsNotAnEndpoint()
    {
        var normalizer = NewNormalizer();
        Confirm(normalizer, 0, 0);
        const string json = "{\"type\":\"Results\",\"channel_index\":[0,1],\"start\":0,\"duration\":0.5,\"is_final\":false,\"channel\":{\"alternatives\":[{\"transcript\":\"Synthetic preliminary\",\"words\":[]}]}}";

        var events = normalizer.Normalize(json);
        Assert.Single(events.OfType<PartialSegment>());
        Assert.Empty(events.OfType<UtteranceBoundary>());
    }

    [Fact]
    public void EmptyEndpointProducesBoundaryAndMixedSpeakersNeverBecomeOneNamedSpeaker()
    {
        var normalizer = NewNormalizer();
        for (int i = 0; i < 5; i++) Confirm(normalizer, i, i * 1_000_000L);

        var mixed = normalizer.Normalize(Fixture("mixed_speakers"));
        var final = Assert.Single(mixed.OfType<FinalSegment>());
        Assert.Equal("Synthetic turn", final.Text);
        Assert.Null(final.Speaker);
        Assert.Single(mixed.OfType<UtteranceBoundary>());

        var empty = normalizer.Normalize(Fixture("empty_endpoint"));
        Assert.Single(empty.OfType<UtteranceBoundary>());
        Assert.Empty(empty.OfType<FinalSegment>());
    }

    [Fact]
    public void PartiallyMissingSpeakerLabelsDoNotAttributeWholeSegment()
    {
        var normalizer = NewNormalizer();
        Confirm(normalizer, 0, 0);
        const string json = "{\"type\":\"Results\",\"channel_index\":[0,1],\"start\":0,\"duration\":1,\"is_final\":true,\"speech_final\":false,\"channel\":{\"alternatives\":[{\"transcript\":\"Synthetic speaker labels\",\"words\":[{\"word\":\"Synthetic\",\"start\":0,\"end\":0.4,\"speaker\":0},{\"word\":\"speaker\",\"start\":0.4,\"end\":0.7},{\"word\":\"labels\",\"start\":0.7,\"end\":0.9,\"speaker\":0}]}]}}";

        var events = normalizer.Normalize(json);

        var final = Assert.Single(events.OfType<FinalSegment>());
        Assert.Equal("Synthetic speaker labels", final.Text);
        Assert.Null(final.Speaker);
        var status = Assert.Single(events.OfType<StreamStatus>());
        Assert.Equal(TranscriptionStreamState.Degraded, status.State);
        Assert.Equal(EventErrorCategory.UnsupportedCapability, status.Error);
    }

    [Fact]
    public void EmptyFinalWithWordsIsRejectedInsteadOfDiscardingSpeech()
    {
        var normalizer = NewNormalizer();
        Confirm(normalizer, 0, 0);
        const string json = "{\"type\":\"Results\",\"channel_index\":[0,1],\"start\":0,\"duration\":1,\"is_final\":true,\"speech_final\":false,\"channel\":{\"alternatives\":[{\"transcript\":\"\",\"words\":[{\"word\":\"Synthetic\",\"start\":0.1,\"end\":0.5}]}]}}";

        var error = Assert.Throws<DeepgramProtocolException>(() => normalizer.Normalize(json));
        Assert.Equal(DeepgramProtocolError.Malformed, error.Error);
    }

    [Fact]
    public void DiscontinuousFinalRetainsSeparateSourceIntervalsAndPartialDoesNotFlattenGap()
    {
        var normalizer = NewNormalizer();
        Confirm(normalizer, 0, 0);
        Confirm(normalizer, 2, 2_000_000);

        var partialError = Assert.Throws<DeepgramProtocolException>(() => normalizer.Normalize(Fixture("cross_gap_partial")));
        Assert.Equal(DeepgramProtocolError.UnsupportedTiming, partialError.Error);
        var final = Assert.IsType<FinalSegment>(Assert.Single(normalizer.Normalize(Fixture("cross_gap_final"))));
        Assert.Equal(2, final.Intervals.Length);
        AssertInterval(final.Intervals[0], 750_000, 1_000_000);
        AssertInterval(final.Intervals[1], 2_000_000, 2_250_000);
    }

    [Fact]
    public void UncertainSendProducesGapAndWhollyUnmappableFinalFailsExplicitly()
    {
        var normalizer = NewNormalizer();
        Confirm(normalizer, 0, 0);
        var gap = normalizer.RecordUncertain(Frame(1, 1_000_000));

        Assert.Equal(GapReason.UnknownSendCompletion, gap.Reason);
        Assert.Equal(TimingCertainty.Unknown, gap.Certainty);
        var exception = Assert.Throws<DeepgramProtocolException>(() => normalizer.Normalize(Fixture("uncertain_final")));
        Assert.Equal(DeepgramProtocolError.UnsupportedTiming, exception.Error);
        Assert.DoesNotContain("Synthetic uncertain", exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void KnownCaptureGapPreservesIdentityAndRejectsConflictsOrOtherSources()
    {
        var normalizer = NewNormalizer();
        var source = new SourceIdentity(Session, SourceId, new StreamEpoch(0));
        var gap = new CaptureGap(ContractVersion.Current, source,
            new TimelineInterval(Position(100_000), Position(200_000)), GapReason.QueueOverflow, TimingCertainty.Exact);
        var id = new EventId(Guid.Parse("40000000-0000-0000-0000-000000000001"));

        TranscriptGap emitted = normalizer.RecordGap(gap, id);
        TranscriptGap replay = normalizer.RecordGap(gap, id);
        Assert.Equal(id, emitted.Header.EventId);
        Assert.Equal(emitted.Header, replay.Header);
        Assert.Equal(gap.Interval, emitted.Interval);
        Assert.Equal(source, emitted.Header.Source);
        Assert.Equal(GapReason.QueueOverflow, emitted.Reason);

        var conflicting = new CaptureGap(ContractVersion.Current, source,
            new TimelineInterval(Position(100_000), Position(300_000)), GapReason.QueueOverflow, TimingCertainty.Exact);
        var conflict = Assert.Throws<DeepgramProtocolException>(() => normalizer.RecordGap(conflicting, id));
        Assert.Equal(DeepgramProtocolError.Conflict, conflict.Error);
        var otherSource = new SourceIdentity(Session, SourceId, new StreamEpoch(1));
        var foreign = new CaptureGap(ContractVersion.Current, otherSource, gap.Interval, gap.Reason, gap.Certainty);
        var mismatch = Assert.Throws<DeepgramProtocolException>(() => normalizer.RecordGap(foreign, new EventId(Guid.NewGuid())));
        Assert.Equal(DeepgramProtocolError.Malformed, mismatch.Error);
    }

    [Fact]
    public void SendOutcomesCannotPredateTheDeclaredStreamStart()
    {
        var confirmed = NewNormalizer(streamStart: 1_000_000);
        var uncertain = NewNormalizer(streamStart: 1_000_000);

        Assert.ThrowsAny<ArgumentException>(() => confirmed.RecordConfirmed(Frame(0, 0), TimingCertainty.Exact));
        Assert.ThrowsAny<ArgumentException>(() => uncertain.RecordUncertain(Frame(0, 0)));
        Confirm(confirmed, 1, 1_000_000);
        var final = Assert.IsType<FinalSegment>(Assert.Single(confirmed.Normalize(Fixture("final"))));
        Assert.Equal(1_000_000, final.Intervals[0].Start.Offset.Value);
    }

    [Fact]
    public void UtteranceEndAtDroppedIntervalUsesPreviousAudioEndAndStaleSentinelIsIgnored()
    {
        var normalizer = NewNormalizer();
        Confirm(normalizer, 0, 0);
        Confirm(normalizer, 2, 2_000_000);

        var boundary = Assert.IsType<UtteranceBoundary>(Assert.Single(normalizer.Normalize(
            "{\"type\":\"UtteranceEnd\",\"channel\":[0,1],\"last_word_end\":1.0}")));
        Assert.Equal(1_000_000, boundary.Position.Offset.Value);
        Assert.Empty(normalizer.Normalize("{\"type\":\"UtteranceEnd\",\"channel\":[0,1],\"last_word_end\":-1}"));
    }

    [Theory]
    [InlineData("[0]")]
    [InlineData("[0,1]")]
    public void UtteranceEndSupportsOfficialSingleAndPairChannelForms(string channel)
    {
        var normalizer = NewNormalizer();
        Confirm(normalizer, 0, 0);

        string json = $"{{\"type\":\"UtteranceEnd\",\"channel\":{channel},\"last_word_end\":0.5}}";
        var boundary = Assert.IsType<UtteranceBoundary>(Assert.Single(normalizer.Normalize(json)));
        Assert.Equal(500_000, boundary.Position.Offset.Value);
    }

    [Fact]
    public void DistinctProviderAnchorsThatRoundToSameSampleAreNotDeduplicated()
    {
        var normalizer = NewNormalizer();
        Confirm(normalizer, 0, 0);
        const string firstJson = "{\"type\":\"Results\",\"channel_index\":[0,1],\"start\":0.000001,\"duration\":0.000001,\"is_final\":true,\"speech_final\":false,\"channel\":{\"alternatives\":[{\"transcript\":\"same\",\"words\":[]}]}}";
        const string secondJson = "{\"type\":\"Results\",\"channel_index\":[0,1],\"start\":0.000003,\"duration\":0.000001,\"is_final\":true,\"speech_final\":false,\"channel\":{\"alternatives\":[{\"transcript\":\"same\",\"words\":[]}]}}";

        var first = Assert.IsType<FinalSegment>(Assert.Single(normalizer.Normalize(firstJson)));
        var second = Assert.IsType<FinalSegment>(Assert.Single(normalizer.Normalize(secondJson)));
        Assert.Equal(first.Intervals[0], second.Intervals[0]);
        Assert.NotEqual(first.SegmentId, second.SegmentId);
        Assert.NotEqual(first.Header.EventId, second.Header.EventId);
    }

    [Fact]
    public void OverlappingInterimsWithDifferentAnchorsAreExplicitlyAmbiguous()
    {
        var normalizer = NewNormalizer();
        Confirm(normalizer, 0, 0);
        _ = normalizer.Normalize(Fixture("partial_initial"));
        const string changedStart = "{\"type\":\"Results\",\"channel_index\":[0,1],\"start\":0.1,\"duration\":0.5,\"is_final\":false,\"speech_final\":false,\"channel\":{\"alternatives\":[{\"transcript\":\"Synthetic changed window\",\"words\":[]}]}}";

        var error = Assert.Throws<DeepgramProtocolException>(() => normalizer.Normalize(changedStart));
        Assert.Equal(DeepgramProtocolError.Conflict, error.Error);
        Assert.DoesNotContain("Synthetic changed window", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ExpandingAnExistingInterimAcrossAnotherWindowIsAConflict()
    {
        var normalizer = NewNormalizer();
        Confirm(normalizer, 0, 0);
        _ = normalizer.Normalize(Fixture("partial_initial"));
        const string secondWindow = "{\"type\":\"Results\",\"channel_index\":[0,1],\"start\":0.6,\"duration\":0.4,\"is_final\":false,\"speech_final\":false,\"channel\":{\"alternatives\":[{\"transcript\":\"Synthetic second\",\"words\":[]}]}}";
        _ = normalizer.Normalize(secondWindow);

        var error = Assert.Throws<DeepgramProtocolException>(() => normalizer.Normalize(Fixture("partial_revision")));
        Assert.Equal(DeepgramProtocolError.Conflict, error.Error);
    }

    [Fact]
    public void ReconnectedEpochAndSeparateSourceDoNotReuseEventOrSegmentIdentity()
    {
        var source = new SourceIdentity(Session, SourceId, new StreamEpoch(0));
        var reconnect = new SourceIdentity(Session, SourceId, new StreamEpoch(1));
        var other = new SourceIdentity(Session, new SourceId(Guid.Parse("20000000-0000-0000-0000-000000000002")), new StreamEpoch(0));
        var original = NewNormalizer(source);
        var resumed = NewNormalizer(reconnect);
        var remote = NewNormalizer(other);
        Confirm(original, 0, 0, source);
        Confirm(resumed, 0, 2_000_000, reconnect);
        Confirm(remote, 0, 0, other);

        var first = Assert.IsType<FinalSegment>(Assert.Single(original.Normalize(Fixture("final"))));
        var second = Assert.IsType<FinalSegment>(Assert.Single(resumed.Normalize(Fixture("final"))));
        var third = Assert.IsType<FinalSegment>(Assert.Single(remote.Normalize(Fixture("final"))));
        Assert.NotEqual(first.SegmentId, second.SegmentId);
        Assert.NotEqual(first.SegmentId, third.SegmentId);
        Assert.NotEqual(first.Header.EventId, second.Header.EventId);
        Assert.Equal(2_000_000, second.Intervals[0].Start.Offset.Value);
        Assert.Equal(reconnect, second.Header.Source);
    }

    [Fact]
    public void MalformedAndUnmappableInputsFailWithoutLeakingTranscriptText()
    {
        var normalizer = NewNormalizer();
        Confirm(normalizer, 0, 0);
        const string privateMarker = "synthetic-secret-never-log-719";
        string[] invalid =
        [
            "{\"type\":\"Results\",\"start\":-1,\"duration\":1,\"is_final\":true,\"speech_final\":false,\"channel\":{\"alternatives\":[{\"transcript\":\"" + privateMarker + "\",\"words\":[]}]}}",
            "{\"type\":\"Results\",\"start\":4,\"duration\":1,\"is_final\":true,\"speech_final\":false,\"channel\":{\"alternatives\":[{\"transcript\":\"" + privateMarker + "\",\"words\":[]}]}}",
            "{\"type\":\"Results\",\"transcript\":\"" + privateMarker + "\""
        ];
        foreach (string json in invalid)
        {
            var error = Assert.Throws<DeepgramProtocolException>(() => normalizer.Normalize(json));
            Assert.DoesNotContain(privateMarker, error.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void DuplicateConflictingJsonPropertyIsMalformedAndDoesNotFinalize()
    {
        var normalizer = NewNormalizer();
        Confirm(normalizer, 0, 0);
        const string duplicate = "{\"type\":\"Results\",\"channel_index\":[0,1],\"start\":0,\"duration\":1,\"is_final\":true,\"is_final\":false,\"speech_final\":false,\"channel\":{\"alternatives\":[{\"transcript\":\"Synthetic duplicate property\",\"words\":[]}]}}";

        var error = Assert.Throws<DeepgramProtocolException>(() => normalizer.Normalize(duplicate));
        Assert.Equal(DeepgramProtocolError.Malformed, error.Error);
        var final = Assert.IsType<FinalSegment>(Assert.Single(normalizer.Normalize(Fixture("final"))));
        Assert.Equal("Synthetic hello again", final.Text);
    }

    [Theory]
    [InlineData("0.000000000000000000000000000001")]
    [InlineData("0.000000000000000000000000000002")]
    public void ProviderTimestampsThatExceedDecimalPrecisionAreRejectedWithoutMutatingState(string start)
    {
        var normalizer = NewNormalizer();
        Confirm(normalizer, 0, 0);
        var first = Assert.IsType<PartialSegment>(Assert.Single(normalizer.Normalize(Fixture("partial_initial"))));
        string json = $"{{\"type\":\"Results\",\"channel_index\":[0,1],\"start\":{start},\"duration\":0.5,\"is_final\":false,\"speech_final\":false,\"channel\":{{\"alternatives\":[{{\"transcript\":\"Synthetic precise\",\"words\":[]}}]}}}}";

        var error = Assert.Throws<DeepgramProtocolException>(() => normalizer.Normalize(json));
        Assert.Equal(DeepgramProtocolError.Malformed, error.Error);
        var next = Assert.IsType<PartialSegment>(Assert.Single(normalizer.Normalize(Fixture("partial_revision"))));
        Assert.Equal(first.SegmentId, next.SegmentId);
        Assert.Equal(first.Revision.Value + 1, next.Revision.Value);
    }

    [Fact]
    public void PartialWithUnreliableSourceTimestampDoesNotAppearSilentlyPrecise()
    {
        var normalizer = NewNormalizer();
        var source = new SourceIdentity(Session, SourceId, new StreamEpoch(0));
        var frame = new AudioFrameMetadata(ContractVersion.Current, source, new FrameSequence(0),
            new TimelineInterval(Position(0), Position(1_000_000)), Format, 16_000, AudioFrameAttributes.TimestampError);
        normalizer.RecordConfirmed(frame, TimingCertainty.Exact);

        try
        {
            var events = normalizer.Normalize(Fixture("partial_initial"));
            Assert.Single(events.OfType<PartialSegment>());
            Assert.Contains(events.OfType<StreamStatus>(), status => status.State == TranscriptionStreamState.Degraded);
        }
        catch (DeepgramProtocolException error)
        {
            Assert.Equal(DeepgramProtocolError.UnsupportedTiming, error.Error);
        }
    }

    [Fact]
    public void BoundaryWithUnreliableSourceTimestampReportsDegradedTiming()
    {
        var normalizer = NewNormalizer();
        var source = new SourceIdentity(Session, SourceId, new StreamEpoch(0));
        var frame = new AudioFrameMetadata(ContractVersion.Current, source, new FrameSequence(0),
            new TimelineInterval(Position(0), Position(1_000_000)), Format, 16_000, AudioFrameAttributes.TimestampError);
        normalizer.RecordConfirmed(frame, TimingCertainty.Exact);

        var events = normalizer.Normalize("{\"type\":\"UtteranceEnd\",\"channel\":[0],\"last_word_end\":0.5}");

        Assert.Single(events.OfType<UtteranceBoundary>());
        Assert.Contains(events.OfType<StreamStatus>(), status => status.State == TranscriptionStreamState.Degraded);
    }

    [Fact]
    public void BoundedSegmentCapacityFailsExplicitlyInsteadOfDroppingAFinal()
    {
        var normalizer = NewNormalizer(maximumSegments: 1);
        Confirm(normalizer, 0, 0);
        Confirm(normalizer, 1, 1_000_000);
        _ = normalizer.Normalize(Fixture("final"));

        var error = Assert.Throws<DeepgramProtocolException>(() => normalizer.Normalize(Fixture("same_text_later")));
        Assert.Equal(DeepgramProtocolError.CapacityExceeded, error.Error);
    }

    private static DeepgramEventNormalizer NewNormalizer(SourceIdentity? source = null, int maximumSegments = 16, long streamStart = 0)
    {
        source ??= new SourceIdentity(Session, SourceId, new StreamEpoch(0));
        var request = new TranscriptionRequest(ContractVersion.Current, source, "en-US", Format,
            new ProviderConfigurationReference("synthetic-deepgram", "fixture-1"), Position(streamStart));
        return new DeepgramEventNormalizer(request, maximumSegments: maximumSegments, maximumIntervals: 16);
    }

    private static void Confirm(DeepgramEventNormalizer normalizer, long sequence, long start, SourceIdentity? source = null) =>
        normalizer.RecordConfirmed(Frame(sequence, start, source), TimingCertainty.Exact);

    private static AudioFrameMetadata Frame(long sequence, long start, SourceIdentity? source = null) =>
        new(ContractVersion.Current, source ?? new SourceIdentity(Session, SourceId, new StreamEpoch(0)),
            new FrameSequence(sequence), new TimelineInterval(Position(start), Position(start + 1_000_000)), Format, 16_000);

    private static TimelinePosition Position(long offset) => new(Timeline, new SessionOffset(offset));

    private static string Fixture(string key)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "deepgram-results.json")));
        return document.RootElement.GetProperty(key).GetRawText();
    }

    private static void AssertInterval(TimelineInterval interval, long start, long end)
    {
        Assert.Equal(start, interval.Start.Offset.Value);
        Assert.Equal(end, interval.End.Offset.Value);
    }
}
