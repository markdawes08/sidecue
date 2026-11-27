using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;

namespace Sidecue.Infrastructure.Persistence;

internal static class TranscriptJournal
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Dictionary<string, GapReason> GapReasons =
        Enum.GetValues<GapReason>().ToDictionary(GapReasonName, StringComparer.Ordinal);

    private const string SelectEntries = """
        SELECT e.source_id, e.stream_epoch, e.event_id, e.event_sequence, e.commit_version, e.kind,
               e.timeline_epoch_id, e.start_offset, e.end_offset, e.timing_certainty,
               coalesce(s.source_segment_id, s.id), r.revision, r.amends_revision, r.timing_ranges_json,
               CAST(r.text AS BLOB), CAST(r.speaker_key AS BLOB), r.speaker_spans_json, g.reason
        FROM transcript_events e
        LEFT JOIN transcript_segment_revisions r ON (r.session_id, r.source_id, r.stream_epoch, r.event_id, r.kind)
            = (e.session_id, e.source_id, e.stream_epoch, e.event_id, e.kind)
        LEFT JOIN transcript_segments s ON s.id = r.segment_id
        LEFT JOIN transcript_gaps g ON (g.session_id, g.source_id, g.stream_epoch, g.event_id, g.kind)
            = (e.session_id, e.source_id, e.stream_epoch, e.event_id, e.kind)
        """;

    internal static CommitReceipt Commit(SqliteConnection connection, TranscriptWrite write, CancellationToken ct)
    {
        TranscriptPayload payload;
        try { payload = TranscriptPayload.Encode(write); }
        catch (ArgumentException) { throw new TranscriptConflictException(); }

        TranscriptEventHeader header = write.Event.Header;
        try
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            (long version, TranscriptCommitDisposition disposition) = CommitEvent(transaction, header, payload, ct);
            var receipt = new CommitReceipt(write.Version, header.Source, header.EventId,
                new TranscriptCommitVersion(version), disposition);
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return receipt;
        }
        catch (Exception exception) when (exception is SqliteException or InvalidCastException or InvalidOperationException or ArgumentException)
        {
            throw new EncryptedDatabaseException("The transcript transaction could not be committed. Recovery is required.");
        }
    }

    internal static SessionSnapshot Read(SqliteConnection connection, SessionId session, CancellationToken ct)
    {
        try
        {
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: true);
            using SqliteCommand version = Command(transaction, "SELECT transcript_version FROM sessions WHERE id = $session;");
            version.Parameters.AddWithValue("$session", Bytes(session.Value));
            long committed = (long?)version.ExecuteScalar() ?? 0;
            var entries = new List<CommittedTranscriptEntry>();
            using (SqliteCommand command = Command(transaction, SelectEntries +
                "\nWHERE e.session_id = $session ORDER BY e.commit_version;"))
            {
                command.Parameters.AddWithValue("$session", Bytes(session.Value));
                using SqliteDataReader reader = command.ExecuteReader();
                while (reader.Read())
                {
                    ct.ThrowIfCancellationRequested();
                    CommittedTranscriptEntry entry = ReadEntry(reader, session);
                    if (entry.CommitVersion.Value != entries.Count + 1L)
                    {
                        throw CorruptHistory();
                    }
                    entries.Add(entry);
                }
            }
            ct.ThrowIfCancellationRequested();
            transaction.Commit();
            return new SessionSnapshot(ContractVersion.Current, session, new TranscriptCommitVersion(committed), entries);
        }
        catch (Exception exception) when (exception is SqliteException or InvalidCastException or InvalidOperationException or ArgumentException)
        {
            throw CorruptHistory();
        }
    }

    private static (long Version, TranscriptCommitDisposition Disposition) CommitEvent(
        SqliteTransaction transaction, TranscriptEventHeader header, TranscriptPayload payload, CancellationToken ct)
    {
        long? duplicate = FindCommittedEvent(transaction, header, payload.Hash);
        if (duplicate is { } original)
        {
            return (original, TranscriptCommitDisposition.NoOp);
        }

        SegmentRow? segment = null;
        if (payload.Segment is { } segmentId && payload.Revision is { } revision)
        {
            segment = FindSegment(transaction, header.Source, segmentId);
            if (segment is { } existing)
            {
                CommittedTranscriptEntry? prior = FindRevision(transaction, header.Source.SessionId, existing.Id, revision);
                if (prior is not null)
                {
                    if (!TranscriptPayload.FingerprintRevision(prior.Write).AsSpan().SequenceEqual(payload.RevisionHash))
                    {
                        throw new TranscriptConflictException();
                    }
                    InsertAlias(transaction, header, prior.Write.Event.Header.EventId, payload.Hash);
                    return (prior.CommitVersion.Value, TranscriptCommitDisposition.NoOp);
                }
            }
            // A final opens a segment. An amendment must advance its current final.
            if (segment?.CurrentRevision != payload.Amends)
            {
                throw new TranscriptConflictException();
            }
        }

        ct.ThrowIfCancellationRequested();
        long version = AdvanceVersion(transaction, header.Source.SessionId);
        InsertEvent(transaction, header, payload, version);
        if (payload.GapReason is { } reason)
        {
            InsertGap(transaction, header, reason);
        }
        else
        {
            Guid storedId = segment?.Id ?? InsertSegment(transaction, header.Source, payload, version);
            InsertRevision(transaction, header, payload, storedId);
            if (segment is not null)
            {
                using SqliteCommand update = Command(transaction,
                    "UPDATE transcript_segments SET current_final_revision = $revision WHERE id = $segment;");
                update.Parameters.AddWithValue("$revision", payload.Revision!.Value.Value);
                update.Parameters.AddWithValue("$segment", Bytes(storedId));
                update.ExecuteNonQuery();
            }
        }
        return (version, TranscriptCommitDisposition.Applied);
    }

    private static long? FindCommittedEvent(SqliteTransaction transaction, TranscriptEventHeader header, byte[] hash)
    {
        using SqliteCommand command = Command(transaction, """
            SELECT commit_version, payload_hash FROM transcript_events
            WHERE session_id = $session AND source_id = $source AND stream_epoch = $epoch AND event_id = $event
            UNION ALL
            SELECT e.commit_version, a.payload_hash FROM transcript_event_aliases a
            JOIN transcript_events e ON (e.session_id, e.source_id, e.stream_epoch, e.event_id)
                = (a.session_id, a.source_id, a.stream_epoch, a.canonical_event_id)
            WHERE a.session_id = $session AND a.source_id = $source AND a.stream_epoch = $epoch AND a.event_id = $event;
            """);
        BindEvent(command, header);
        using SqliteDataReader row = command.ExecuteReader();
        if (!row.Read())
        {
            return null;
        }
        long version = row.GetInt64(0);
        if (!row.GetFieldValue<byte[]>(1).AsSpan().SequenceEqual(hash))
        {
            throw new TranscriptConflictException();
        }
        if (row.Read())
        {
            throw CorruptHistory();
        }
        return version;
    }

    private static SegmentRow? FindSegment(SqliteTransaction transaction, SourceIdentity source, SegmentId segment)
    {
        using SqliteCommand command = Command(transaction, """
            SELECT id, current_final_revision FROM transcript_segments
            WHERE session_id = $session AND source_id = $source AND stream_epoch = $epoch
                AND coalesce(source_segment_id, id) = $segment;
            """);
        BindSource(command, source);
        command.Parameters.AddWithValue("$segment", Bytes(segment.Value));
        using SqliteDataReader row = command.ExecuteReader();
        return row.Read() ? new SegmentRow(GuidAt(row, 0), new SegmentRevision(row.GetInt64(1))) : null;
    }

    private static CommittedTranscriptEntry? FindRevision(
        SqliteTransaction transaction, SessionId session, Guid storedId, SegmentRevision revision)
    {
        using SqliteCommand command = Command(transaction, SelectEntries +
            "\nWHERE r.segment_id = $segment AND r.revision = $revision;");
        command.Parameters.AddWithValue("$segment", Bytes(storedId));
        command.Parameters.AddWithValue("$revision", revision.Value);
        using SqliteDataReader row = command.ExecuteReader();
        return row.Read() ? ReadEntry(row, session) : null;
    }

    private static long AdvanceVersion(SqliteTransaction transaction, SessionId session)
    {
        using SqliteCommand command = Command(transaction, """
            UPDATE sessions SET transcript_version = transcript_version + 1
            WHERE id = $session AND transcript_version < 9223372036854775807
            RETURNING transcript_version;
            """);
        command.Parameters.AddWithValue("$session", Bytes(session.Value));
        return command.ExecuteScalar() is long version ? version :
            throw new EncryptedDatabaseException("A transcript version could not be assigned to the session.");
    }

    private static void InsertEvent(
        SqliteTransaction transaction, TranscriptEventHeader header, TranscriptPayload payload, long version)
    {
        using SqliteCommand command = Command(transaction, """
            INSERT INTO transcript_events
                (session_id, source_id, stream_epoch, event_id, event_sequence, commit_version, kind,
                 payload_hash, timeline_epoch_id, start_offset, end_offset, timing_certainty)
            VALUES ($session, $source, $epoch, $event, $sequence, $version, $kind,
                    $hash, $timeline, $start, $end, $certainty);
            """);
        BindEvent(command, header);
        command.Parameters.AddWithValue("$sequence", header.Sequence.Value);
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$kind", payload.Kind);
        command.Parameters.AddWithValue("$hash", payload.Hash);
        command.Parameters.AddWithValue("$timeline", Bytes(payload.Timeline.Value));
        command.Parameters.AddWithValue("$start", payload.StartOffset);
        command.Parameters.AddWithValue("$end", payload.EndOffset);
        command.Parameters.AddWithValue("$certainty", TranscriptPayload.CertaintyName(payload.Certainty));
        command.ExecuteNonQuery();
    }

    private static Guid InsertSegment(
        SqliteTransaction transaction, SourceIdentity source, TranscriptPayload payload, long version)
    {
        Guid storageId = Guid.NewGuid();
        using SqliteCommand command = Command(transaction, """
            INSERT INTO transcript_segments
                (id, session_id, source_id, stream_epoch, ordinal, current_final_revision, source_segment_id)
            VALUES ($id, $session, $source, $epoch, $ordinal, $revision, $segment);
            """);
        BindSource(command, source);
        command.Parameters.AddWithValue("$id", Bytes(storageId));
        command.Parameters.AddWithValue("$segment", Bytes(payload.Segment!.Value.Value));
        command.Parameters.AddWithValue("$ordinal", version);
        command.Parameters.AddWithValue("$revision", payload.Revision!.Value.Value);
        command.ExecuteNonQuery();
        return storageId;
    }

    private static void InsertRevision(
        SqliteTransaction transaction, TranscriptEventHeader header, TranscriptPayload payload, Guid storedId)
    {
        using SqliteCommand command = Command(transaction, """
            INSERT INTO transcript_segment_revisions
                (segment_id, revision, session_id, source_id, stream_epoch, event_id, kind,
                 timing_ranges_json, text, speaker_key, speaker_spans_json, amends_revision)
            VALUES ($segment, $revision, $session, $source, $epoch, $event, $kind,
                    $ranges, CAST($text AS TEXT), CAST($speaker AS TEXT), $spans, $amends);
            """);
        BindEvent(command, header);
        command.Parameters.AddWithValue("$segment", Bytes(storedId));
        command.Parameters.AddWithValue("$revision", payload.Revision!.Value.Value);
        command.Parameters.AddWithValue("$kind", payload.Kind);
        command.Parameters.AddWithValue("$ranges", payload.IntervalsJson!);
        // Bind the exact UTF-8 bytes even when text contains an embedded NUL.
        command.Parameters.AddWithValue("$text", Utf8.GetBytes(payload.Text!));
        command.Parameters.AddWithValue("$speaker", payload.SpeakerKey is { } key ? Utf8.GetBytes(key) : DBNull.Value);
        command.Parameters.AddWithValue("$spans", (object?)payload.SpeakerSpansJson ?? DBNull.Value);
        command.Parameters.AddWithValue("$amends", (object?)payload.Amends?.Value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }

    private static void InsertGap(SqliteTransaction transaction, TranscriptEventHeader header, GapReason reason)
    {
        using SqliteCommand command = Command(transaction, """
            INSERT INTO transcript_gaps (session_id, source_id, stream_epoch, event_id, reason)
            VALUES ($session, $source, $epoch, $event, $reason);
            """);
        BindEvent(command, header);
        command.Parameters.AddWithValue("$reason", GapReasonName(reason));
        command.ExecuteNonQuery();
    }

    private static void InsertAlias(
        SqliteTransaction transaction, TranscriptEventHeader header, EventId canonical, byte[] hash)
    {
        using SqliteCommand command = Command(transaction, """
            INSERT INTO transcript_event_aliases
                (session_id, source_id, stream_epoch, event_id, canonical_event_id, payload_hash)
            VALUES ($session, $source, $epoch, $event, $canonical, $hash);
            """);
        BindEvent(command, header);
        command.Parameters.AddWithValue("$canonical", Bytes(canonical.Value));
        command.Parameters.AddWithValue("$hash", hash);
        command.ExecuteNonQuery();
    }

    private static CommittedTranscriptEntry ReadEntry(SqliteDataReader row, SessionId session)
    {
        var source = new SourceIdentity(session, new SourceId(GuidAt(row, 0)), new StreamEpoch(row.GetInt64(1)));
        var header = new TranscriptEventHeader(ContractVersion.Current, source,
            new EventId(GuidAt(row, 2)), new EventSequence(row.GetInt64(3)));
        TranscriptWrite write = TranscriptPayload.Decode(header, row.GetString(5),
            new TimelineEpochId(GuidAt(row, 6)), row.GetInt64(7), OptionalInteger(row, 8),
            TranscriptPayload.ParseCertainty(row.GetString(9)),
            row.IsDBNull(10) ? null : new SegmentId(GuidAt(row, 10)),
            OptionalRevision(row, 11), OptionalRevision(row, 12), OptionalString(row, 13),
            Utf8Text(row, 14), Utf8Text(row, 15), OptionalString(row, 16),
            row.IsDBNull(17) ? null : ParseGapReason(row.GetString(17)));
        return new CommittedTranscriptEntry(new TranscriptCommitVersion(row.GetInt64(4)), write);
    }

    private static GapReason ParseGapReason(string value) =>
        GapReasons.TryGetValue(value, out GapReason reason) ? reason : throw CorruptHistory();

    private static string GapReasonName(GapReason reason) => JsonNamingPolicy.SnakeCaseLower.ConvertName(reason.ToString());

    private static long? OptionalInteger(SqliteDataReader row, int index) =>
        row.IsDBNull(index) ? null : row.GetInt64(index);

    private static SegmentRevision? OptionalRevision(SqliteDataReader row, int index) =>
        OptionalInteger(row, index) is long value ? new SegmentRevision(value) : null;

    private static string? OptionalString(SqliteDataReader row, int index) =>
        row.IsDBNull(index) ? null : row.GetString(index);

    private static string? Utf8Text(SqliteDataReader row, int index) =>
        row.IsDBNull(index) ? null : Utf8.GetString(row.GetFieldValue<byte[]>(index));

    private static Guid GuidAt(SqliteDataReader row, int index) => new(row.GetFieldValue<byte[]>(index), bigEndian: true);

    private static byte[] Bytes(Guid id) => id.ToByteArray(bigEndian: true);

    private static SqliteCommand Command(SqliteTransaction transaction, string sql)
    {
        SqliteCommand command = transaction.Connection!.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        return command;
    }

    private static void BindEvent(SqliteCommand command, TranscriptEventHeader header)
    {
        BindSource(command, header.Source);
        command.Parameters.AddWithValue("$event", Bytes(header.EventId.Value));
    }

    private static void BindSource(SqliteCommand command, SourceIdentity source)
    {
        command.Parameters.AddWithValue("$session", Bytes(source.SessionId.Value));
        command.Parameters.AddWithValue("$source", Bytes(source.SourceId.Value));
        command.Parameters.AddWithValue("$epoch", source.StreamEpoch.Value);
    }

    private static EncryptedDatabaseException CorruptHistory() =>
        new("The committed transcript could not be read consistently. Recovery is required.");

    private readonly record struct SegmentRow(Guid Id, SegmentRevision CurrentRevision);
}
