using System.Collections.Immutable;

namespace Sidecue.Core.Contracts;

public enum TimingCertainty
{
    Exact,
    Estimated,
    Unknown
}

public enum GapReason
{
    QueueOverflow,
    DeviceUnavailable,
    TransportDisconnected,
    UnknownSendCompletion,
    CaptureFailure,
    CaptureDiscontinuity
}

public enum EventErrorCategory
{
    Unknown,
    PermissionDenied,
    DeviceUnavailable,
    ProviderAuthentication,
    ProviderRateLimit,
    ProviderUnavailable,
    ProtocolConflict,
    StorageUnavailable,
    CorruptData,
    Cancelled,
    UnsupportedCapability
}

/// <summary>An anonymous provider label scoped to a source connection, not a person's identity.</summary>
public sealed record SpeakerKey
{
    public SpeakerKey(SourceId sourceId, StreamEpoch streamEpoch, string providerSpeakerId)
    {
        ContractGuard.Guid(sourceId.Value, nameof(sourceId));
        ContractGuard.Text(providerSpeakerId, nameof(providerSpeakerId));
        SourceId = sourceId;
        StreamEpoch = streamEpoch;
        ProviderSpeakerId = providerSpeakerId;
    }

    public SourceId SourceId { get; }
    public StreamEpoch StreamEpoch { get; }
    public string ProviderSpeakerId { get; }

    public override string ToString() => $"SpeakerKey {{ SourceId = {SourceId}, StreamEpoch = {StreamEpoch} }}";
}

/// <summary>Anonymous speaker evidence for one word in the provider's result order.</summary>
/// <remarks>The index refers to the provider's word list. It is not a character or token offset in the transcript text.</remarks>
public sealed record WordSpeakerSpan
{
    public WordSpeakerSpan(int wordIndex, SpeakerKey speaker, IEnumerable<TimelineInterval> intervals,
        TimingCertainty certainty)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(wordIndex);
        ArgumentNullException.ThrowIfNull(speaker);
        EventGuard.Defined(certainty, nameof(certainty));
        WordIndex = wordIndex;
        Speaker = speaker;
        Intervals = EventGuard.Intervals(intervals);
        Certainty = certainty;
    }

    public int WordIndex { get; }
    public SpeakerKey Speaker { get; }
    public ImmutableArray<TimelineInterval> Intervals { get; }
    public TimingCertainty Certainty { get; }
}

internal static class EventGuard
{
    internal static void Defined<T>(T value, string name) where T : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(name, "Unknown contract enum value.");
        }
    }

    internal static void Speaker(SpeakerKey? speaker, SourceIdentity source)
    {
        if (speaker is not null && (speaker.SourceId != source.SourceId || speaker.StreamEpoch != source.StreamEpoch))
        {
            throw new ArgumentException("The speaker key must belong to the event source and stream epoch.", nameof(speaker));
        }
    }

    internal static ImmutableArray<WordSpeakerSpan> SpeakerSpans(IEnumerable<WordSpeakerSpan>? spans,
        SourceIdentity source, ImmutableArray<TimelineInterval> segmentIntervals, SpeakerKey? aggregate)
    {
        if (spans is null) { return []; }
        var copy = spans.ToImmutableArray();
        int previousIndex = -1;
        foreach (var span in copy)
        {
            ArgumentNullException.ThrowIfNull(span);
            Speaker(span.Speaker, source);
            if (span.WordIndex <= previousIndex || (aggregate is not null && aggregate != span.Speaker))
            {
                throw new ArgumentException("Speaker spans must be in word order and match any aggregate label.", nameof(spans));
            }
            foreach (var interval in span.Intervals)
            {
                if (!segmentIntervals.Any(segment => interval.TimelineEpoch == segment.TimelineEpoch &&
                    interval.Start.Offset.Value >= segment.Start.Offset.Value && interval.End.Offset.Value <= segment.End.Offset.Value))
                {
                    throw new ArgumentException("Speaker spans must remain within supported segment intervals.", nameof(spans));
                }
            }
            previousIndex = span.WordIndex;
        }
        return copy;
    }

    internal static ImmutableArray<TimelineInterval> Intervals(IEnumerable<TimelineInterval> intervals)
    {
        ArgumentNullException.ThrowIfNull(intervals);
        var copy = intervals.ToImmutableArray();
        if (copy.IsEmpty)
        {
            throw new ArgumentException("At least one supported source interval is required.", nameof(intervals));
        }

        TimelineInterval? previous = null;
        foreach (var interval in copy)
        {
            ArgumentNullException.ThrowIfNull(interval);
            if (previous is not null &&
                (interval.TimelineEpoch != previous.TimelineEpoch || interval.Start.Offset.Value < previous.End.Offset.Value))
            {
                throw new ArgumentException("Intervals must be ordered, nonoverlapping and within one timeline epoch.", nameof(intervals));
            }

            previous = interval;
        }

        return copy;
    }

    internal static ImmutableArray<CitationId> Citations(IEnumerable<CitationId> citationIds)
    {
        ArgumentNullException.ThrowIfNull(citationIds);
        var copy = citationIds.ToImmutableArray();
        foreach (var citationId in copy)
        {
            ArgumentNullException.ThrowIfNull(citationId);
        }

        return copy;
    }
}
