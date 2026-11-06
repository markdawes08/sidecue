using System.Collections.Immutable;

namespace Sidecue.Core.Contracts;

public sealed record TranscriptEventHeader
{
    public TranscriptEventHeader(ContractVersion version, SourceIdentity source, EventId eventId, EventSequence sequence)
    {
        ContractGuard.Version(version, nameof(version));
        ArgumentNullException.ThrowIfNull(source);
        ContractGuard.Guid(eventId.Value, nameof(eventId));
        Version = version;
        Source = source;
        EventId = eventId;
        Sequence = sequence;
    }

    public ContractVersion Version { get; }
    public SourceIdentity Source { get; }
    public EventId EventId { get; }
    public EventSequence Sequence { get; }
}

public abstract record TranscriptEvent
{
    private protected TranscriptEvent(TranscriptEventHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        Header = header;
    }

    public TranscriptEventHeader Header { get; }

    // Record-generated formatting must never put transcript content in diagnostics.
    public sealed override string ToString() => $"{GetType().Name} {{ EventId = {Header.EventId}, Sequence = {Header.Sequence} }}";
}

public sealed record PartialSegment : TranscriptEvent
{
    public PartialSegment(TranscriptEventHeader header, SegmentId segmentId, SegmentRevision revision,
        TimelineInterval interval, string text, SpeakerKey? speaker = null) : base(header)
    {
        ContractGuard.Guid(segmentId.Value, nameof(segmentId));
        ArgumentNullException.ThrowIfNull(interval);
        ArgumentNullException.ThrowIfNull(text);
        EventGuard.Speaker(speaker, header.Source);
        SegmentId = segmentId;
        Revision = revision;
        Interval = interval;
        Text = text;
        Speaker = speaker;
    }

    public SegmentId SegmentId { get; }
    public SegmentRevision Revision { get; }
    public TimelineInterval Interval { get; }
    public string Text { get; }
    public SpeakerKey? Speaker { get; }
}

public sealed record FinalSegment : TranscriptEvent
{
    public FinalSegment(TranscriptEventHeader header, SegmentId segmentId, SegmentRevision revision,
        IEnumerable<TimelineInterval> intervals, TimingCertainty certainty, string text, SpeakerKey? speaker = null) : base(header)
    {
        ContractGuard.Guid(segmentId.Value, nameof(segmentId));
        ArgumentNullException.ThrowIfNull(text);
        EventGuard.Defined(certainty, nameof(certainty));
        EventGuard.Speaker(speaker, header.Source);
        Intervals = EventGuard.Intervals(intervals);
        SegmentId = segmentId;
        Revision = revision;
        Certainty = certainty;
        Text = text;
        Speaker = speaker;
    }

    public SegmentId SegmentId { get; }
    public SegmentRevision Revision { get; }
    public ImmutableArray<TimelineInterval> Intervals { get; }
    public TimingCertainty Certainty { get; }
    public string Text { get; }
    public SpeakerKey? Speaker { get; }
}

public sealed record FinalAmendment : TranscriptEvent
{
    public FinalAmendment(TranscriptEventHeader header, SegmentId segmentId, SegmentRevision revision,
        SegmentRevision amendsRevision, IEnumerable<TimelineInterval> intervals, TimingCertainty certainty,
        string text, SpeakerKey? speaker = null) : base(header)
    {
        ContractGuard.Guid(segmentId.Value, nameof(segmentId));
        ArgumentNullException.ThrowIfNull(text);
        if (revision.Value <= amendsRevision.Value)
        {
            throw new ArgumentException("An amendment must have a higher revision than the finalized revision it replaces.", nameof(revision));
        }

        EventGuard.Defined(certainty, nameof(certainty));
        EventGuard.Speaker(speaker, header.Source);
        Intervals = EventGuard.Intervals(intervals);
        SegmentId = segmentId;
        Revision = revision;
        AmendsRevision = amendsRevision;
        Certainty = certainty;
        Text = text;
        Speaker = speaker;
    }

    public SegmentId SegmentId { get; }
    public SegmentRevision Revision { get; }
    public SegmentRevision AmendsRevision { get; }
    public ImmutableArray<TimelineInterval> Intervals { get; }
    public TimingCertainty Certainty { get; }
    public string Text { get; }
    public SpeakerKey? Speaker { get; }
}

public sealed record UtteranceBoundary : TranscriptEvent
{
    public UtteranceBoundary(TranscriptEventHeader header, TimelinePosition position) : base(header)
    {
        ArgumentNullException.ThrowIfNull(position);
        Position = position;
    }

    public TimelinePosition Position { get; }
}

public sealed record TranscriptGap : TranscriptEvent
{
    public TranscriptGap(TranscriptEventHeader header, TimelineInterval interval, GapReason reason,
        TimingCertainty certainty) : base(header)
    {
        ArgumentNullException.ThrowIfNull(interval);
        EventGuard.Defined(reason, nameof(reason));
        EventGuard.Defined(certainty, nameof(certainty));
        Interval = interval;
        Reason = reason;
        Certainty = certainty;
    }

    public TimelineInterval Interval { get; }
    public GapReason Reason { get; }
    public TimingCertainty Certainty { get; }
}

public enum TranscriptionStreamState
{
    Connecting,
    Live,
    Degraded,
    Draining,
    Stopped,
    Failed
}

public sealed record StreamStatus : TranscriptEvent
{
    public StreamStatus(TranscriptEventHeader header, TranscriptionStreamState state,
        EventErrorCategory? error = null) : base(header)
    {
        EventGuard.Defined(state, nameof(state));
        if (error is { } category)
        {
            EventGuard.Defined(category, nameof(error));
        }

        if (state == TranscriptionStreamState.Failed && error is null)
        {
            throw new ArgumentException("A failed stream requires a safe error category.", nameof(error));
        }

        State = state;
        Error = error;
    }

    public TranscriptionStreamState State { get; }
    public EventErrorCategory? Error { get; }
}
