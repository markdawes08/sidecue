using Sidecue.Core.Contracts;
using Xunit;

namespace Sidecue.Application.Tests.Contracts;

public sealed class CaptureAndStoreContractTests
{
    [Fact]
    public void CaptureCannotUseAnotherSessionsClockAnchor()
    {
        var anchor = new SessionClockAnchor(ContractSamples.OtherSession, ContractSamples.Position(), Utc());

        Assert.ThrowsAny<ArgumentException>(() => new CaptureRequest(ContractVersion.Current,
            ContractSamples.Identity(), anchor, Selection(), new AudioBufferPolicy(2_000_000, 64_000)));
    }

    [Fact]
    public void BufferPolicyRequiresBothFinitePositiveLimits()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBufferPolicy(0, 64_000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new AudioBufferPolicy(2_000_000, 0));
    }

    [Fact]
    public void CaptureStatusCannotAttributeAnotherSourcesGap()
    {
        var gap = new CaptureGap(ContractVersion.Current, ContractSamples.Identity(ContractSamples.OtherSession),
            ContractSamples.Interval(), GapReason.QueueOverflow, TimingCertainty.Exact);

        Assert.ThrowsAny<ArgumentException>(() => new CaptureStatusEvent(ContractVersion.Current,
            ContractSamples.Identity(), ContractSamples.Position(100_000), CaptureState.Degraded, gap: gap));
    }

    [Fact]
    public void TranscriptWritesRejectProvisionalAndStatusOnlyEvents()
    {
        TranscriptEvent[] transientEvents =
        [
            new PartialSegment(ContractSamples.Header(), ContractSamples.Segment, new SegmentRevision(0),
                ContractSamples.Interval(), "Synthetic provisional text"),
            new UtteranceBoundary(ContractSamples.Header(), ContractSamples.Position()),
            new StreamStatus(ContractSamples.Header(), TranscriptionStreamState.Live)
        ];

        Assert.All(transientEvents, item => Assert.ThrowsAny<ArgumentException>(() =>
            new TranscriptWrite(ContractVersion.Current, item)));
    }

    [Fact]
    public void DurableSnapshotCopiesEntriesAndRetainsCommittedIdentity()
    {
        var write = new TranscriptWrite(ContractVersion.Current, ContractSamples.Final());
        var entry = new CommittedTranscriptEntry(new TranscriptCommitVersion(1), write);
        var entries = new List<CommittedTranscriptEntry> { entry };
        var snapshot = new SessionSnapshot(ContractVersion.Current, ContractSamples.Session,
            new TranscriptCommitVersion(1), entries);

        entries.Clear();

        Assert.Equal(entry, Assert.Single(snapshot.Entries));
        Assert.Equal(ContractSamples.Event, snapshot.Entries[0].Write.Event.Header.EventId);
        Assert.Equal(ContractVersion.Current, snapshot.Version);
    }

    [Fact]
    public void DurableSnapshotRejectsForeignOrInconsistentCommitHistory()
    {
        var write = new TranscriptWrite(ContractVersion.Current, ContractSamples.Final());
        var entry = new CommittedTranscriptEntry(new TranscriptCommitVersion(1), write);

        Assert.ThrowsAny<ArgumentException>(() => new SessionSnapshot(ContractVersion.Current,
            ContractSamples.OtherSession, new TranscriptCommitVersion(1), [entry]));
        Assert.ThrowsAny<ArgumentException>(() => new SessionSnapshot(ContractVersion.Current,
            ContractSamples.Session, new TranscriptCommitVersion(2), [entry]));
        Assert.ThrowsAny<ArgumentException>(() => new SessionSnapshot(ContractVersion.Current,
            ContractSamples.Session, new TranscriptCommitVersion(1), [entry, entry]));
        Assert.ThrowsAny<ArgumentException>(() => new CommittedTranscriptEntry(new TranscriptCommitVersion(0), write));
    }

    internal static UtcTimestamp Utc() => new(new DateTimeOffset(2026, 11, 6, 15, 0, 0, TimeSpan.Zero));
    internal static CaptureSourceSelection Selection() => new(CaptureSourceKind.Microphone, "synthetic-microphone");
}
