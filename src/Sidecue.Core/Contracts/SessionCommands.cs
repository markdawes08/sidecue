namespace Sidecue.Core.Contracts;

/// <summary>Immutable intent only. The session controller owns all effects and fences.</summary>
public abstract record SessionCommand
{
    private protected SessionCommand(ContractVersion version, SessionId sessionId)
    {
        ContractGuard.Version(version);
        ContractGuard.Guid(sessionId.Value, nameof(sessionId));
        Version = version;
        SessionId = sessionId;
    }

    public ContractVersion Version { get; }
    public SessionId SessionId { get; }
    public sealed override string ToString() => $"{GetType().Name} {{ SessionId = {SessionId} }}";
}

/// <summary>Stop acquisition/new uploads and discard unsent audio. Existing assistance continues.</summary>
public sealed record PauseListening : SessionCommand
{
    public PauseListening(ContractVersion version, SessionId sessionId) : base(version, sessionId) { }
}

/// <summary>Change visibility only; capture and assistance continue.</summary>
public sealed record HideOverlay : SessionCommand
{
    public HideOverlay(ContractVersion version, SessionId sessionId) : base(version, sessionId) { }
}

/// <summary>Advance context epoch/boundary and cancel generation, preserving durable history.</summary>
public sealed record ClearAssistantContext : SessionCommand
{
    public ClearAssistantContext(ContractVersion version, SessionId sessionId) : base(version, sessionId) { }
}

/// <summary>Fence requests, cancel generation, stop capture, drain/finalize and enqueue summary once.</summary>
public sealed record EndSession : SessionCommand
{
    public EndSession(ContractVersion version, SessionId sessionId) : base(version, sessionId) { }
}
