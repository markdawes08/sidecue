using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sidecue.Application.Timing;
using Sidecue.Core.Contracts;

namespace Sidecue.Infrastructure.Transcription;

/// <summary>Normalizes one Deepgram connection's untrusted messages without network I/O.</summary>
/// <remarks>
/// The owner serializes send outcomes and messages. Create a new instance with a new stream epoch
/// after any uncertain send or reconnect. Exact provider start values identify segment windows
/// within that connection; overlapping or changed finalized windows are explicit conflicts.
/// Deepgram does not document individual result IDs, so this is a conservative local protocol
/// assumption, not a guarantee of identity across reconnects or process restarts.
/// Text is never used alone as identity, logged, or retained in the replay index.
/// </remarks>
public sealed class DeepgramEventNormalizer
{
    private readonly TranscriptionRequest request;
    private readonly SentSampleMap sent;
    private readonly int maximumSegments;
    private readonly int maximumMessageBytes;
    private readonly int maximumEvents;
    private readonly Dictionary<decimal, Segment> segments = [];
    private readonly Dictionary<decimal, BoundaryReplay> boundaries = [];
    private readonly Dictionary<EventId, (CaptureGap Gap, TranscriptGap Event)> captureGaps = [];
    private readonly HashSet<EventId> emittedIds = [];
    private long sequence;
    private int eventCount;
    private decimal finalizedThrough;

