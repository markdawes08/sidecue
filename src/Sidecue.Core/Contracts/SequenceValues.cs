namespace Sidecue.Core.Contracts;

// Counters start at zero. Their owning stream/session enforces advancement;
// these values reject negative input without conflating unrelated counters.
public readonly record struct StreamEpoch
{
    public StreamEpoch(long value) { ArgumentOutOfRangeException.ThrowIfNegative(value); Value = value; }
    public long Value { get; }
}

public readonly record struct FrameSequence
{
    public FrameSequence(long value) { ArgumentOutOfRangeException.ThrowIfNegative(value); Value = value; }
    public long Value { get; }
}

public readonly record struct SegmentRevision
{
    public SegmentRevision(long value) { ArgumentOutOfRangeException.ThrowIfNegative(value); Value = value; }
    public long Value { get; }
}

public readonly record struct TranscriptCommitVersion
{
    public TranscriptCommitVersion(long value) { ArgumentOutOfRangeException.ThrowIfNegative(value); Value = value; }
    public long Value { get; }
}

public readonly record struct ContextEpoch
{
    public ContextEpoch(long value) { ArgumentOutOfRangeException.ThrowIfNegative(value); Value = value; }
    public long Value { get; }
}

public readonly record struct EventSequence
{
    public EventSequence(long value) { ArgumentOutOfRangeException.ThrowIfNegative(value); Value = value; }
    public long Value { get; }
}

public readonly record struct AnswerSequence
{
    public AnswerSequence(long value) { ArgumentOutOfRangeException.ThrowIfNegative(value); Value = value; }
    public long Value { get; }
}

// Schema version 1 implements the design document's baseline 0.1; these are
// deliberately distinct version schemes. Unknown versions fail at envelopes.
public readonly record struct ContractVersion
{
    public static ContractVersion Current { get; } = new(1);

    public ContractVersion(int value) { ArgumentOutOfRangeException.ThrowIfNegativeOrZero(value); Value = value; }
    public int Value { get; }
}
