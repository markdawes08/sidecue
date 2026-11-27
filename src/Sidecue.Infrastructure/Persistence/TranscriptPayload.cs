using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sidecue.Core.Contracts;

namespace Sidecue.Infrastructure.Persistence;

internal sealed record TranscriptPayload(
    string Kind, TimelineEpochId Timeline, long StartOffset, long EndOffset, TimingCertainty Certainty,
    SegmentId? Segment, SegmentRevision? Revision, SegmentRevision? Amends, string? IntervalsJson, string? Text,
    string? SpeakerKey, string? SpeakerSpansJson, GapReason? GapReason, byte[] Hash, byte[] RevisionHash)
{
    internal const string FinalKind = "final";
    internal const string AmendmentKind = "amendment";
    internal const string GapKind = "gap";

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public override string ToString() => nameof(TranscriptPayload);

    internal static TranscriptPayload Encode(TranscriptWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        try
        {
            byte[] revisionHash = FingerprintRevision(write);
            byte[] hash = FingerprintEvent(write.Event.Header, revisionHash);
            return write.Event switch
            {
                FinalSegment final => EncodeRevision(FinalKind, final.SegmentId, final.Revision, null,
                    final.Intervals, final.Certainty, final.Text, final.Speaker, final.SpeakerSpans, hash, revisionHash),
                FinalAmendment amendment => EncodeRevision(AmendmentKind, amendment.SegmentId, amendment.Revision,
                    amendment.AmendsRevision, amendment.Intervals, amendment.Certainty, amendment.Text,
                    amendment.Speaker, amendment.SpeakerSpans, hash, revisionHash),
                TranscriptGap gap => new TranscriptPayload(GapKind, gap.Interval.TimelineEpoch,
                    gap.Interval.Start.Offset.Value, gap.Interval.End.Offset.Value, gap.Certainty, null, null, null,
                    null, null, null, null, gap.Reason, hash, revisionHash),
                _ => throw new ArgumentException("The event is not a durable transcript change.", nameof(write))
            };
        }
        catch (EncoderFallbackException)
        {
            throw new ArgumentException("Transcript text and speaker labels must contain valid Unicode.", nameof(write));
        }
    }

    internal static TranscriptWrite Decode(
        TranscriptEventHeader header, string kind, TimelineEpochId timeline, long startOffset, long? endOffset,
        TimingCertainty certainty, SegmentId? segment, SegmentRevision? revision, SegmentRevision? amends,
        string? intervalsJson, string? text, string? speakerKey, string? speakerSpansJson, GapReason? gapReason)
    {
        try
        {
            if (kind == GapKind && gapReason is { } reason)
            {
                // An older row can record only a known start. Represent that as an
                // uncertainty marker without assigning a duration.
                var gap = new TranscriptGap(header, Interval(timeline, startOffset, endOffset ?? startOffset),
                    reason, endOffset is null ? TimingCertainty.Unknown : certainty);
                return new TranscriptWrite(header.Version, gap);
            }

            if (segment is not { } segmentId || revision is not { } revisionId || intervalsJson is null || text is null)
                throw InvalidStoredPayload();

            using JsonDocument document = JsonDocument.Parse(intervalsJson);
            var intervals = ReadIntervals(document.RootElement, timeline);
            if (intervals.Length == 0 || intervals[0].Start.Offset.Value != startOffset ||
                intervals[^1].End.Offset.Value != endOffset)
            {
                throw InvalidStoredPayload();
            }

            _ = StrictUtf8.GetByteCount(text);
            SpeakerKey? speaker = speakerKey is null ? null : ScopedSpeaker(header.Source, speakerKey);
            WordSpeakerSpan[] spans = ReadSpans(speakerSpansJson, timeline, header.Source);
            TranscriptEvent value = kind switch
            {
                FinalKind when amends is null => new FinalSegment(header, segmentId, revisionId,
                    intervals, certainty, text, speaker, spans),
                AmendmentKind when amends is { } superseded => new FinalAmendment(header, segmentId, revisionId,
                    superseded, intervals, certainty, text, speaker, spans),
                _ => throw InvalidStoredPayload()
            };
            return new TranscriptWrite(header.Version, value);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or JsonException or FormatException)
        {
            throw InvalidStoredPayload();
        }
    }

    private static TranscriptPayload EncodeRevision(
        string kind, SegmentId segment, SegmentRevision revision, SegmentRevision? amends,
        ImmutableArray<TimelineInterval> intervals, TimingCertainty certainty, string text,
        SpeakerKey? speaker, ImmutableArray<WordSpeakerSpan> spans, byte[] hash, byte[] revisionHash)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        WriteIntervals(writer, intervals);
        writer.Flush();
        string intervalsJson = Encoding.UTF8.GetString(buffer.WrittenSpan);

        string? spansJson = null;
        if (!spans.IsEmpty)
        {
            buffer.Clear();
            writer.Reset(buffer);
            writer.WriteStartArray();
            foreach (WordSpeakerSpan span in spans)
            {
                writer.WriteStartArray();
                writer.WriteNumberValue(span.WordIndex);
                writer.WriteStringValue(span.Speaker.ProviderSpeakerId);
                writer.WriteStringValue(CertaintyName(span.Certainty));
                WriteIntervals(writer, span.Intervals);
                writer.WriteEndArray();
            }
            writer.WriteEndArray();
            writer.Flush();
            spansJson = Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        return new TranscriptPayload(kind, intervals[0].TimelineEpoch, intervals[0].Start.Offset.Value,
            intervals[^1].End.Offset.Value, certainty, segment, revision, amends, intervalsJson, text,
            speaker?.ProviderSpeakerId, spansJson, null, hash, revisionHash);
    }

    // BinaryWriter provides fixed little-endian integers and length-prefixed UTF-8 strings.
    // Keep this layout stable across runtimes and independent of JSON escaping.
    internal static byte[] FingerprintRevision(TranscriptWrite write)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, StrictUtf8);
        var header = write.Event.Header;
        writer.Write("sidecue.transcript.revision.v1");
        writer.Write(write.Version.Value);
        writer.Write(header.Version.Value);
        writer.Write(header.Source.SessionId.Value.ToByteArray(bigEndian: true));
        writer.Write(header.Source.SourceId.Value.ToByteArray(bigEndian: true));
        writer.Write(header.Source.StreamEpoch.Value);
        switch (write.Event)
        {
            case FinalSegment final:
                writer.Write(FinalKind);
                WriteRevision(writer, final.SegmentId, final.Revision, final.Intervals, final.Certainty,
                    final.Text, final.Speaker, final.SpeakerSpans);
                break;
            case FinalAmendment amendment:
                writer.Write(AmendmentKind);
                writer.Write(amendment.AmendsRevision.Value);
                WriteRevision(writer, amendment.SegmentId, amendment.Revision, amendment.Intervals,
                    amendment.Certainty, amendment.Text, amendment.Speaker, amendment.SpeakerSpans);
                break;
            case TranscriptGap gap:
                writer.Write(GapKind);
                writer.Write(gap.Interval.TimelineEpoch.Value.ToByteArray(bigEndian: true));
                writer.Write(gap.Interval.Start.Offset.Value);
                writer.Write(gap.Interval.End.Offset.Value);
                writer.Write(gap.Reason.ToString());
                writer.Write(CertaintyName(gap.Certainty));
                break;
            default:
                throw new ArgumentException("The event is not a durable transcript change.", nameof(write));
        }
        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    private static byte[] FingerprintEvent(TranscriptEventHeader header, byte[] revisionHash)
    {
        using var buffer = new MemoryStream();
        using var writer = new BinaryWriter(buffer, StrictUtf8);
        writer.Write("sidecue.transcript.event.v1");
        writer.Write(header.EventId.Value.ToByteArray(bigEndian: true));
        writer.Write(header.Sequence.Value);
        writer.Write(revisionHash);
        return SHA256.HashData(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
    }

    private static void WriteRevision(BinaryWriter writer, SegmentId segment, SegmentRevision revision,
        ImmutableArray<TimelineInterval> intervals, TimingCertainty certainty, string text, SpeakerKey? speaker,
        ImmutableArray<WordSpeakerSpan> spans)
    {
        writer.Write(segment.Value.ToByteArray(bigEndian: true));
        writer.Write(revision.Value);
        writer.Write(intervals[0].TimelineEpoch.Value.ToByteArray(bigEndian: true));
        WriteIntervals(writer, intervals);
        writer.Write(CertaintyName(certainty));
        writer.Write(text);
        writer.Write(speaker is not null);
        if (speaker is not null)
            writer.Write(speaker.ProviderSpeakerId);

        writer.Write(spans.Length);
        foreach (WordSpeakerSpan span in spans)
        {
            writer.Write(span.WordIndex);
            writer.Write(span.Speaker.ProviderSpeakerId);
            writer.Write(CertaintyName(span.Certainty));
            WriteIntervals(writer, span.Intervals);
        }
    }

    private static void WriteIntervals(BinaryWriter writer, ImmutableArray<TimelineInterval> intervals)
    {
        writer.Write(intervals.Length);
        foreach (TimelineInterval interval in intervals)
        {
            writer.Write(interval.Start.Offset.Value);
            writer.Write(interval.End.Offset.Value);
        }
    }

    private static void WriteIntervals(Utf8JsonWriter writer, ImmutableArray<TimelineInterval> intervals)
    {
        writer.WriteStartArray();
        foreach (TimelineInterval interval in intervals)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(interval.Start.Offset.Value);
            writer.WriteNumberValue(interval.End.Offset.Value);
            writer.WriteEndArray();
        }
        writer.WriteEndArray();
    }

    private static TimelineInterval[] ReadIntervals(JsonElement list, TimelineEpochId timeline)
    {
        var intervals = new TimelineInterval[list.GetArrayLength()];
        int index = 0;
        foreach (JsonElement pair in list.EnumerateArray())
        {
            if (pair.GetArrayLength() != 2)
                throw InvalidStoredPayload();

            intervals[index++] = Interval(timeline, pair[0].GetInt64(), pair[1].GetInt64());
        }
        return intervals;
    }

    private static WordSpeakerSpan[] ReadSpans(string? json, TimelineEpochId timeline, SourceIdentity source)
    {
        if (json is null)
            return [];

        using JsonDocument document = JsonDocument.Parse(json);
        var spans = new WordSpeakerSpan[document.RootElement.GetArrayLength()];
        int index = 0;
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.GetArrayLength() != 4 || item[1].GetString() is not { } label)
                throw InvalidStoredPayload();

            spans[index++] = new WordSpeakerSpan(item[0].GetInt32(), ScopedSpeaker(source, label),
                ReadIntervals(item[3], timeline), ParseCertainty(item[2].GetString()));
        }
        return spans;
    }

    private static SpeakerKey ScopedSpeaker(SourceIdentity source, string label)
    {
        _ = StrictUtf8.GetByteCount(label);
        return new SpeakerKey(source.SourceId, source.StreamEpoch, label);
    }

    private static TimelineInterval Interval(TimelineEpochId timeline, long start, long end) =>
        new(new TimelinePosition(timeline, new SessionOffset(start)), new TimelinePosition(timeline, new SessionOffset(end)));

    internal static string CertaintyName(TimingCertainty certainty) => certainty switch
    {
        TimingCertainty.Exact => "exact",
        TimingCertainty.Estimated => "estimated",
        TimingCertainty.Unknown => "unknown",
        _ => throw new ArgumentOutOfRangeException(nameof(certainty), "Unknown transcript timing certainty.")
    };

    internal static TimingCertainty ParseCertainty(string? value) => value switch
    {
        "exact" => TimingCertainty.Exact,
        "estimated" => TimingCertainty.Estimated,
        "unknown" => TimingCertainty.Unknown,
        _ => throw InvalidStoredPayload()
    };

    private static EncryptedDatabaseException InvalidStoredPayload() =>
        new("Stored transcript data is invalid. Recovery is required.");
}
