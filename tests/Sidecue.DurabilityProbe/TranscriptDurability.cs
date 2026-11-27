using Microsoft.Data.Sqlite;
using Sidecue.Core.Contracts;
using Sidecue.Infrastructure.Persistence;
using Sidecue.Infrastructure.Security;

internal static class TranscriptDurability
{
    private const string AppVersion = "0.1.0-transcript-durability";
    private const string FinalCanary = "SidecueSyntheticTranscriptFinal603179";
    private const string AmendmentCanary = "SidecueSyntheticTranscriptAmendment840527";
    private const long AnchorUtc = 639313344000000000;
    private static readonly SessionId Session = new(Id(1));
    private static readonly SourceId Source = new(Id(2));
    private static readonly TimelineEpochId InitialTimeline = new(Id(3));
    private static readonly TimelineEpochId LaterTimeline = new(Id(4));
    private static readonly SourceIdentity InitialStream = new(Session, Source, new StreamEpoch(2));
    private static readonly SourceIdentity LaterStream = new(Session, Source, new StreamEpoch(3));
    private static readonly SegmentId Segment = new(Id(20));
    private static readonly SegmentRevision InitialRevision = new(2);
    private static readonly SegmentRevision AmendedRevision = new(4);
    private static readonly FinalSegment Initial = new(Header(InitialStream, 10, 7), Segment, InitialRevision,
        [Interval(InitialTimeline, 100, 200), Interval(InitialTimeline, 400, 500)],
        TimingCertainty.Exact, FinalCanary, new SpeakerKey(Source, InitialStream.StreamEpoch, "synthetic-speaker-a"));
    private static readonly TranscriptGap Gap = new(Header(LaterStream, 11, 0), Interval(LaterTimeline, 10, 60),
        GapReason.TransportDisconnected, TimingCertainty.Estimated);
    private static readonly FinalAmendment Amendment = new(Header(InitialStream, 12, 8), Segment, AmendedRevision,
        InitialRevision, [Interval(InitialTimeline, 100, 200), Interval(InitialTimeline, 400, 520)],
        TimingCertainty.Estimated, MakeAmendmentText(),
        speakerSpans:
        [
            new WordSpeakerSpan(0, new SpeakerKey(Source, InitialStream.StreamEpoch, "synthetic-speaker-a"),
                [Interval(InitialTimeline, 110, 140)], TimingCertainty.Exact),
            new WordSpeakerSpan(2, new SpeakerKey(Source, InitialStream.StreamEpoch, "synthetic-speaker-b"),
                [Interval(InitialTimeline, 440, 480)], TimingCertainty.Estimated)
        ]);

    internal static IReadOnlyList<string> Canaries { get; } = [FinalCanary, AmendmentCanary];

