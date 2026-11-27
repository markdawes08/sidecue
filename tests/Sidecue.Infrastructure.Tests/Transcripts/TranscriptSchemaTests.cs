using System.Globalization;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Sidecue.Infrastructure.Persistence;
using Sidecue.Infrastructure.Security;
using Xunit;

namespace Sidecue.Infrastructure.Tests.Transcripts;

public sealed class TranscriptSchemaTests
{
    private const string TestAppVersion = "0.1.0-schema-test";

    [SqlCipherFact]
    public void CreatesSchemaInEncryptedWalStore()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var database = new TestDatabase();
        using (SqliteConnection connection = database.Store.CreateNewTranscriptStore(TestAppVersion, cancellationToken))
        {
            Assert.Equal("2", Scalar(connection, "SELECT count(*) FROM schema_versions;"));
            Assert.Equal("7", Scalar(connection, """
                SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name IN (
                    'sessions', 'timeline_epochs', 'audio_streams', 'transcript_events',
                    'transcript_segments', 'transcript_segment_revisions', 'transcript_gaps'
                );
                """));
        }

        using SqliteConnection reopened = database.Store.OpenExistingTranscriptStore(TestAppVersion, cancellationToken);
        Assert.Throws<EncryptedDatabaseException>(() =>
            SchemaMigrator.Apply(reopened, [TranscriptSchema.Migrations[0]], TestAppVersion, cancellationToken));
        Assert.Equal("2", Scalar(reopened, "SELECT count(*) FROM schema_versions;"));
        Assert.Equal("1", Scalar(reopened, "PRAGMA cipher_status;"));
        Assert.Equal("wal", Scalar(reopened, "PRAGMA journal_mode;"));
        Assert.Equal("1", Scalar(reopened, "PRAGMA foreign_keys;"));
        Assert.Equal("ok", Scalar(reopened, "PRAGMA integrity_check;"));
    }

    [SqlCipherFact]
    public void UpgradePreservesScopedIdentityAndSourceIntervalsAcrossReopen()
    {
        const string ranges = "[[100,200],[300,400]]";
        const string finalText = "Synthetic finalized transcript";
        byte[] sessionId = Guid.Parse("10000000-0000-0000-0000-000000000001").ToByteArray(bigEndian: true);
        byte[] sourceId = Guid.Parse("20000000-0000-0000-0000-000000000001").ToByteArray(bigEndian: true);
        byte[] timelineId = Guid.Parse("30000000-0000-0000-0000-000000000001").ToByteArray(bigEndian: true);
        byte[] otherTimelineId = Guid.Parse("30000000-0000-0000-0000-000000000002").ToByteArray(bigEndian: true);
        byte[] eventId = Guid.Parse("40000000-0000-0000-0000-000000000001").ToByteArray(bigEndian: true);
        byte[] gapEventId = Guid.Parse("40000000-0000-0000-0000-000000000002").ToByteArray(bigEndian: true);
        byte[] segmentId = Guid.Parse("50000000-0000-0000-0000-000000000001").ToByteArray(bigEndian: true);
        byte[] hash = SHA256.HashData("Synthetic finalized transcript"u8);
        const long anchorUtc = 639313344000000000;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var database = new TestDatabase();
        string? firstChecksum;
        using (SqliteConnection versionOne = database.Store.CreateNew())
        {
            SchemaMigrator.Apply(versionOne, [TranscriptSchema.Migrations[0]], "0.1.0-first-version", cancellationToken);
            firstChecksum = Scalar(versionOne, "SELECT hex(checksum) FROM schema_versions WHERE version = 1;");
            Execute(versionOne, null, """
                INSERT INTO sessions(id, state, started_utc, completeness)
                VALUES ($session, 'starting', $anchorUtc, 'unknown');
                INSERT INTO timeline_epochs(session_id, id, anchor_utc, clock_kind, clock_frequency)
                VALUES ($session, $timeline, $anchorUtc, 'stopwatch', 10000000),
                       ($session, $otherTimeline, $anchorUtc, 'stopwatch', 10000000);
                INSERT INTO audio_streams(session_id, source_id, stream_epoch, timeline_epoch_id, kind, started_offset)
                VALUES ($session, $source, 3, $timeline, 'mic', 0);
                """, ("$session", sessionId), ("$source", sourceId), ("$timeline", timelineId),
                ("$otherTimeline", otherTimelineId), ("$anchorUtc", anchorUtc));
        }

        using (SqliteConnection upgraded = database.Store.OpenExistingTranscriptStore(TestAppVersion, cancellationToken))
        {
            Assert.Equal(firstChecksum, Scalar(upgraded, "SELECT hex(checksum) FROM schema_versions WHERE version = 1;"));
            Assert.Equal("0.1.0-first-version", Scalar(upgraded, "SELECT app_version FROM schema_versions WHERE version = 1;"));
            Assert.Equal(TestAppVersion, Scalar(upgraded, "SELECT app_version FROM schema_versions WHERE version = 2;"));
            const string insertEvent = """
                INSERT INTO transcript_events(
                    session_id, source_id, stream_epoch, event_id, event_sequence, commit_version, kind,
                    payload_hash, timeline_epoch_id, start_offset, end_offset, timing_certainty)
                VALUES ($session, $source, 3, $event, 7, 1, 'final', $hash, $timeline, 100, 400, 'exact');
                """;
            SqliteException wrongEpoch = Assert.Throws<SqliteException>(() => Execute(upgraded, null, insertEvent,
                ("$session", sessionId), ("$source", sourceId), ("$event", eventId), ("$hash", hash), ("$timeline", otherTimelineId)));
            Assert.Equal(787, wrongEpoch.SqliteExtendedErrorCode);

            using SqliteTransaction transaction = upgraded.BeginTransaction();
            Execute(upgraded, transaction, insertEvent, ("$session", sessionId), ("$source", sourceId),
                ("$event", eventId), ("$hash", hash), ("$timeline", timelineId));
            const string insertGap = """
                INSERT INTO transcript_events(
                    session_id, source_id, stream_epoch, event_id, event_sequence, commit_version, kind,
                    payload_hash, timeline_epoch_id, start_offset, timing_certainty)
                VALUES ($session, $source, 3, $event, 8, 2, 'gap', $hash, $timeline, 500, 'unknown');
                INSERT INTO transcript_gaps(session_id, source_id, stream_epoch, event_id, reason)
                VALUES ($session, $source, 3, $event, 'transport_disconnected');
                """;
            SqliteException reusedId = Assert.Throws<SqliteException>(() => Execute(upgraded, transaction, insertGap,
                ("$session", sessionId), ("$source", sourceId), ("$event", eventId), ("$hash", hash), ("$timeline", timelineId)));
            Assert.Equal(2067, reusedId.SqliteExtendedErrorCode);
            Execute(upgraded, transaction, insertGap, ("$session", sessionId), ("$source", sourceId),
                ("$event", gapEventId), ("$hash", hash), ("$timeline", timelineId));
            Execute(upgraded, transaction, """
                INSERT INTO transcript_segments(id, session_id, source_id, stream_epoch, ordinal, current_final_revision)
                VALUES ($segment, $session, $source, 3, 1, 0);
                INSERT INTO transcript_segment_revisions(
                    segment_id, revision, session_id, source_id, stream_epoch, event_id, kind, timing_ranges_json, text)
                VALUES ($segment, 0, $session, $source, 3, $event, 'final', $ranges, $text);
                UPDATE sessions SET transcript_version = 2 WHERE id = $session;
                """, ("$segment", segmentId), ("$session", sessionId), ("$source", sourceId), ("$event", eventId),
                ("$ranges", ranges), ("$text", finalText));
            transaction.Commit();
        }

        using SqliteConnection reopened = database.Store.OpenExistingTranscriptStore(TestAppVersion, cancellationToken);
        Assert.Equal("1", Scalar(reopened, """
            SELECT count(*) FROM transcript_gaps g
            JOIN transcript_events e USING (session_id, source_id, stream_epoch, event_id, kind)
            WHERE g.reason = 'transport_disconnected' AND e.start_offset = 500
                AND e.end_offset IS NULL AND e.commit_version = 2;
            """));
        using SqliteCommand read = reopened.CreateCommand();
        read.CommandText = """
            SELECT e.session_id, e.source_id, e.stream_epoch, e.timeline_epoch_id, e.start_offset, e.end_offset,
                   r.timing_ranges_json, r.text, e.commit_version, s.transcript_version
            FROM transcript_segments seg
            JOIN transcript_segment_revisions r ON r.segment_id = seg.id AND r.revision = seg.current_final_revision
            JOIN transcript_events e ON (e.session_id, e.source_id, e.stream_epoch, e.event_id)
                = (r.session_id, r.source_id, r.stream_epoch, r.event_id)
            JOIN sessions s ON s.id = e.session_id;
            """;
        using SqliteDataReader revision = read.ExecuteReader();
        Assert.True(revision.Read());
        Assert.Equal(sessionId, revision.GetFieldValue<byte[]>(0));
        Assert.Equal(sourceId, revision.GetFieldValue<byte[]>(1));
        Assert.Equal(3L, revision.GetInt64(2));
        Assert.Equal(timelineId, revision.GetFieldValue<byte[]>(3));
        Assert.Equal(100L, revision.GetInt64(4));
        Assert.Equal(400L, revision.GetInt64(5));
        Assert.Equal(ranges, revision.GetString(6));
        Assert.Equal(finalText, revision.GetString(7));
        Assert.Equal(1L, revision.GetInt64(8));
        Assert.Equal(2L, revision.GetInt64(9));
        Assert.False(revision.Read());
    }

    [SqlCipherFact]
    public void FailedUpgradeRollsBackSchemaAndVersionHistoryBeforeRetry()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using var database = new TestDatabase();
        using (SqliteConnection connection = database.Store.CreateNew())
        {
            string firstMigration = TranscriptSchema.Migrations[0];
            SchemaMigrator.Apply(connection, [firstMigration], "0.1.0-first-version", cancellationToken);
            string brokenUpgrade = TranscriptSchema.Migrations[1] + """

                CREATE TABLE migration_rollback_probe (id INTEGER PRIMARY KEY);
                UPDATE schema_versions SET app_version = 'rolled-back change' WHERE version = 1;
                INSERT INTO migration_rollback_probe(id) VALUES (1), (1);
                """;
            Assert.Throws<EncryptedDatabaseException>(() =>
                SchemaMigrator.Apply(connection, [firstMigration, brokenUpgrade], TestAppVersion, cancellationToken));
        }

        using (SqliteConnection reopened = database.Store.OpenExisting())
        {
            Assert.Equal("1", Scalar(reopened, "SELECT count(*) FROM schema_versions;"));
            Assert.Equal("0.1.0-first-version", Scalar(reopened, "SELECT app_version FROM schema_versions WHERE version = 1;"));
            Assert.Equal("0", Scalar(reopened, """
                SELECT count(*) FROM sqlite_schema
                WHERE name IN ('transcript_events', 'transcript_segment_revisions', 'migration_rollback_probe');
                """));
            Assert.Throws<EncryptedDatabaseException>(() =>
                SchemaMigrator.Apply(reopened, [TranscriptSchema.Migrations[0] + "\n-- changed history"], TestAppVersion, cancellationToken));
        }

        using SqliteConnection upgraded = database.Store.OpenExistingTranscriptStore(TestAppVersion, cancellationToken);
        Assert.Equal("2", Scalar(upgraded, "SELECT count(*) FROM schema_versions;"));
        Assert.Equal("1", Scalar(upgraded, "SELECT count(*) FROM sqlite_schema WHERE name = 'transcript_events';"));
    }

    private static void Execute(
        SqliteConnection connection, SqliteTransaction? transaction, string sql, params (string Name, object Value)[] parameters)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }
        command.ExecuteNonQuery();
    }

    private static string? Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private sealed class TestDatabase : IDisposable
    {
        private readonly DirectoryInfo sandbox = Directory.CreateTempSubdirectory("sidecue-schema-test-");
        private readonly SidecueDataPaths paths;

        public TestDatabase()
        {
            paths = SidecueDataPaths.ForRoot(Path.Combine(sandbox.FullName, "store"));
            string nativePath = Environment.GetEnvironmentVariable(SqlCipherFactAttribute.NativePathVariable)
                ?? throw new InvalidOperationException("The reviewed SQLCipher test DLL must be configured.");
            Store = new EncryptedDatabaseStore(paths, nativePath);
        }

        public EncryptedDatabaseStore Store { get; }

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
