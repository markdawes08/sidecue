using Sidecue.Core.Contracts;

namespace Sidecue.Application.Abstractions;

/// <summary>The durable transcript subset needed for the first deterministic replay.</summary>
/// <remarks>
/// Implementations own and serialize their storage work. Callers never receive a
/// SQL connection or an uncommitted transaction. End/summary-job and assistance
/// artifact operations are added with their later persistence tasks.
/// </remarks>
public interface ISessionStore
{
    /// <summary>Commit a final revision or gap and return its durable outcome.</summary>
    /// <remarks>
    /// The source and event ID are the idempotency key. An identical retry returns
    /// NoOp with the original assigned commit version; conflicting content must be
    /// reported as a protocol conflict. Cancellation can race with commit. A caller
    /// receiving an ambiguous failure must reread state or retry the same identity,
    /// rather than assuming cancellation rolled back the transaction.
    /// </remarks>
    Task<CommitReceipt> CommitTranscriptAsync(TranscriptWrite write, CancellationToken ct);

    /// <summary>Read consistent committed evidence for one session.</summary>
    /// <remarks>
    /// Cancellation ends the read, not the session. The returned snapshot owns no
    /// database resources and remains immutable after the operation completes.
    /// </remarks>
    Task<SessionSnapshot> ReadSessionAsync(SessionId id, CancellationToken ct);
}
