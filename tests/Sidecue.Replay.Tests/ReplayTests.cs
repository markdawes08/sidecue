using System.Globalization;
using System.Text.Json;
using Sidecue.Core.Contracts;
using Sidecue.Replay.Tests.Fakes;
using Sidecue.Replay.Tests.Reference;
using Xunit;

namespace Sidecue.Replay.Tests;

public sealed class ReplayTests
{
    [Fact]
    public async Task FixedFixtureProducesRepeatableExpectedObservations()
    {
        var root = RepositoryRoot();
        var fixturePath = Path.Combine(root, "tests", "fixtures", "replay", "first-meeting.json");
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath, TestContext.Current.CancellationToken));
        var expected = fixture.RootElement.GetProperty("expected");

        var first = await ReplayHarness.RunAsync(fixturePath);
        var second = await ReplayHarness.RunAsync(fixturePath);

        Assert.Equal(first.ToJson(), second.ToJson());
        Assert.Equal("first-meeting", first.Id);
        Assert.Equal(ReadStrings(expected, "transcriptOutcomes"), first.TranscriptOutcomes);
        Assert.Equal(ReadStrings(expected, "answerOutcomes"), first.AnswerOutcomes);
        Assert.Equal(expected.GetProperty("durableCommits").GetInt32(), first.DurableCommits);
        Assert.Equal(expected.GetProperty("currentFinalCount").GetInt32(), first.CurrentFinalCount);
        Assert.Equal(expected.GetProperty("partialCount").GetInt32(), first.PartialCount);
        Assert.Equal(expected.GetProperty("framesDisposed").GetInt32(), first.FramesDisposed);
        Assert.Equal(expected.GetProperty("visibleAnswer").GetString(), first.VisibleAnswer);
        Assert.Equal(3, first.TranscriptState.Length);
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 20, 0, 0, TimeSpan.Zero),
            DateTimeOffset.Parse(first.AnchorUtc, CultureInfo.InvariantCulture));
        Assert.Equal(new DateTimeOffset(2026, 11, 6, 20, 0, 0, TimeSpan.Zero).AddMilliseconds(500),
            DateTimeOffset.Parse(first.FinalUtc, CultureInfo.InvariantCulture));

        // Only normalized observations from this explicitly fictional fixture are exported.
        var output = Path.Combine(root, "artifacts", "replay");
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(output, "first-meeting-run1.json"), first.ToJson(), TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(output, "first-meeting-run2.json"), second.ToJson(), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task UnknownFixtureVersionIsRejectedBeforeReplay()
    {
        var output = Path.Combine(RepositoryRoot(), "artifacts", "replay");
        Directory.CreateDirectory(output);
        var invalidPath = Path.Combine(output, "invalid-schema.json");
        await File.WriteAllTextAsync(invalidPath, "{\"schemaVersion\":2}", TestContext.Current.CancellationToken);

        await Assert.ThrowsAnyAsync<ArgumentException>(() => ReplayHarness.RunAsync(invalidPath));
    }

    [Fact]
    public void FingerprintsCompareReconstructedContentAndPreserveMetadata()
    {
        var first = Final();
        var reconstructed = Final();
        var changedText = Final(text: "Changed synthetic evidence.");
        var changedSequence = Final(sequence: 2);

        Assert.Equal(TranscriptFingerprint.Compute(first), TranscriptFingerprint.Compute(reconstructed));
        Assert.NotEqual(TranscriptFingerprint.Compute(first), TranscriptFingerprint.Compute(changedText));
        Assert.NotEqual(TranscriptFingerprint.Compute(first), TranscriptFingerprint.Compute(changedSequence));
        Assert.Equal(TranscriptFingerprint.Payload(first), TranscriptFingerprint.Payload(changedSequence));
    }

    [Fact]
    public async Task StoreRetriesAreIdempotentAndConflictsPreserveTheOriginalWrite()
    {
        var store = new FakeSessionStore();
        var committed = await store.CommitTranscriptAsync(Write(Final()), TestContext.Current.CancellationToken);
        var replayed = await store.CommitTranscriptAsync(Write(Final()), TestContext.Current.CancellationToken);

        Assert.Equal(TranscriptCommitDisposition.Applied, committed.Disposition);
        Assert.Equal(TranscriptCommitDisposition.NoOp, replayed.Disposition);
        Assert.Equal(committed.CommitVersion, replayed.CommitVersion);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CommitTranscriptAsync(
            Write(Final(text: "Conflicting synthetic evidence.")), TestContext.Current.CancellationToken));
        var snapshot = await store.ReadSessionAsync(Session(), TestContext.Current.CancellationToken);
        Assert.Equal(1, snapshot.CommitVersion.Value);
        Assert.Equal("Synthetic final.", Assert.IsType<FinalSegment>(Assert.Single(snapshot.Entries).Write.Event).Text);
    }

    [Fact]
    public async Task CaptureCancellationLeavesTransferredAndQueuedBuffersWithTheirOwners()
    {
        var clock = new ManualSessionClock(Anchor());
        var format = new AudioFormat(16_000, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian);
        AudioFrameMetadata[] metadata =
        [
            new(ContractVersion.Current, Source(), new FrameSequence(0), Interval(0, 100_000), format, 1_600),
            new(ContractVersion.Current, Source(), new FrameSequence(1), Interval(100_000, 200_000), format, 1_600)
        ];
        var capture = new FakeCaptureSource(clock, metadata);
        var request = new CaptureRequest(ContractVersion.Current, Source(), clock.Anchor,
            new CaptureSourceSelection(CaptureSourceKind.Microphone, "synthetic-microphone"),
            new AudioBufferPolicy(2_000_000, 64_000));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var run = await capture.OpenAsync(request, TestContext.Current.CancellationToken);
        await using var frames = run.ReadFramesAsync(cancellation.Token).GetAsyncEnumerator(cancellation.Token);
        Assert.True(await frames.MoveNextAsync());
        var transferred = frames.Current;
        try
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => frames.MoveNextAsync().AsTask());
            Assert.Equal(2, capture.OwnersCreated);
            Assert.Equal(0, capture.OwnersDisposed);
        }
        finally
        {
            transferred.Dispose();
            await run.DisposeAsync();
        }

        Assert.Equal(2, capture.OwnersDisposed);
    }

    [Fact]
    public void ManualClockAdvancesMonotonicAndCalendarValuesWithoutWallTime()
    {
        var clock = new ManualSessionClock(Anchor());
        clock.AdvanceTo(500_000);

        Assert.Equal(500_000, clock.GetPosition().Offset.Value);
        Assert.Equal(Anchor().Origin.TimelineEpoch, clock.GetPosition().TimelineEpoch);
        Assert.Equal(Anchor().UtcAtOrigin.Value.AddMilliseconds(500), clock.GetUtcNow().Value);
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.AdvanceTo(499_999));
    }

    [Fact]
    public async Task FinalChangesRequireAnExplicitAmendmentOfTheCurrentRevision()
    {
        var store = new FakeSessionStore();
        var model = new TranscriptReferenceModel(store);
        Assert.Equal("committed", await model.ApplyAsync(Final(), TestContext.Current.CancellationToken));
        Assert.Equal("conflict", await model.ApplyAsync(Final(eventNumber: 2, revision: 2, text: "Implicit change."), TestContext.Current.CancellationToken));

        var invalidAmendment = new FinalAmendment(Header(3), Segment(), new SegmentRevision(2),
            new SegmentRevision(0), [Interval()], TimingCertainty.Exact, "Wrong base revision.");
        var validAmendment = new FinalAmendment(Header(4), Segment(), new SegmentRevision(2),
            new SegmentRevision(1), [Interval()], TimingCertainty.Exact, "Explicit correction.");
        Assert.Equal("conflict", await model.ApplyAsync(invalidAmendment, TestContext.Current.CancellationToken));
        Assert.Equal("amended", await model.ApplyAsync(validAmendment, TestContext.Current.CancellationToken));
        Assert.Equal("Explicit correction.", Assert.IsType<FinalAmendment>(Assert.Single(model.CurrentFinals)).Text);
        var stored = await store.ReadSessionAsync(Session(), TestContext.Current.CancellationToken);
        Assert.Equal(2, stored.Entries.Length);
        Assert.IsType<FinalSegment>(stored.Entries[0].Write.Event);
        Assert.IsType<FinalAmendment>(stored.Entries[1].Write.Event);
    }

    private static string[] ReadStrings(JsonElement value, string name) =>
        value.GetProperty(name).EnumerateArray().Select(item => item.GetString()!).ToArray();

    private static SessionId Session() => new(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static SourceIdentity Source() =>
        new(Session(), new SourceId(Guid.Parse("33333333-3333-3333-3333-333333333333")), new StreamEpoch(0));
    private static SegmentId Segment() => new(Guid.Parse("55555555-5555-5555-5555-555555555555"));
    private static TimelinePosition Position(long offset) =>
        new(new TimelineEpochId(Guid.Parse("22222222-2222-2222-2222-222222222222")), new SessionOffset(offset));
    private static TimelineInterval Interval(long start = 0, long end = 100_000) => new(Position(start), Position(end));
    private static SessionClockAnchor Anchor() =>
        new(Session(), Position(0), new UtcTimestamp(new DateTimeOffset(2026, 11, 6, 20, 0, 0, TimeSpan.Zero)));
    private static TranscriptEventHeader Header(int eventNumber = 1, long sequence = 1) =>
        new(ContractVersion.Current, Source(), new EventId(Guid.Parse(
            string.Create(CultureInfo.InvariantCulture, $"70000000-0000-0000-0000-{eventNumber:D12}"))), new EventSequence(sequence));
    private static FinalSegment Final(int eventNumber = 1, long sequence = 1, long revision = 1,
        string text = "Synthetic final.") =>
        new(Header(eventNumber, sequence), Segment(), new SegmentRevision(revision), [Interval()], TimingCertainty.Exact, text);
    private static TranscriptWrite Write(TranscriptEvent value) => new(ContractVersion.Current, value);

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Sidecue.slnx"))) { return directory.FullName; }
        }
        throw new DirectoryNotFoundException("Run replay tests from a built Sidecue checkout.");
    }
}
