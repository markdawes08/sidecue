using Sidecue.Core.Contracts;

namespace Sidecue.Replay.Tests.Reference;

// Executable reference rules for fixtures, deliberately separate from production
// lifecycle, persistence reconciliation and live UI ordering implementations.
internal sealed class TranscriptReferenceModel(FakeSessionStore store)
{
    private readonly Dictionary<(SourceIdentity Source, EventId EventId), (string Fingerprint, string Outcome)> _seen = [];
    private readonly Dictionary<(SourceIdentity Source, SegmentId Segment), PartialSegment> _partials = [];
    private readonly Dictionary<(SourceIdentity Source, SegmentId Segment), TranscriptEvent> _finals = [];
    private readonly Dictionary<(SourceIdentity Source, SegmentId Segment, SegmentRevision Revision), string> _revisions = [];

    public IReadOnlyList<TranscriptEvent> CurrentFinals => Sort(_finals.Values).ToArray();
    public IReadOnlyList<PartialSegment> Partials => Sort(_partials.Values).Cast<PartialSegment>().ToArray();

    public async Task<string> ApplyAsync(TranscriptEvent value, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(value);
        cancellationToken.ThrowIfCancellationRequested();
        var identity = (value.Header.Source, value.Header.EventId);
        var fingerprint = TranscriptFingerprint.Compute(value);
        if (_seen.TryGetValue(identity, out var seen))
        {
            return !StringComparer.Ordinal.Equals(seen.Fingerprint, fingerprint) || seen.Outcome == "conflict"
                ? "conflict" : "duplicate";
        }
        var outcome = await ApplyNewAsync(value, cancellationToken);
        _seen.Add(identity, (fingerprint, outcome));
        return outcome;
    }

    private async Task<string> ApplyNewAsync(TranscriptEvent value, CancellationToken cancellationToken)
    {
        if (value is PartialSegment partial)
        {
            var key = (partial.Header.Source, partial.SegmentId);
            if (_finals.ContainsKey(key)) { return "late-partial"; }
            if (_partials.TryGetValue(key, out var previous) && partial.Revision.Value <= previous.Revision.Value)
            {
                return "stale-partial";
            }
            _partials[key] = partial;
            return "partial";
        }
        if (value is FinalSegment or FinalAmendment)
        {
            var (segment, revision) = SegmentRevisionOf(value);
            var key = (value.Header.Source, segment);
            var revisionKey = (value.Header.Source, segment, revision);
            var payload = TranscriptFingerprint.Payload(value);
            if (_revisions.TryGetValue(revisionKey, out var priorPayload))
            {
                return StringComparer.Ordinal.Equals(payload, priorPayload) ? "duplicate" : "conflict";
            }
            _finals.TryGetValue(key, out var current);
            if (value is FinalAmendment amendment)
            {
                if (current is null || SegmentRevisionOf(current).Revision != amendment.AmendsRevision)
                {
                    return "conflict";
                }
            }
            else if (current is not null) { return "conflict"; }

            await store.CommitTranscriptAsync(new TranscriptWrite(ContractVersion.Current, value), cancellationToken);
            _revisions.Add(revisionKey, payload);
            _finals[key] = value;
            _partials.Remove(key);
            return value is FinalAmendment ? "amended" : "committed";
        }
        if (value is TranscriptGap)
        {
            await store.CommitTranscriptAsync(new TranscriptWrite(ContractVersion.Current, value), cancellationToken);
            return "gap";
        }
        return value switch
        {
            UtteranceBoundary => "boundary",
            StreamStatus => "status",
            _ => throw new ArgumentException("Unsupported replay transcript event.", nameof(value))
        };
    }

    private static (SegmentId Segment, SegmentRevision Revision) SegmentRevisionOf(TranscriptEvent value) => value switch
    {
        PartialSegment partial => (partial.SegmentId, partial.Revision),
        FinalSegment final => (final.SegmentId, final.Revision),
        FinalAmendment amendment => (amendment.SegmentId, amendment.Revision),
        _ => throw new ArgumentException("The event has no segment revision.", nameof(value))
    };

    private static TimelineInterval FirstInterval(TranscriptEvent value) => value switch
    {
        PartialSegment partial => partial.Interval,
        FinalSegment final => final.Intervals[0],
        FinalAmendment amendment => amendment.Intervals[0],
        _ => throw new ArgumentException("The event has no segment interval.", nameof(value))
    };

    private static IOrderedEnumerable<TranscriptEvent> Sort(IEnumerable<TranscriptEvent> values) => values
        // Group clock lifetimes before considering offsets; never infer elapsed
        // order across sessions or independent timeline origins.
        .OrderBy(value => value.Header.Source.SessionId.Value)
        .ThenBy(value => FirstInterval(value).TimelineEpoch.Value)
        .ThenBy(value => FirstInterval(value).Start.Offset.Value)
        .ThenBy(value => value.Header.Source.SourceId.Value)
        .ThenBy(value => SegmentRevisionOf(value).Segment.Value)
        .ThenBy(value => value.Header.Source.StreamEpoch.Value);
}