    internal static void CreateBaseline(EncryptedDatabaseStore store)
    {
        using SqliteConnection connection = store.CreateNewTranscriptStore(AppVersion);
        using (SqliteTransaction transaction = connection.BeginTransaction())
        {
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sessions (id, state, started_utc, completeness)
                VALUES ($session, 'listening', $anchor, 'unknown');
                INSERT INTO timeline_epochs (session_id, id, anchor_utc, clock_kind, clock_frequency)
                VALUES ($session, $initialTimeline, $anchor, 'stopwatch', 10000000),
                       ($session, $laterTimeline, $anchor + 10000000, 'stopwatch', 10000000);
                INSERT INTO audio_streams (session_id, source_id, stream_epoch, timeline_epoch_id, kind, started_offset)
                VALUES ($session, $source, $initialEpoch, $initialTimeline, 'mic', 0),
                       ($session, $source, $laterEpoch, $laterTimeline, 'mic', 0);
                """;
            command.Parameters.AddWithValue("$session", Session.Value.ToByteArray(bigEndian: true));
            command.Parameters.AddWithValue("$source", Source.Value.ToByteArray(bigEndian: true));
            command.Parameters.AddWithValue("$anchor", AnchorUtc);
            command.Parameters.AddWithValue("$initialTimeline", InitialTimeline.Value.ToByteArray(bigEndian: true));
            command.Parameters.AddWithValue("$laterTimeline", LaterTimeline.Value.ToByteArray(bigEndian: true));
            command.Parameters.AddWithValue("$initialEpoch", InitialStream.StreamEpoch.Value);
            command.Parameters.AddWithValue("$laterEpoch", LaterStream.StreamEpoch.Value);
            command.ExecuteNonQuery();
            transaction.Commit();
        }

        VerifyReceipt(Commit(connection, Initial), Initial, 1, TranscriptCommitDisposition.Applied);
        VerifyReceipt(Commit(connection, Gap), Gap, 2, TranscriptCommitDisposition.Applied);
    }

    internal static void RunChild(string native, string directory, string stage, Action pauseUntilKilled)
    {
        bool commitBeforePause = CommitsBeforePause(stage);
        var store = new EncryptedDatabaseStore(SidecueDataPaths.ForRoot(directory), native);
        using SqliteConnection connection = store.OpenExistingTranscriptStore(AppVersion);
        using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA wal_autocheckpoint=0; PRAGMA cache_size=-64; PRAGMA cache_spill=ON;";
            command.ExecuteNonQuery();
        }

        if (!commitBeforePause)
        {
            // This connection alone sees the trigger. It pauses inside the writer's
            // transaction after updating the current revision and before COMMIT.
            connection.CreateFunction<long>("sidecue_probe_pause", () =>
            {
                pauseUntilKilled();
                throw new InvalidOperationException("The interruption wait returned unexpectedly.");
            });
            using SqliteCommand trigger = connection.CreateCommand();
            trigger.CommandText = """
                CREATE TEMP TRIGGER sidecue_probe_pause_update
                AFTER UPDATE OF current_final_revision ON main.transcript_segments
                BEGIN
                    SELECT sidecue_probe_pause();
                END;
                """;
            trigger.ExecuteNonQuery();
        }

        CommitReceipt receipt = Commit(connection, Amendment);
        Require(commitBeforePause, "The amendment returned before the pre-commit pause.");
        VerifyReceipt(receipt, Amendment, 3, TranscriptCommitDisposition.Applied);
        pauseUntilKilled();
        throw new InvalidOperationException("The interruption wait returned unexpectedly.");
    }

    internal static void VerifyRecovered(EncryptedDatabaseStore store, string stage)
    {
        bool amendmentCommitted = CommitsBeforePause(stage);
        using SqliteConnection connection = store.OpenExistingTranscriptStore(AppVersion);
        Require(Scalar(connection, "PRAGMA cipher_status;") is "1", "Transcript store encryption is inactive.");
        Require(Scalar(connection, "PRAGMA journal_mode;") is "wal", "Transcript store WAL is inactive.");
        Require(Scalar(connection, "PRAGMA integrity_check;") is "ok", "Transcript SQLite integrity check failed.");
        Require(Scalar(connection, "PRAGMA cipher_integrity_check;") is null, "Transcript SQLCipher integrity check failed.");
        Require(Scalar(connection, "PRAGMA foreign_key_check;") is null, "Transcript foreign key check failed.");

        VerifySnapshot(connection, amendmentCommitted);
        // The original event ID must either apply at version 3 after a rollback
        // or return the committed version from the interrupted caller.
        VerifyReceipt(Commit(connection, Amendment), Amendment, 3,
            amendmentCommitted ? TranscriptCommitDisposition.NoOp : TranscriptCommitDisposition.Applied);
        VerifyReceipt(Commit(connection, Amendment), Amendment, 3, TranscriptCommitDisposition.NoOp);
        VerifySnapshot(connection, amendmentCommitted: true);
    }

    private static void VerifySnapshot(SqliteConnection connection, bool amendmentCommitted)
    {
        SessionSnapshot snapshot = TranscriptJournal.Read(connection, Session, CancellationToken.None);
        int expectedCount = amendmentCommitted ? 3 : 2;
        Require(snapshot.Version == ContractVersion.Current && snapshot.SessionId == Session &&
            snapshot.CommitVersion.Value == expectedCount && snapshot.Entries.Length == expectedCount,
            "The recovered transcript version or entry count differs from the interruption boundary.");
        Require(Scalar(connection, "SELECT current_final_revision FROM transcript_segments;")
            is long current && current == (amendmentCommitted ? AmendedRevision.Value : InitialRevision.Value),
            "The recovered current revision differs from the interruption boundary.");

        TranscriptEvent first = VerifyEntry(snapshot.Entries[0], Initial, 1);
        Require(first is FinalSegment final && final.SegmentId == Initial.SegmentId &&
            final.Revision == Initial.Revision && final.Text == Initial.Text && final.Certainty == Initial.Certainty &&
            final.Intervals.SequenceEqual(Initial.Intervals) && final.Speaker == Initial.Speaker && final.SpeakerSpans.IsEmpty,
            "The recovered baseline final has missing or changed evidence.");
        Require(VerifyEntry(snapshot.Entries[1], Gap, 2) is TranscriptGap gap && gap == Gap,
            "The recovered gap has missing or changed evidence.");
        if (amendmentCommitted)
        {
            TranscriptEvent last = VerifyEntry(snapshot.Entries[2], Amendment, 3);
            Require(last is FinalAmendment amendment && amendment.SegmentId == Amendment.SegmentId &&
                amendment.Revision == Amendment.Revision && amendment.AmendsRevision == Initial.Revision &&
                amendment.Text == Amendment.Text && amendment.Certainty == Amendment.Certainty &&
                amendment.Intervals.SequenceEqual(Amendment.Intervals) && amendment.Speaker == Amendment.Speaker &&
                amendment.SpeakerSpans.Length == Amendment.SpeakerSpans.Length &&
                amendment.SpeakerSpans.Zip(Amendment.SpeakerSpans).All(pair =>
                    pair.First.WordIndex == pair.Second.WordIndex && pair.First.Speaker == pair.Second.Speaker &&
                    pair.First.Certainty == pair.Second.Certainty && pair.First.Intervals.SequenceEqual(pair.Second.Intervals)),
                "The recovered amendment has missing or changed evidence.");
        }
    }

    private static TranscriptEvent VerifyEntry(CommittedTranscriptEntry entry, TranscriptEvent expected, long version)
    {
        Require(entry.Write.Version == ContractVersion.Current && entry.CommitVersion.Value == version &&
            entry.Write.Event.Header == expected.Header, "A recovered event identity or commit version differs.");
        return entry.Write.Event;
    }

    private static CommitReceipt Commit(SqliteConnection connection, TranscriptEvent value) =>
        TranscriptJournal.Commit(connection, new TranscriptWrite(ContractVersion.Current, value), CancellationToken.None);

    private static void VerifyReceipt(CommitReceipt receipt, TranscriptEvent expected, long version,
        TranscriptCommitDisposition disposition)
    {
        Require(receipt.Version == ContractVersion.Current && receipt.Source == expected.Header.Source &&
            receipt.EventId == expected.Header.EventId && receipt.CommitVersion.Value == version &&
            receipt.Disposition == disposition, "A transcript commit receipt differs from its expected outcome.");
    }

    private static object? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar();
    }

    private static bool CommitsBeforePause(string stage) => stage switch
    {
        "transcript-before" => false,
        "transcript-after" => true,
        _ => throw new ArgumentException("Unknown transcript interruption boundary.", nameof(stage))
    };

    private static string MakeAmendmentText()
    {
        // Include the marker on overflow pages that can spill to the live WAL.
        return string.Concat(Enumerable.Repeat(AmendmentCanary + " " + new string('z', 1024) + " ", 192));
    }

    private static TranscriptEventHeader Header(SourceIdentity source, int id, long sequence) =>
        new(ContractVersion.Current, source, new EventId(Id(id)), new EventSequence(sequence));

    private static TimelineInterval Interval(TimelineEpochId timeline, long start, long end) =>
        new(new TimelinePosition(timeline, new SessionOffset(start)), new TimelinePosition(timeline, new SessionOffset(end)));

    private static Guid Id(int value) => new(value, 0, 0, 0x80, 0, 0, 0, 0, 0, 0, 0);

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
