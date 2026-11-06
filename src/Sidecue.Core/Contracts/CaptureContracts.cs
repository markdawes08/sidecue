namespace Sidecue.Core.Contracts;

/// <summary>A fixed relation between a session timeline and calendar time.</summary>
public sealed record SessionClockAnchor
{
    public SessionClockAnchor(SessionId sessionId, TimelinePosition origin, UtcTimestamp utcAtOrigin)
    {
        ContractGuard.Guid(sessionId.Value, nameof(sessionId));
        ArgumentNullException.ThrowIfNull(origin);
        SessionId = sessionId;
        Origin = origin;
        UtcAtOrigin = utcAtOrigin;
    }

    public SessionId SessionId { get; }
    public TimelinePosition Origin { get; }
    public UtcTimestamp UtcAtOrigin { get; }
}

public enum CaptureSourceKind
{
    Microphone,
    RemotePlayback
}

/// <summary>An opaque selection resolved inside the capture adapter, never a native handle.</summary>
/// <remarks>
/// The Windows adapter issues and resolves the selector to a device or selected process target.
/// Callers do not encode operating-system handles, process IDs or credentials in this value.
/// </remarks>
public sealed record CaptureSourceSelection
{
    public CaptureSourceSelection(CaptureSourceKind kind, string selectorId)
    {
        EventGuard.Defined(kind, nameof(kind));
        ContractGuard.Text(selectorId, nameof(selectorId));
        Kind = kind;
        SelectorId = selectorId;
    }

    public CaptureSourceKind Kind { get; }
    public string SelectorId { get; }

    public override string ToString() => $"CaptureSourceSelection {{ Kind = {Kind} }}";
}

/// <summary>Both limits apply; exceeding either requires explicit disposal and gap accounting.</summary>
public sealed record AudioBufferPolicy
{
    public AudioBufferPolicy(long maximumDurationMicroseconds, int maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumDurationMicroseconds);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        MaximumDurationMicroseconds = maximumDurationMicroseconds;
        MaximumBytes = maximumBytes;
    }

    public long MaximumDurationMicroseconds { get; }
    public int MaximumBytes { get; }
}

public sealed record CaptureRequest
{
    public CaptureRequest(ContractVersion version, SourceIdentity source, SessionClockAnchor clockAnchor,
        CaptureSourceSelection selection, AudioBufferPolicy bufferPolicy)
    {
        ContractGuard.Version(version);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(clockAnchor);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(bufferPolicy);
        if (source.SessionId != clockAnchor.SessionId)
        {
            throw new ArgumentException("The clock anchor must belong to the requested session.", nameof(clockAnchor));
        }

        Version = version;
        Source = source;
        ClockAnchor = clockAnchor;
        Selection = selection;
        BufferPolicy = bufferPolicy;
    }

    public ContractVersion Version { get; }
    public SourceIdentity Source { get; }
    public SessionClockAnchor ClockAnchor { get; }
    public CaptureSourceSelection Selection { get; }
    public AudioBufferPolicy BufferPolicy { get; }
}

public enum CaptureState
{
    Connecting,
    Live,
    Degraded,
    Paused,
    Stopped,
    Failed
}

public enum CaptureStopReason
{
    Pause,
    End,
    Recovery
}

/// <summary>Content-free source health and optional explicit missing interval.</summary>
public sealed record CaptureStatusEvent
{
    public CaptureStatusEvent(ContractVersion version, SourceIdentity source, TimelinePosition position,
        CaptureState state, EventErrorCategory? error = null, CaptureGap? gap = null)
    {
        ContractGuard.Version(version);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(position);
        EventGuard.Defined(state, nameof(state));
        if (error is { } category)
        {
            EventGuard.Defined(category, nameof(error));
        }
        if (state == CaptureState.Failed && error is null)
        {
            throw new ArgumentException("A failed capture status requires an error category.", nameof(error));
        }
        if (gap is not null && (gap.Source != source || gap.Interval.TimelineEpoch != position.TimelineEpoch ||
            gap.Interval.End.Offset.Value > position.Offset.Value))
        {
            throw new ArgumentException("A gap must belong to this source and timeline and end by the status position.", nameof(gap));
        }

        Version = version;
        Source = source;
        Position = position;
        State = state;
        Error = error;
        Gap = gap;
    }

    public ContractVersion Version { get; }
    public SourceIdentity Source { get; }
    public TimelinePosition Position { get; }
    public CaptureState State { get; }
    public EventErrorCategory? Error { get; }
    public CaptureGap? Gap { get; }
}

public sealed record TranscriptionRequest
{
    public TranscriptionRequest(ContractVersion version, SourceIdentity source, string language, AudioFormat format,
        ProviderConfigurationReference providerConfiguration, TimelinePosition streamStart)
    {
        ContractGuard.Version(version);
        ArgumentNullException.ThrowIfNull(source);
        ContractGuard.Text(language, nameof(language));
        ArgumentNullException.ThrowIfNull(format);
        ArgumentNullException.ThrowIfNull(providerConfiguration);
        ArgumentNullException.ThrowIfNull(streamStart);
        Version = version;
        Source = source;
        Language = language;
        Format = format;
        ProviderConfiguration = providerConfiguration;
        StreamStart = streamStart;
    }

    public ContractVersion Version { get; }
    public SourceIdentity Source { get; }
    public string Language { get; }
    public AudioFormat Format { get; }
    public ProviderConfigurationReference ProviderConfiguration { get; }
    public TimelinePosition StreamStart { get; }

    public override string ToString() => $"TranscriptionRequest {{ Source = {Source}, Version = {Version} }}";
}
