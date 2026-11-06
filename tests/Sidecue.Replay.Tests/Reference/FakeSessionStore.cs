using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;

namespace Sidecue.Replay.Tests.Reference;

// Sequential in-memory contract fake. No database/durability/concurrency claim.
internal sealed class FakeSessionStore : ISessionStore
{
    private readonly Dictionary<SessionId, List<CommittedTranscriptEntry>> _sessions = [];
    private readonly Dictionary<(SourceIdentity Source, EventId EventId), (string Fingerprint, TranscriptCommitVersion Version)> _commits = [];

    public Task<CommitReceipt> CommitTranscriptAsync(TranscriptWrite write, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(write);
        ct.ThrowIfCancellationRequested();
        var header = write.Event.Header;
        var key = (header.Source, header.EventId);
        var fingerprint = TranscriptFingerprint.Compute(write.Event);
        if (_commits.TryGetValue(key, out var existing))
        {
            if (!StringComparer.Ordinal.Equals(existing.Fingerprint, fingerprint))
            {
                throw new InvalidOperationException("Conflicting replay transcript event identity.");
            }
            return Task.FromResult(new CommitReceipt(write.Version, header.Source, header.EventId,
                existing.Version, TranscriptCommitDisposition.NoOp));
        }
        if (!_sessions.TryGetValue(header.Source.SessionId, out var entries))
        {
            entries = [];
            _sessions.Add(header.Source.SessionId, entries);
        }
        var version = new TranscriptCommitVersion(checked((long)entries.Count + 1));
        entries.Add(new CommittedTranscriptEntry(version, write));
        _commits.Add(key, (fingerprint, version));
        return Task.FromResult(new CommitReceipt(write.Version, header.Source, header.EventId,
            version, TranscriptCommitDisposition.Applied));
    }

    public Task<SessionSnapshot> ReadSessionAsync(SessionId id, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _sessions.TryGetValue(id, out var entries);
        return Task.FromResult(new SessionSnapshot(ContractVersion.Current, id,
            new TranscriptCommitVersion(entries?.Count ?? 0), entries ?? []));
    }
}
