using Microsoft.Data.Sqlite;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;
using Sidecue.Infrastructure.Persistence;
using Sidecue.Infrastructure.Security;
using Xunit;

namespace Sidecue.Infrastructure.Tests.Transcripts;

public sealed class SqliteSessionStoreTests
{
    private const string AppVersion = "0.1.0-transcript-test";
    private const string SyntheticText = "Synthetic transcript café\0with a second marker";
    private static readonly SessionId Session = new(Id(1));
    private static readonly SourceId Source = new(Id(2));
    private static readonly TimelineEpochId EarlierTimeline = new(Id(3));
    private static readonly TimelineEpochId LaterTimeline = new(Id(4));
    private static readonly SourceIdentity EarlierSource = new(Session, Source, new StreamEpoch(2));
    private static readonly SourceIdentity LaterSource = new(Session, Source, new StreamEpoch(3));

    [SqlCipherFact]
    public async Task ReorderedFinalsAndGapCommitInArrivalOrderAcrossStreamEpochs()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var database = new TestDatabase(ct);
        FinalSegment first = Final(10, 12, 100);
        var next = new FinalSegment(Header(EarlierSource, 11, 3), new SegmentId(Id(101)),
            new SegmentRevision(0), [Interval(EarlierTimeline, 700, 800)], TimingCertainty.Exact, SyntheticText);
        var afterReconnect = new FinalSegment(Header(LaterSource, 10, 0), first.SegmentId,
            new SegmentRevision(0), [Interval(LaterTimeline, 10, 30)], TimingCertainty.Exact, SyntheticText);
        var gap = new TranscriptGap(Header(EarlierSource, 12, 13), Interval(EarlierTimeline, 200, 300),
            GapReason.TransportDisconnected, TimingCertainty.Estimated);

        await using SqliteSessionStore store = await database.OpenAsync(ct);
        Assert.Equal(0L, (await store.ReadSessionAsync(Session, ct)).CommitVersion.Value);
        AssertReceipt(await store.CommitTranscriptAsync(Write(first), ct), first, 1, TranscriptCommitDisposition.Applied);
        var wrongTimeline = new FinalSegment(Header(EarlierSource, 14, 4), next.SegmentId, next.Revision,
            [Interval(LaterTimeline, 700, 800)], next.Certainty, SyntheticText);
        await Assert.ThrowsAsync<EncryptedDatabaseException>(() => store.CommitTranscriptAsync(Write(wrongTimeline), ct));
        AssertReceipt(await store.CommitTranscriptAsync(Write(next), ct), next, 2, TranscriptCommitDisposition.Applied);
        AssertReceipt(await store.CommitTranscriptAsync(Write(afterReconnect), ct), afterReconnect, 3, TranscriptCommitDisposition.Applied);
        AssertReceipt(await store.CommitTranscriptAsync(Write(gap), ct), gap, 4, TranscriptCommitDisposition.Applied);
        AssertReceipt(await store.CommitTranscriptAsync(Write(Final(10, 12, 100)), ct), first, 1, TranscriptCommitDisposition.NoOp);

        var partial = new PartialSegment(Header(EarlierSource, 13, 14), first.SegmentId,
            new SegmentRevision(0), first.Intervals[0], "Synthetic late partial");
        Assert.Throws<ArgumentException>("event", () => Write(partial));

