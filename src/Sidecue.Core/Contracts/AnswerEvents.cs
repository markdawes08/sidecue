using System.Collections.Immutable;

namespace Sidecue.Core.Contracts;

public sealed record AnswerFence
{
    public AnswerFence(SessionId sessionId, ContextEpoch contextEpoch, RequestId requestId)
    {
        ContractGuard.Guid(sessionId.Value, nameof(sessionId));
        ContractGuard.Guid(requestId.Value, nameof(requestId));
        SessionId = sessionId;
        ContextEpoch = contextEpoch;
        RequestId = requestId;
    }

    public SessionId SessionId { get; }
    public ContextEpoch ContextEpoch { get; }
    public RequestId RequestId { get; }
}

public sealed record AnswerEventHeader
{
    public AnswerEventHeader(ContractVersion version, AnswerFence fence, AnswerSequence sequence)
    {
        ContractGuard.Version(version, nameof(version));
        ArgumentNullException.ThrowIfNull(fence);
        Version = version;
        Fence = fence;
        Sequence = sequence;
    }

    public ContractVersion Version { get; }
    public AnswerFence Fence { get; }
    public AnswerSequence Sequence { get; }
}

public abstract record AnswerEvent
{
    private protected AnswerEvent(AnswerEventHeader header)
    {
        ArgumentNullException.ThrowIfNull(header);
        Header = header;
    }

    public AnswerEventHeader Header { get; }

    // An event's diagnostic representation never contains model text or provider payloads.
    public sealed override string ToString() => $"{GetType().Name} {{ RequestId = {Header.Fence.RequestId}, Sequence = {Header.Sequence} }}";
}

public sealed record AnswerStarted : AnswerEvent
{
    public AnswerStarted(AnswerEventHeader header) : base(header) { }
}

public sealed record AnswerTextDelta : AnswerEvent
{
    public AnswerTextDelta(AnswerEventHeader header, string text) : base(header)
    {
        ArgumentNullException.ThrowIfNull(text);
        Text = text;
    }

    public string Text { get; }
}

/// <summary>A key into the request's frozen evidence table. Construction does not verify grounding.</summary>
public sealed record CitationId
{
    public CitationId(string value)
    {
        ContractGuard.Text(value, nameof(value));
        Value = value;
    }

    public string Value { get; }

    public override string ToString() => nameof(CitationId);
}

public sealed record AnswerEvidenceUpdated : AnswerEvent
{
    public AnswerEvidenceUpdated(AnswerEventHeader header, IEnumerable<CitationId> citationIds) : base(header)
    {
        CitationIds = EventGuard.Citations(citationIds);
    }

    public ImmutableArray<CitationId> CitationIds { get; }
}

public sealed record TokenUsage
{
    public TokenUsage(long inputTokens, long outputTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputTokens);
        ArgumentOutOfRangeException.ThrowIfNegative(outputTokens);
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
    }

    public long InputTokens { get; }
    public long OutputTokens { get; }
}

public enum AnswerFinishReason
{
    Stop,
    OutputLimit,
    ContentFiltered,
    Unknown
}

public sealed record AnswerCompleted : AnswerEvent
{
    public AnswerCompleted(AnswerEventHeader header, string text, IEnumerable<CitationId> verifiedCitationIds,
        string providerId, string modelId, TokenUsage? usage = null,
        AnswerFinishReason finishReason = AnswerFinishReason.Stop) : base(header)
    {
        ArgumentNullException.ThrowIfNull(text);
        ContractGuard.Text(providerId, nameof(providerId));
        ContractGuard.Text(modelId, nameof(modelId));
        EventGuard.Defined(finishReason, nameof(finishReason));
        VerifiedCitationIds = EventGuard.Citations(verifiedCitationIds);
        Text = text;
        ProviderId = providerId;
        ModelId = modelId;
        Usage = usage;
        FinishReason = finishReason;
    }

    public string Text { get; }
    /// <summary>The coordinator must resolve and verify these keys before creating a completed event.</summary>
    public ImmutableArray<CitationId> VerifiedCitationIds { get; }
    public string ProviderId { get; }
    public string ModelId { get; }
    public TokenUsage? Usage { get; }
    public AnswerFinishReason FinishReason { get; }
}

public sealed record AnswerCancelled : AnswerEvent
{
    public AnswerCancelled(AnswerEventHeader header) : base(header) { }
}

public sealed record AnswerFailed : AnswerEvent
{
    public AnswerFailed(AnswerEventHeader header, EventErrorCategory error) : base(header)
    {
        EventGuard.Defined(error, nameof(error));
        Error = error;
    }

    public EventErrorCategory Error { get; }
}
