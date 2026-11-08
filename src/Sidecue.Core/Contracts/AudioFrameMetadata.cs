namespace Sidecue.Core.Contracts;

[Flags]
public enum AudioFrameAttributes
{
    None = 0,
    Silence = 1,
    Discontinuity = 2,
    TimestampError = 4,
    /// <summary>The session interval is an estimate, independently of the native timestamp flag.</summary>
    SessionTimestampEstimated = 8
}

/// <summary>Immutable audio identity and timing; sample count is per channel.</summary>
public sealed record AudioFrameMetadata
{
    public AudioFrameMetadata(
        ContractVersion version,
        SourceIdentity source,
        FrameSequence sequence,
        TimelineInterval interval,
        AudioFormat format,
        int sampleCount,
        AudioFrameAttributes flags = AudioFrameAttributes.None,
        RawClockPosition? devicePosition = null,
        RawClockPosition? performanceCounter = null)
    {
        ContractGuard.Version(version);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(interval);
        ArgumentNullException.ThrowIfNull(format);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleCount);
        const AudioFrameAttributes knownFlags = AudioFrameAttributes.Silence | AudioFrameAttributes.Discontinuity |
            AudioFrameAttributes.TimestampError | AudioFrameAttributes.SessionTimestampEstimated;
        if ((flags & ~knownFlags) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(flags));
        }

        // Independently rounded endpoints can differ by either the floor or the
        // ceiling of the exact sample duration. Floating-point tolerances could
        // accept inconsistent frames, particularly on long-running timelines.
        Int128 numerator = (Int128)sampleCount * 1_000_000;
        long minimumDuration = checked((long)(numerator / format.SampleRate));
        long maximumDuration = checked((long)((numerator + format.SampleRate - 1) / format.SampleRate));
        if (interval.DurationMicroseconds < minimumDuration || interval.DurationMicroseconds > maximumDuration)
        {
            throw new ArgumentException("Interval duration must agree with the sample count within integer microsecond rounding.", nameof(interval));
        }

        PayloadByteCount = checked(sampleCount * format.BytesPerFrame);
        Version = version;
        Source = source;
        Sequence = sequence;
        Interval = interval;
        Format = format;
        SampleCount = sampleCount;
        Flags = flags;
        DevicePosition = devicePosition;
        PerformanceCounter = performanceCounter;
    }

    public ContractVersion Version { get; }
    public SourceIdentity Source { get; }
    public FrameSequence Sequence { get; }
    public TimelineInterval Interval { get; }
    public AudioFormat Format { get; }
    public int SampleCount { get; }
    public AudioFrameAttributes Flags { get; }
    public RawClockPosition? DevicePosition { get; }
    public RawClockPosition? PerformanceCounter { get; }
    public int PayloadByteCount { get; }
}
