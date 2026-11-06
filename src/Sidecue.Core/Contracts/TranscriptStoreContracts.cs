using System.Collections.Immutable;

namespace Sidecue.Core.Contracts;

/// <summary>One durable transcript change, identified by its source and event ID.</summary>
public sealed record TranscriptWrite
{
    public TranscriptWrite(ContractVersion version, TranscriptEvent @event)
    {
        ContractGuard.Version(version);
        ArgumentNullException.ThrowIfNull(@event);
        if (@event is not (FinalSegment or FinalAmendment or TranscriptGap))
        {
            throw new ArgumentException("Only finalized revisions and gaps belong in the durable transcript path.", nameof(@event));
        }

        Version = version;
        Event = @event;
    }

    public ContractVersion Version { get; }
    public TranscriptEvent Event { get; }

    public override string ToString() => $"TranscriptWrite {{ EventId = {Event.Header.EventId} }}";
}

public enum TranscriptCommitDisposition
{
    Applied,
    NoOp
}

/// <summary>Receipt for a committed transaction, never just a queued write.</summary>
/// <remarks>
/// NoOp identifies an already committed identical change and retains that change's
/// assigned version. Reusing its event identity with different content is a
/// protocol conflict, not a NoOp. The store must compare canonical payloads.
/// </remarks>
public sealed record CommitReceipt
{
    public CommitReceipt(ContractVersion version, SourceIdentity source, EventId eventId,
        TranscriptCommitVersion commitVersion, TranscriptCommitDisposition disposition)
    {
        ContractGuard.Version(version);
        ArgumentNullException.ThrowIfNull(source);
        ContractGuard.Guid(eventId.Value, nameof(eventId));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commitVersion.Value, nameof(commitVersion));
        EventGuard.Defined(disposition, nameof(disposition));
        Version = version;
        Source = source;
        EventId = eventId;
        CommitVersion = commitVersion;
        Disposition = disposition;
    }

    public ContractVersion Version { get; }
    public SourceIdentity Source { get; }
    public EventId EventId { get; }
    public TranscriptCommitVersion CommitVersion { get; }
    public TranscriptCommitDisposition Disposition { get; }
}

/// <summary>An accepted durable change and the version assigned in its transaction.</summary>
public sealed record CommittedTranscriptEntry
{
    public CommittedTranscriptEntry(TranscriptCommitVersion commitVersion, TranscriptWrite write)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commitVersion.Value, nameof(commitVersion));
        ArgumentNullException.ThrowIfNull(write);
        CommitVersion = commitVersion;
        Write = write;
    }

    public TranscriptCommitVersion CommitVersion { get; }
    public TranscriptWrite Write { get; }

    public override string ToString() => $"CommittedTranscriptEntry {{ CommitVersion = {CommitVersion} }}";
}

/// <summary>Immutable durable transcript evidence read at one session commit version.</summary>
/// <remarks>
/// Entries retain the full accepted history of finals, amendments and gaps in commit
/// order, not display order. Zero is the empty baseline; durable changes start at one.
/// The store supplies a consistent read; construction cannot prove durability or
/// completeness of historical content. This is not a partial-text projection.
/// </remarks>
public sealed record SessionSnapshot
{
    public SessionSnapshot(ContractVersion version, SessionId sessionId,
        TranscriptCommitVersion commitVersion, IEnumerable<CommittedTranscriptEntry> entries)
    {
        ContractGuard.Version(version);
        ContractGuard.Guid(sessionId.Value, nameof(sessionId));
        ArgumentNullException.ThrowIfNull(entries);
        var copy = entries.ToImmutableArray();
        var identities = new HashSet<(SourceIdentity Source, EventId EventId)>();
        TranscriptCommitVersion? previousVersion = null;
        foreach (var entry in copy)
        {
            ArgumentNullException.ThrowIfNull(entry);
            var header = entry.Write.Event.Header;
            if (header.Source.SessionId != sessionId)
            {
                throw new ArgumentException("Every transcript entry must belong to the snapshot session.", nameof(entries));
            }

            if (entry.CommitVersion.Value > commitVersion.Value ||
                (previousVersion is { } previous && entry.CommitVersion.Value <= previous.Value))
            {
                throw new ArgumentException("Entry commit versions must increase and cannot exceed the snapshot version.", nameof(entries));
            }

            if (!identities.Add((header.Source, header.EventId)))
            {
                throw new ArgumentException("A committed event identity cannot appear twice in a snapshot.", nameof(entries));
            }

            previousVersion = entry.CommitVersion;
        }

        if ((copy.IsEmpty && commitVersion.Value != 0) ||
            (!copy.IsEmpty && copy[^1].CommitVersion != commitVersion))
        {
            throw new ArgumentException("The snapshot version must be zero for empty history or match its last committed entry.", nameof(commitVersion));
        }

        Version = version;
        SessionId = sessionId;
        CommitVersion = commitVersion;
        Entries = copy;
    }

    public ContractVersion Version { get; }
    public SessionId SessionId { get; }
    public TranscriptCommitVersion CommitVersion { get; }
    public ImmutableArray<CommittedTranscriptEntry> Entries { get; }

    public override string ToString() => $"SessionSnapshot {{ SessionId = {SessionId}, CommitVersion = {CommitVersion}, EntryCount = {Entries.Length} }}";
}