    public DeepgramEventNormalizer(TranscriptionRequest request, int maximumSegments = 4096,
        int maximumIntervals = 4096, int maximumMessageBytes = 262144, int maximumEvents = 16384)
    {
        this.request = request ?? throw new ArgumentNullException(nameof(request));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumSegments);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumIntervals);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumMessageBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEvents);
        if (request.Format.Channels != 1) throw new ArgumentException("Each speech source must have one channel.", nameof(request));
        this.maximumSegments = maximumSegments;
        this.maximumMessageBytes = maximumMessageBytes;
        this.maximumEvents = maximumEvents;
        sent = new SentSampleMap(request.Source, request.StreamStart.TimelineEpoch, request.Format, maximumIntervals);
    }

    /// <summary>Record a send only once its completion is confirmed.</summary>
    public void RecordConfirmed(AudioFrameMetadata frame, TimingCertainty certainty)
    {
        ValidateFrameStart(frame);
        sent.RecordConfirmed(frame, certainty);
    }

    /// <summary>Seal the current epoch and expose the uncertain source interval.</summary>
    public TranscriptGap RecordUncertain(AudioFrameMetadata frame)
    {
        ValidateFrameStart(frame);
        EnsureCapacity(1);
        EnsureIdAvailable(Id("uncertain", frame.Sequence.Value.ToString(CultureInfo.InvariantCulture)));
        sent.RecordUncertain(frame);
        var header = NewHeader("uncertain", frame.Sequence.Value.ToString(CultureInfo.InvariantCulture));
        return new TranscriptGap(header, frame.Interval, GapReason.UnknownSendCompletion, TimingCertainty.Unknown);
    }

    /// <summary>Forward a capture gap with the upstream stable event identity.</summary>
    /// <remarks>The caller must forward capture-status gaps; recording a later send does not imply a precise missing interval.</remarks>
    public TranscriptGap RecordGap(CaptureGap gap, EventId eventId)
    {
        ArgumentNullException.ThrowIfNull(gap);
        if (eventId.Value == Guid.Empty || gap.Source != request.Source ||
            gap.Interval.TimelineEpoch != request.StreamStart.TimelineEpoch ||
            gap.Interval.Start.Offset.Value < request.StreamStart.Offset.Value)
            throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);
        if (captureGaps.TryGetValue(eventId, out var previous))
        {
            if (previous.Gap != gap) throw new DeepgramProtocolException(DeepgramProtocolError.Conflict);
            return previous.Event;
        }
        EnsureCapacity(1);
        EnsureIdAvailable(eventId.Value);
        emittedIds.Add(eventId);
        eventCount++;
        var header = new TranscriptEventHeader(ContractVersion.Current, request.Source, eventId, new EventSequence(sequence++));
        var result = new TranscriptGap(header, gap.Interval, gap.Reason, gap.Certainty);
        captureGaps.Add(eventId, (gap, result));
        return result;
    }

    private void ValidateFrameStart(AudioFrameMetadata frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Interval.Start.Offset.Value < request.StreamStart.Offset.Value)
            throw new ArgumentException("The frame cannot precede this stream epoch.", nameof(frame));
    }

    /// <summary>Parse a single response; exact duplicate events retain their original identity and sequence.</summary>
    public IReadOnlyList<TranscriptEvent> Normalize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > maximumMessageBytes) throw new DeepgramProtocolException(DeepgramProtocolError.CapacityExceeded);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 32 });
            JsonElement root = doc.RootElement;
            RejectDuplicateProperties(root);
            string type = RequiredString(root, "type");
            return type switch
            {
                "Results" => Results(root),
                "UtteranceEnd" => UtteranceEnd(root),
                "Metadata" or "SpeechStarted" => [],
                _ => throw new DeepgramProtocolException(DeepgramProtocolError.UnsupportedMessage)
            };
        }
        catch (DeepgramProtocolException) { throw; }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException or OverflowException or ArgumentException)
        {
            throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);
        }
    }

    private List<TranscriptEvent> Results(JsonElement root)
    {
        RequireMonoChannel(root, "channel_index", results: true);
        decimal start = RequiredNumber(root, "start");
        decimal duration = RequiredNumber(root, "duration");
        if (start < 0 || duration < 0) throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);
        decimal end = checked(start + duration);
        bool final = RequiredBool(root, "is_final");
        bool speechFinal = root.TryGetProperty("speech_final", out _) && RequiredBool(root, "speech_final");
        JsonElement channel = Required(root, "channel");
        JsonElement alternatives = Required(channel, "alternatives");
        if (alternatives.ValueKind != JsonValueKind.Array || alternatives.GetArrayLength() == 0)
            throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);
        JsonElement alternative = alternatives[0];
        string text = RequiredString(alternative, "transcript");
        (SpeakerKey? speaker, bool mixed, decimal? lastWordEnd) = ReadWords(alternative, start, end);
        if (text.Length == 0 && lastWordEnd.HasValue)
            throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);
        if (text.Length != 0 && duration <= 0) throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);

        // Map every required point before mutating any identity/revision state.
        SentAudioRange? range = text.Length == 0 ? null : MapRange(start, end);
        if (range is not null && (range.SupportedIntervals.Count == 0 || (!final && (range.SupportedIntervals.Count != 1 || range.ContainsUncertainSend))))
            throw new DeepgramProtocolException(DeepgramProtocolError.UnsupportedTiming);
        decimal boundaryTime = lastWordEnd ?? end;
        SentAudioPoint? boundary = speechFinal ? MapPrecedingBoundary(boundaryTime) : null;
        Segment? segment = null;
        string? fingerprint = null;
        Replay? replay = null;
        bool isNewSegment = false;
        if (text.Length != 0)
        {
            if (!segments.TryGetValue(start, out segment))
            {
                if (start < finalizedThrough) throw new DeepgramProtocolException(DeepgramProtocolError.Conflict);
                if (segments.Count >= maximumSegments) throw new DeepgramProtocolException(DeepgramProtocolError.CapacityExceeded);
                segment = new Segment(new SegmentId(Id("segment", Exact(start))));
                isNewSegment = true;
            }
            fingerprint = Fingerprint(start, duration, final, text, speaker, mixed, alternative);
            segment.Replays.TryGetValue(fingerprint, out replay);
            if (segment.Final is not null && replay is null)
                throw new DeepgramProtocolException(DeepgramProtocolError.Conflict);
            if (replay is null && segment.Final is null && start < finalizedThrough)
                throw new DeepgramProtocolException(DeepgramProtocolError.Conflict);
            if (replay is null && segments.Any(entry => entry.Key != start && entry.Value.Final is null &&
                entry.Value.LastPartialEnd is { } priorEnd && entry.Key < end && start < priorEnd))
                throw new DeepgramProtocolException(DeepgramProtocolError.Conflict);
        }

        bool partialTimingUnknown = segment is not null && !final && range!.Certainty == TimingCertainty.Unknown;
        bool newBoundary = boundary is not null && !boundaries.ContainsKey(boundaryTime);
        int additional = (segment is not null && replay is null ? 1 : 0) +
            (mixed && replay is null ? 1 : 0) + (partialTimingUnknown && replay is null ? 1 : 0) +
            (newBoundary ? (boundary!.Certainty == TimingCertainty.Unknown ? 2 : 1) : 0);
        EnsureCapacity(additional);
        // Preflight every generated event identity before mutating revisions, sequence,
        // or the segment index. An upstream gap ID may deliberately collide.
        var pendingIds = new HashSet<Guid>();
        if (segment is not null && replay is null)
        {
            string key = $"{Exact(start)}:{segment.NextRevision}";
            PreflightId(Id(final ? "final" : "partial", key), pendingIds);
            if (mixed) PreflightId(Id("mixed", key), pendingIds);
            if (partialTimingUnknown) PreflightId(Id("partial-timing", key), pendingIds);
        }
        if (newBoundary)
        {
            PreflightId(Id("boundary", Exact(boundaryTime)), pendingIds);
            if (boundary!.Certainty == TimingCertainty.Unknown)
                PreflightId(Id("boundary-timing", Exact(boundaryTime)), pendingIds);
        }
        if (isNewSegment) segments.Add(start, segment!);
        var events = new List<TranscriptEvent>(3);
        if (segment is not null)
        {
            if (replay is null)
            {
                long revision = segment.NextRevision++;
                var header = NewHeader(final ? "final" : "partial", $"{Exact(start)}:{revision}");
                TranscriptEventHeader? mixedHeader = mixed ? NewHeader("mixed", $"{Exact(start)}:{revision}") : null;
                TranscriptEventHeader? timingHeader = partialTimingUnknown ? NewHeader("partial-timing", $"{Exact(start)}:{revision}") : null;
                replay = new Replay(header, new SegmentRevision(revision), mixedHeader, timingHeader);
                segment.Replays.Add(fingerprint!, replay);
                if (final)
                {
                    segment.Final = replay;
                    finalizedThrough = Math.Max(finalizedThrough, end);
                }
                else segment.LastPartialEnd = end;
            }
            if (final)
            {
                var certainty = range!.Certainty == TimingCertainty.Exact ? TimingCertainty.Estimated : range.Certainty;
                events.Add(new FinalSegment(replay.Header, segment.Id, replay.Revision, range.SupportedIntervals, certainty, text, speaker));
            }
            else events.Add(new PartialSegment(replay.Header, segment.Id, replay.Revision, range!.SupportedIntervals[0], text, speaker));
            if (replay.MixedHeader is not null)
                events.Add(new StreamStatus(replay.MixedHeader, TranscriptionStreamState.Degraded, EventErrorCategory.UnsupportedCapability));
            if (replay.TimingHeader is not null)
                events.Add(new StreamStatus(replay.TimingHeader, TranscriptionStreamState.Degraded, EventErrorCategory.Unknown));
        }
        if (boundary is not null) events.AddRange(Boundary(boundaryTime, boundary));
        return events;
    }

    private List<TranscriptEvent> UtteranceEnd(JsonElement root)
    {
        RequireMonoChannel(root, "channel", results: false);
        decimal end = RequiredNumber(root, "last_word_end");
        if (end == -1) return [];
        if (end < 0) throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);
        var position = MapPrecedingBoundary(end);
        if (!boundaries.ContainsKey(end))
        {
            EnsureCapacity(position.Certainty == TimingCertainty.Unknown ? 2 : 1);
            var pendingIds = new HashSet<Guid>();
            PreflightId(Id("boundary", Exact(end)), pendingIds);
            if (position.Certainty == TimingCertainty.Unknown) PreflightId(Id("boundary-timing", Exact(end)), pendingIds);
        }
        return Boundary(end, position);
    }

    private List<TranscriptEvent> Boundary(decimal providerTime, SentAudioPoint position)
    {
        if (!boundaries.TryGetValue(providerTime, out var replay))
        {
            var header = NewHeader("boundary", Exact(providerTime));
            var timing = position.Certainty == TimingCertainty.Unknown ? NewHeader("boundary-timing", Exact(providerTime)) : null;
            replay = new BoundaryReplay(header, timing);
            boundaries.Add(providerTime, replay);
        }
        var events = new List<TranscriptEvent>(2) { new UtteranceBoundary(replay.Header, position.Position!) };
        if (replay.TimingHeader is not null)
            events.Add(new StreamStatus(replay.TimingHeader, TranscriptionStreamState.Degraded, EventErrorCategory.Unknown));
        return events;
    }

    private SentAudioRange MapRange(decimal start, decimal end)
    {
        try
        {
            long first = checked((long)decimal.Floor(start * request.Format.SampleRate));
            long last = checked((long)decimal.Ceiling(end * request.Format.SampleRate));
            if (last <= first) throw new DeepgramProtocolException(DeepgramProtocolError.UnsupportedTiming);
            return sent.Map(first, last);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        { throw new DeepgramProtocolException(DeepgramProtocolError.UnsupportedTiming); }
    }

    private SentAudioPoint MapPrecedingBoundary(decimal seconds)
    {
        try
        {
            long sample = checked((long)decimal.Ceiling(seconds * request.Format.SampleRate));
            if (sample == 0)
            {
                SentAudioPoint point = sent.MapPoint(0);
                return point.Position is not null ? point : throw new DeepgramProtocolException(DeepgramProtocolError.UnsupportedTiming);
            }
            // MapPoint chooses the following segment at a discontinuity; a last-word end
            // instead belongs to the preceding sample and must not jump across a gap.
            SentAudioRange range = sent.Map(sample - 1, sample);
            if (range.ContainsUncertainSend || range.SupportedIntervals.Count != 1)
                throw new DeepgramProtocolException(DeepgramProtocolError.UnsupportedTiming);
            return new SentAudioPoint(range.SupportedIntervals[0].End, range.Certainty);
        }
        catch (Exception exception) when (exception is ArgumentException or OverflowException)
        { throw new DeepgramProtocolException(DeepgramProtocolError.UnsupportedTiming); }
    }

    private (SpeakerKey? Speaker, bool Mixed, decimal? LastEnd) ReadWords(JsonElement alternative, decimal start, decimal end)
    {
        if (!alternative.TryGetProperty("words", out JsonElement words)) return (null, false, null);
        if (words.ValueKind != JsonValueKind.Array) throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);
        int? firstSpeaker = null;
        bool missing = false, mixed = false;
        decimal? last = null;
        foreach (JsonElement word in words.EnumerateArray())
        {
            decimal wordStart = RequiredNumber(word, "start"), wordEnd = RequiredNumber(word, "end");
            if (wordStart < start || wordEnd < wordStart || wordEnd > end)
                throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);
            last = last.HasValue ? Math.Max(last.Value, wordEnd) : wordEnd;
            if (!word.TryGetProperty("speaker", out JsonElement value) || value.ValueKind == JsonValueKind.Null) { missing = true; continue; }
            if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out int speaker) || speaker < 0)
                throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);
            if (firstSpeaker.HasValue && firstSpeaker.Value != speaker) mixed = true;
            firstSpeaker ??= speaker;
        }
        bool ambiguous = mixed || (missing && firstSpeaker.HasValue);
        return (firstSpeaker.HasValue && !ambiguous
            ? new SpeakerKey(request.Source.SourceId, request.Source.StreamEpoch, firstSpeaker.Value.ToString(CultureInfo.InvariantCulture)) : null, ambiguous, last);
    }

    private static void RequireMonoChannel(JsonElement root, string field, bool results)
    {
        JsonElement value = Required(root, field);
        if (value.ValueKind != JsonValueKind.Array || (results ? value.GetArrayLength() != 2 : value.GetArrayLength() is < 1 or > 2)
            || !value[0].TryGetInt32(out int index) || index != 0
            || (value.GetArrayLength() == 2 && (!value[1].TryGetInt32(out int channels) || channels != 1)))
            throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);
    }

    private static JsonElement Required(JsonElement obj, string field) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(field, out var value)
            ? value : throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);
    private static string RequiredString(JsonElement obj, string field) =>
        Required(obj, field).ValueKind == JsonValueKind.String ? Required(obj, field).GetString()! : throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);
    private static decimal RequiredNumber(JsonElement obj, string field)
    {
        JsonElement value = Required(obj, field);
        if (value.ValueKind != JsonValueKind.Number) throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);
        string raw = value.GetRawText();
        if (raw.Length > 128) throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);
        int exponentIndex = raw.IndexOfAny(['e', 'E']);
        string mantissa = exponentIndex < 0 ? raw : raw[..exponentIndex];
        int exponent = 0;
        if (exponentIndex >= 0 && !int.TryParse(raw.AsSpan(exponentIndex + 1), NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture, out exponent)) throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);
        int point = mantissa.IndexOf('.');
        int fractionalDigits = point < 0 ? 0 : mantissa.Length - point - 1;
        string digits = mantissa.TrimStart('-').Replace(".", "", StringComparison.Ordinal).TrimStart('0');
        if (digits.Length == 0) return 0;
        long scale = (long)fractionalDigits - exponent;
        while (digits.Length > 0 && digits[^1] == '0') { digits = digits[..^1]; scale--; }
        if (digits.Length > 29 || scale > 28 || scale < -28 ||
            !BigInteger.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out BigInteger magnitude))
            throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);
        if (scale < 0) magnitude *= BigInteger.Pow(10, checked((int)-scale));
        if (magnitude > (BigInteger.One << 96) - BigInteger.One || !value.TryGetDecimal(out decimal result))
            throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);
        return result;
    }
    private static bool RequiredBool(JsonElement obj, string field) => Required(obj, field).ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new DeepgramProtocolException(DeepgramProtocolError.Malformed)
    };

    private static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new DeepgramProtocolException(DeepgramProtocolError.Malformed);
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in value.EnumerateArray()) RejectDuplicateProperties(item);
        }
    }

    private void EnsureCapacity(int additional)
    {
        if (additional > maximumEvents - eventCount) throw new DeepgramProtocolException(DeepgramProtocolError.CapacityExceeded);
    }

    private TranscriptEventHeader NewHeader(string kind, string key)
    {
        Guid value = Id(kind, key);
        EnsureIdAvailable(value);
        eventCount++;
        var eventId = new EventId(value);
        emittedIds.Add(eventId);
        return new TranscriptEventHeader(ContractVersion.Current, request.Source, eventId, new EventSequence(sequence++));
    }

    private void EnsureIdAvailable(Guid id)
    {
        if (id == Guid.Empty || emittedIds.Contains(new EventId(id)))
            throw new DeepgramProtocolException(DeepgramProtocolError.Conflict);
    }

    private void PreflightId(Guid id, HashSet<Guid> pending)
    {
        EnsureIdAvailable(id);
        if (!pending.Add(id)) throw new DeepgramProtocolException(DeepgramProtocolError.Conflict);
    }

    private Guid Id(string kind, string key)
    {
        string input = FormattableString.Invariant($"sidecue:deepgram:v1:{request.Source.SessionId.Value:D}:{request.Source.SourceId.Value:D}:{request.Source.StreamEpoch.Value}:{kind}:{key}");
        byte[] digest = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return new Guid(digest.AsSpan(0, 16));
    }

    private static string Fingerprint(decimal start, decimal duration, bool final, string text, SpeakerKey? speaker, bool mixed, JsonElement alternative)
    {
        // This verifies exact replay of a provider window; text alone never identifies an event.
        string canonical = FormattableString.Invariant($"{Exact(start)}|{Exact(duration)}|{final}|{speaker?.ProviderSpeakerId}|{mixed}|{text}|{alternative.GetRawText()}");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static string Exact(decimal value) => value.ToString("G29", CultureInfo.InvariantCulture);
    private sealed class Segment(SegmentId id)
    {
        public SegmentId Id { get; } = id;
        public long NextRevision { get; set; }
        public Replay? Final { get; set; }
        public decimal? LastPartialEnd { get; set; }
        public Dictionary<string, Replay> Replays { get; } = [];
    }
    private sealed record Replay(TranscriptEventHeader Header, SegmentRevision Revision, TranscriptEventHeader? MixedHeader,
        TranscriptEventHeader? TimingHeader);
    private sealed record BoundaryReplay(TranscriptEventHeader Header, TranscriptEventHeader? TimingHeader);
}
