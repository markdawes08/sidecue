namespace Sidecue.Core.Contracts;

// The all-zero struct value is intentionally invalid. Enclosing contracts also
// validate IDs so default(T) cannot bypass a wrapper's constructor.
public readonly record struct SessionId
{
    public SessionId(Guid value) { ContractGuard.Guid(value, nameof(value)); Value = value; }
    public Guid Value { get; }
}

public readonly record struct TimelineEpochId
{
    public TimelineEpochId(Guid value) { ContractGuard.Guid(value, nameof(value)); Value = value; }
    public Guid Value { get; }
}

public readonly record struct SourceId
{
    public SourceId(Guid value) { ContractGuard.Guid(value, nameof(value)); Value = value; }
    public Guid Value { get; }
}

public readonly record struct SegmentId
{
    public SegmentId(Guid value) { ContractGuard.Guid(value, nameof(value)); Value = value; }
    public Guid Value { get; }
}

public readonly record struct EventId
{
    public EventId(Guid value) { ContractGuard.Guid(value, nameof(value)); Value = value; }
    public Guid Value { get; }
}

public readonly record struct RequestId
{
    public RequestId(Guid value) { ContractGuard.Guid(value, nameof(value)); Value = value; }
    public Guid Value { get; }
}

public readonly record struct ProfileVersionId
{
    public ProfileVersionId(Guid value) { ContractGuard.Guid(value, nameof(value)); Value = value; }
    public Guid Value { get; }
}

public sealed record SourceIdentity
{
    public SourceIdentity(SessionId sessionId, SourceId sourceId, StreamEpoch streamEpoch)
    {
        ContractGuard.Guid(sessionId.Value, nameof(sessionId));
        ContractGuard.Guid(sourceId.Value, nameof(sourceId));
        SessionId = sessionId;
        SourceId = sourceId;
        StreamEpoch = streamEpoch;
    }

    public SessionId SessionId { get; }
    public SourceId SourceId { get; }
    public StreamEpoch StreamEpoch { get; }
}
