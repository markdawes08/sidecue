using System.Security.Cryptography;
using System.Text.Json;
using Sidecue.Core.Contracts;

namespace Sidecue.Replay.Tests.Reference;

// Explicit schema for synthetic replay only. Record equality does not compare
// ImmutableArray contents, and diagnostic ToString deliberately omits text.
internal static class TranscriptFingerprint
{
    internal static string Compute(TranscriptEvent value) => Hash(value, includeHeader: true);
    internal static string Payload(TranscriptEvent value) => Hash(value, includeHeader: false);

    private static string Hash(TranscriptEvent value, bool includeHeader)
    {
        ArgumentNullException.ThrowIfNull(value);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            if (includeHeader)
            {
                var header = value.Header;
                writer.WriteNumberValue(header.Version.Value);
                writer.WriteStringValue(header.Source.SessionId.Value);
                writer.WriteStringValue(header.Source.SourceId.Value);
                writer.WriteNumberValue(header.Source.StreamEpoch.Value);
                writer.WriteStringValue(header.EventId.Value);
                writer.WriteNumberValue(header.Sequence.Value);
            }
            switch (value)
            {
                case PartialSegment partial:
                    writer.WriteStringValue(nameof(PartialSegment));
                    writer.WriteStringValue(partial.SegmentId.Value);
                    writer.WriteNumberValue(partial.Revision.Value);
                    Interval(writer, partial.Interval);
                    writer.WriteStringValue(partial.Text);
                    Speaker(writer, partial.Speaker);
                    break;
                case FinalSegment final:
                    writer.WriteStringValue(nameof(FinalSegment));
                    Final(writer, final.SegmentId, final.Revision, final.Intervals, final.Certainty, final.Text, final.Speaker);
                    break;
                case FinalAmendment amendment:
                    writer.WriteStringValue(nameof(FinalAmendment));
                    writer.WriteNumberValue(amendment.AmendsRevision.Value);
                    Final(writer, amendment.SegmentId, amendment.Revision, amendment.Intervals,
                        amendment.Certainty, amendment.Text, amendment.Speaker);
                    break;
                case TranscriptGap gap:
                    writer.WriteStringValue(nameof(TranscriptGap));
                    Interval(writer, gap.Interval);
                    writer.WriteNumberValue((int)gap.Reason);
                    writer.WriteNumberValue((int)gap.Certainty);
                    break;
                case UtteranceBoundary boundary:
                    writer.WriteStringValue(nameof(UtteranceBoundary));
                    writer.WriteStringValue(boundary.Position.TimelineEpoch.Value);
                    writer.WriteNumberValue(boundary.Position.Offset.Value);
                    break;
                case StreamStatus status:
                    writer.WriteStringValue(nameof(StreamStatus));
                    writer.WriteNumberValue((int)status.State);
                    if (status.Error is { } error) { writer.WriteNumberValue((int)error); }
                    else { writer.WriteNullValue(); }
                    break;
                default:
                    throw new ArgumentException("Unsupported replay transcript event.", nameof(value));
            }
            writer.WriteEndArray();
        }
        return Convert.ToHexStringLower(SHA256.HashData(stream.ToArray()));
    }

    private static void Final(Utf8JsonWriter writer, SegmentId segment, SegmentRevision revision,
        IEnumerable<TimelineInterval> intervals, TimingCertainty certainty, string text, SpeakerKey? speaker)
    {
        writer.WriteStringValue(segment.Value);
        writer.WriteNumberValue(revision.Value);
        writer.WriteStartArray();
        foreach (var interval in intervals) { Interval(writer, interval); }
        writer.WriteEndArray();
        writer.WriteNumberValue((int)certainty);
        writer.WriteStringValue(text);
        Speaker(writer, speaker);
    }

    private static void Interval(Utf8JsonWriter writer, TimelineInterval interval)
    {
        writer.WriteStartArray();
        writer.WriteStringValue(interval.TimelineEpoch.Value);
        writer.WriteNumberValue(interval.Start.Offset.Value);
        writer.WriteNumberValue(interval.End.Offset.Value);
        writer.WriteEndArray();
    }

    private static void Speaker(Utf8JsonWriter writer, SpeakerKey? speaker)
    {
        if (speaker is null) { writer.WriteNullValue(); return; }
        writer.WriteStartArray();
        writer.WriteStringValue(speaker.SourceId.Value);
        writer.WriteNumberValue(speaker.StreamEpoch.Value);
        writer.WriteStringValue(speaker.ProviderSpeakerId);
        writer.WriteEndArray();
    }
}