        SessionSnapshot snapshot = await store.ReadSessionAsync(Session, ct);
        Assert.Equal(4L, snapshot.CommitVersion.Value);
        Assert.Collection(snapshot.Entries,
            entry => AssertFinal(entry, first, 1),
            entry => AssertFinal(entry, next, 2),
            entry => AssertFinal(entry, afterReconnect, 3),
            entry =>
            {
                Assert.Equal(4L, entry.CommitVersion.Value);
                Assert.Equal(gap, Assert.IsType<TranscriptGap>(entry.Write.Event));
            });
    }

    [SqlCipherFact]
    public async Task ReopenPreservesAmendmentHistoryAndScopedWordSpeakers()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var database = new TestDatabase(ct);
        var spans = new[]
        {
            new WordSpeakerSpan(0, new SpeakerKey(Source, EarlierSource.StreamEpoch, "speaker-a"),
                [Interval(EarlierTimeline, 100, 120)], TimingCertainty.Exact),
            new WordSpeakerSpan(2, new SpeakerKey(Source, EarlierSource.StreamEpoch, "speaker-b"),
                [Interval(EarlierTimeline, 430, 480)], TimingCertainty.Estimated)
        };
        FinalSegment initial = Final(20, 1, 110, spans);
        FinalSegment alias = Final(24, 5, 110, spans);
        var conflictingSpans = new[]
        {
            spans[0],
            new WordSpeakerSpan(2, new SpeakerKey(Source, EarlierSource.StreamEpoch, "speaker-c"),
                [Interval(EarlierTimeline, 430, 480)], TimingCertainty.Estimated)
        };
        var amendment = new FinalAmendment(Header(EarlierSource, 22, 3), initial.SegmentId,
            new SegmentRevision(4), initial.Revision, initial.Intervals, TimingCertainty.Estimated,
            "Synthetic correction", speakerSpans: spans);

        await using (SqliteSessionStore store = await database.OpenAsync(ct))
        {
            AssertReceipt(await store.CommitTranscriptAsync(Write(initial), ct), initial, 1, TranscriptCommitDisposition.Applied);
            AssertReceipt(await store.CommitTranscriptAsync(Write(alias), ct), alias, 1, TranscriptCommitDisposition.NoOp);
            await Assert.ThrowsAsync<TranscriptConflictException>(() =>
                store.CommitTranscriptAsync(Write(Final(20, 1, 110, conflictingSpans)), ct));
            var replacement = new FinalSegment(Header(EarlierSource, 21, 2), initial.SegmentId,
                new SegmentRevision(3), initial.Intervals, TimingCertainty.Exact, "Synthetic replacement");
            await Assert.ThrowsAsync<TranscriptConflictException>(() => store.CommitTranscriptAsync(Write(replacement), ct));
            AssertReceipt(await store.CommitTranscriptAsync(Write(amendment), ct), amendment, 2, TranscriptCommitDisposition.Applied);
            var staleAmendment = new FinalAmendment(Header(EarlierSource, 23, 4), initial.SegmentId,
                new SegmentRevision(5), initial.Revision, initial.Intervals, TimingCertainty.Exact, "Synthetic stale correction");
            await Assert.ThrowsAsync<TranscriptConflictException>(() => store.CommitTranscriptAsync(Write(staleAmendment), ct));
        }

        await using SqliteSessionStore reopened = await database.OpenAsync(ct);
        AssertReceipt(await reopened.CommitTranscriptAsync(Write(Final(20, 1, 110, spans)), ct),
            initial, 1, TranscriptCommitDisposition.NoOp);
        AssertReceipt(await reopened.CommitTranscriptAsync(Write(Final(24, 5, 110, spans)), ct),
            alias, 1, TranscriptCommitDisposition.NoOp);
        await Assert.ThrowsAsync<TranscriptConflictException>(() =>
            reopened.CommitTranscriptAsync(Write(Final(24, 5, 110, conflictingSpans)), ct));
        SessionSnapshot snapshot = await reopened.ReadSessionAsync(Session, ct);
        Assert.Equal(2L, snapshot.CommitVersion.Value);
        Assert.Collection(snapshot.Entries,
            entry => AssertFinal(entry, initial, 1),
            entry =>
            {
                Assert.Equal(2L, entry.CommitVersion.Value);
                var persisted = Assert.IsType<FinalAmendment>(entry.Write.Event);
                Assert.Equal(amendment.Header, persisted.Header);
                Assert.Equal(amendment.SegmentId, persisted.SegmentId);
                Assert.Equal(amendment.Revision, persisted.Revision);
                Assert.Equal(amendment.AmendsRevision, persisted.AmendsRevision);
                Assert.Equal(amendment.Certainty, persisted.Certainty);
                Assert.Equal(amendment.Text, persisted.Text);
                Assert.Equal<TimelineInterval>(amendment.Intervals, persisted.Intervals);
                AssertSpans(spans, persisted.SpeakerSpans);
            });
    }

    [SqlCipherFact]
    public async Task ConcurrentRetriesShareOneCommittedVersion()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using var database = new TestDatabase(ct);
        await using SqliteSessionStore store = await database.OpenAsync(ct);
        FinalSegment initial = Final(30, 1, 120);
        FinalSegment next = Final(31, 2, 121);
        CommitReceipt[] receipts = await Task.WhenAll(
            store.CommitTranscriptAsync(Write(initial), ct),
            store.CommitTranscriptAsync(Write(next), ct),
            store.CommitTranscriptAsync(Write(Final(30, 1, 120)), ct));

        Assert.Equal(receipts[0].CommitVersion, receipts[2].CommitVersion);
        Assert.NotEqual(receipts[0].Disposition, receipts[2].Disposition);
        Assert.Equal(TranscriptCommitDisposition.Applied, receipts[1].Disposition);
        SessionSnapshot snapshot = await store.ReadSessionAsync(Session, ct);
        Assert.Equal(2L, snapshot.CommitVersion.Value);
        Assert.Collection(snapshot.Entries,
            entry => Assert.Equal(1L, entry.CommitVersion.Value),
            entry => Assert.Equal(2L, entry.CommitVersion.Value));
    }

    private static void AssertFinal(CommittedTranscriptEntry entry, FinalSegment expected, long version)
    {
        Assert.Equal(version, entry.CommitVersion.Value);
        var final = Assert.IsType<FinalSegment>(entry.Write.Event);
        Assert.Equal(expected.Header, final.Header);
        Assert.Equal(expected.SegmentId, final.SegmentId);
        Assert.Equal(expected.Revision, final.Revision);
        Assert.Equal(expected.Text, final.Text);
        Assert.Equal(expected.Certainty, final.Certainty);
        Assert.Equal(expected.Speaker, final.Speaker);
        Assert.Equal<TimelineInterval>(expected.Intervals, final.Intervals);
        AssertSpans(expected.SpeakerSpans, final.SpeakerSpans);
    }

    private static void AssertSpans(IReadOnlyList<WordSpeakerSpan> expected, IReadOnlyList<WordSpeakerSpan> actual)
    {
        Assert.Equal(expected.Count, actual.Count);
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].WordIndex, actual[index].WordIndex);
            Assert.Equal(expected[index].Speaker, actual[index].Speaker);
            Assert.Equal(expected[index].Certainty, actual[index].Certainty);
            Assert.Equal<TimelineInterval>(expected[index].Intervals, actual[index].Intervals);
        }
    }

    private static void AssertReceipt(CommitReceipt receipt, TranscriptEvent expected, long version, TranscriptCommitDisposition disposition)
    {
        Assert.Equal(ContractVersion.Current, receipt.Version);
        Assert.Equal(expected.Header.Source, receipt.Source);
        Assert.Equal(expected.Header.EventId, receipt.EventId);
        Assert.Equal(version, receipt.CommitVersion.Value);
        Assert.Equal(disposition, receipt.Disposition);
    }

    private static TranscriptWrite Write(TranscriptEvent value) => new(ContractVersion.Current, value);

    private static FinalSegment Final(int eventId, long sequence, int segment, IEnumerable<WordSpeakerSpan>? spans = null) =>
        new(Header(EarlierSource, eventId, sequence), new SegmentId(Id(segment)), new SegmentRevision(2),
            [Interval(EarlierTimeline, 100, 200), Interval(EarlierTimeline, 400, 500)],
            TimingCertainty.Exact, SyntheticText, speakerSpans: spans);

    private static TranscriptEventHeader Header(SourceIdentity source, int eventId, long sequence) =>
        new(ContractVersion.Current, source, new EventId(Id(eventId)), new EventSequence(sequence));

    private static TimelineInterval Interval(TimelineEpochId timeline, long start, long end) =>
        new(new TimelinePosition(timeline, new SessionOffset(start)), new TimelinePosition(timeline, new SessionOffset(end)));

    private static Guid Id(int value) => new(value, 0, 0, 0x80, 0, 0, 0, 0, 0, 0, 0);

    private sealed class TestDatabase : IDisposable
    {
        private readonly DirectoryInfo sandbox;
        private readonly SidecueDataPaths paths;
        private readonly EncryptedDatabaseStore database;

        internal TestDatabase(CancellationToken ct)
        {
            string nativePath = Environment.GetEnvironmentVariable(SqlCipherFactAttribute.NativePathVariable)
                ?? throw new InvalidOperationException("The reviewed SQLCipher test DLL must be configured.");
            sandbox = Directory.CreateTempSubdirectory("sidecue-transcript-test-");
            paths = SidecueDataPaths.ForRoot(Path.Combine(sandbox.FullName, "store"));
            database = new EncryptedDatabaseStore(paths, nativePath);
            try
            {
                using SqliteConnection connection = database.CreateNewTranscriptStore(AppVersion, ct);
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = """
                    INSERT INTO sessions(id, state, started_utc, completeness)
                    VALUES ($session, 'starting', 639313344000000000, 'unknown');
                    INSERT INTO timeline_epochs(session_id, id, anchor_utc, clock_kind, clock_frequency)
                    VALUES ($session, $earlierTimeline, 639313344000000000, 'stopwatch', 10000000),
                           ($session, $laterTimeline, 639313344010000000, 'stopwatch', 10000000);
                    INSERT INTO audio_streams(session_id, source_id, stream_epoch, timeline_epoch_id, kind, started_offset)
                    VALUES ($session, $source, $earlierEpoch, $earlierTimeline, 'mic', 0),
                           ($session, $source, $laterEpoch, $laterTimeline, 'mic', 0);
                    """;
                command.Parameters.AddWithValue("$session", Session.Value.ToByteArray(bigEndian: true));
                command.Parameters.AddWithValue("$source", Source.Value.ToByteArray(bigEndian: true));
                command.Parameters.AddWithValue("$earlierTimeline", EarlierTimeline.Value.ToByteArray(bigEndian: true));
                command.Parameters.AddWithValue("$laterTimeline", LaterTimeline.Value.ToByteArray(bigEndian: true));
                command.Parameters.AddWithValue("$earlierEpoch", EarlierSource.StreamEpoch.Value);
                command.Parameters.AddWithValue("$laterEpoch", LaterSource.StreamEpoch.Value);
                command.ExecuteNonQuery();
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal Task<SqliteSessionStore> OpenAsync(CancellationToken ct) =>
            SqliteSessionStore.OpenAsync(database, AppVersion, createNew: false, ct);

        public void Dispose()
        {
            if (Directory.Exists(paths.RootDirectory))
            {
                foreach (string file in Directory.EnumerateFiles(paths.RootDirectory))
                {
                    File.Delete(file);
                }
                Directory.Delete(paths.RootDirectory);
            }
            sandbox.Delete();
        }
    }
}
