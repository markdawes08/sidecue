namespace Sidecue.Core.Contracts;

/// <summary>A known or uncertain missing source interval, without invented audio.</summary>
public sealed record CaptureGap
{
    public CaptureGap(
        ContractVersion version,
        SourceIdentity source,
        TimelineInterval interval,
        GapReason reason,
        TimingCertainty certainty)
    {
        ContractGuard.Version(version);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(interval);
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(nameof(reason));
        }

        if (!Enum.IsDefined(certainty))
        {
            throw new ArgumentOutOfRangeException(nameof(certainty));
        }

        Version = version;
        Source = source;
        Interval = interval;
        Reason = reason;
        Certainty = certainty;
    }

    public ContractVersion Version { get; }
    public SourceIdentity Source { get; }
    public TimelineInterval Interval { get; }
    public GapReason Reason { get; }
    public TimingCertainty Certainty { get; }
}
