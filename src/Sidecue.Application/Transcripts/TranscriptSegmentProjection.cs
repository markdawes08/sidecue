using Sidecue.Core.Contracts;

namespace Sidecue.Application.Transcripts;

/// <summary>Keeps the provisional display state for one scoped transcript segment.</summary>
/// <remarks>
/// The caller serializes updates and restores the committed revision from durable evidence
/// when recreating a projection. The projection retains at most one provisional event.
/// </remarks>
public sealed class TranscriptSegmentProjection
{
    private readonly SourceIdentity source;
    private readonly SegmentId segmentId;

    public TranscriptSegmentProjection(SourceIdentity source, SegmentId segmentId)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (segmentId.Value == Guid.Empty)
        {
            throw new ArgumentException("A segment identity is required.", nameof(segmentId));
        }

        this.source = source;
        this.segmentId = segmentId;
    }

    public PartialSegment? Partial { get; private set; }

    public SegmentRevision? CommittedRevision { get; private set; }

    public bool TryApplyPartial(PartialSegment partial)
    {
        ArgumentNullException.ThrowIfNull(partial);
        RequireSameSegment(partial.Header.Source, partial.SegmentId, nameof(partial));
        if (CommittedRevision is not null ||
            (Partial is { } current && partial.Revision.Value <= current.Revision.Value))
        {
            return false;
        }

        Partial = partial;
        return true;
    }

    public void ObserveCommitted(CommittedTranscriptEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        TranscriptEvent committed = entry.Write.Event;
        var (id, revision) = committed switch
        {
            FinalSegment final => (final.SegmentId, final.Revision),
            FinalAmendment amendment => (amendment.SegmentId, amendment.Revision),
            _ => throw new ArgumentException("Expected a committed final revision.", nameof(entry)),
        };
        RequireSameSegment(committed.Header.Source, id, nameof(entry));

        if (CommittedRevision is not { } current || revision.Value > current.Value)
        {
            CommittedRevision = revision;
        }
        Partial = null;
    }

    private void RequireSameSegment(SourceIdentity eventSource, SegmentId eventSegment, string parameterName)
    {
        if (eventSource != source || eventSegment != segmentId)
        {
            throw new ArgumentException("The event belongs to a different transcript segment.", parameterName);
        }
    }
}
