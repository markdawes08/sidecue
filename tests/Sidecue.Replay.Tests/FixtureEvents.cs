using System.Text.Json;
using Sidecue.Core.Contracts;

namespace Sidecue.Replay.Tests;

/// <summary>Explicit fixture schema, not a production provider or persistence decoder.</summary>
internal static class FixtureEvents
{
    internal static TranscriptEvent Read(JsonElement item, SourceIdentity source, TimelineEpochId timeline)
    {
        var header = new TranscriptEventHeader(ContractVersion.Current, source,
            new EventId(item.GetProperty("eventId").GetGuid()), new EventSequence(item.GetProperty("sequence").GetInt64()));
        var start = new TimelinePosition(timeline, new SessionOffset(item.GetProperty("startUs").GetInt64()));
        var end = new TimelinePosition(timeline, new SessionOffset(item.GetProperty("endUs").GetInt64()));
        var interval = new TimelineInterval(start, end);
        var kind = Text(item, "kind");
        if (kind == "gap") { return new TranscriptGap(header, interval, GapReason.QueueOverflow, TimingCertainty.Exact); }
        if (kind == "boundary") { return new UtteranceBoundary(header, end); }
        if (kind == "status") { return new StreamStatus(header, TranscriptionStreamState.Live); }
        var segment = new SegmentId(item.GetProperty("segmentId").GetGuid());
        var revision = new SegmentRevision(item.GetProperty("revision").GetInt64());
        var text = Text(item, "text");
        var speaker = item.TryGetProperty("speaker", out var label)
            ? new SpeakerKey(source.SourceId, source.StreamEpoch, label.GetString()!) : null;
        return kind switch
        {
            "partial" => new PartialSegment(header, segment, revision, interval, text, speaker),
            "final" => new FinalSegment(header, segment, revision, [interval], TimingCertainty.Exact, text, speaker),
            "amendment" => new FinalAmendment(header, segment, revision,
                new SegmentRevision(item.GetProperty("amendsRevision").GetInt64()), [interval], TimingCertainty.Exact, text, speaker),
            _ => throw new ArgumentException("Unknown transcript fixture kind.", nameof(item))
        };
    }

    internal static string Text(JsonElement item, string property) =>
        item.GetProperty(property).GetString() ?? throw new ArgumentException("A fixture string is null.", nameof(item));
}
