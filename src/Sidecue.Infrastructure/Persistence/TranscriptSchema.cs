using System.Collections.Immutable;
using Microsoft.Data.Sqlite;

namespace Sidecue.Infrastructure.Persistence;

internal static class TranscriptSchema
{
    // GUIDs use RFC 9562 byte order (Guid.ToByteArray(bigEndian: true)).
    // UTC columns store .NET UTC ticks. Offsets are timeline-relative microseconds.
    internal static ImmutableArray<string> Migrations { get; } =
    [
        """
        CREATE TABLE sessions (
            id BLOB NOT NULL PRIMARY KEY CHECK (length(id) = 16 AND id <> zeroblob(16)),
            title TEXT,
            state TEXT NOT NULL,
            started_utc INTEGER NOT NULL,
            ended_utc INTEGER,
            transcript_version INTEGER NOT NULL DEFAULT 0 CHECK (transcript_version >= 0),
            context_epoch INTEGER NOT NULL DEFAULT 0 CHECK (context_epoch >= 0),
            context_timeline_epoch_id BLOB,
            context_start_offset INTEGER CHECK (context_start_offset >= 0),
            completeness TEXT NOT NULL,
            profile_version_id BLOB CHECK (length(profile_version_id) = 16 AND profile_version_id <> zeroblob(16)),
            language TEXT,
            retention_policy TEXT,
            CHECK ((context_timeline_epoch_id IS NULL) = (context_start_offset IS NULL)),
            FOREIGN KEY (id, context_timeline_epoch_id) REFERENCES timeline_epochs (session_id, id)
        ) STRICT;
        CREATE INDEX sessions_by_start ON sessions (started_utc);

        CREATE TRIGGER sessions_monotonic_versions
        BEFORE UPDATE OF transcript_version, context_epoch ON sessions
        WHEN NEW.transcript_version < OLD.transcript_version OR NEW.context_epoch < OLD.context_epoch
        BEGIN
            SELECT RAISE(ABORT, 'Session versions cannot decrease.');
        END;

        CREATE TABLE timeline_epochs (
            session_id BLOB NOT NULL REFERENCES sessions (id),
            id BLOB NOT NULL CHECK (length(id) = 16 AND id <> zeroblob(16)),
            anchor_utc INTEGER NOT NULL,
            clock_kind TEXT NOT NULL,
            clock_frequency INTEGER NOT NULL CHECK (clock_frequency > 0),
            relation_to_previous TEXT,
            PRIMARY KEY (session_id, id)
        ) STRICT;

        CREATE TABLE audio_streams (
            session_id BLOB NOT NULL,
            source_id BLOB NOT NULL CHECK (length(source_id) = 16 AND source_id <> zeroblob(16)),
            stream_epoch INTEGER NOT NULL CHECK (stream_epoch >= 0),
            timeline_epoch_id BLOB NOT NULL,
            kind TEXT NOT NULL,
            device_reference TEXT,
            started_offset INTEGER NOT NULL CHECK (started_offset >= 0),
            ended_offset INTEGER CHECK (ended_offset >= started_offset),
            PRIMARY KEY (session_id, source_id, stream_epoch),
            UNIQUE (session_id, source_id, stream_epoch, timeline_epoch_id),
            FOREIGN KEY (session_id, timeline_epoch_id) REFERENCES timeline_epochs (session_id, id)
        ) STRICT;
        CREATE INDEX audio_streams_by_start ON audio_streams (session_id, timeline_epoch_id, started_offset);
        """,
        """
        CREATE TABLE transcript_events (
            session_id BLOB NOT NULL,
            source_id BLOB NOT NULL,
            stream_epoch INTEGER NOT NULL,
            event_id BLOB NOT NULL CHECK (length(event_id) = 16 AND event_id <> zeroblob(16)),
            event_sequence INTEGER NOT NULL CHECK (event_sequence >= 0),
            commit_version INTEGER NOT NULL CHECK (commit_version > 0),
            kind TEXT NOT NULL CHECK (kind IN ('final', 'amendment', 'gap')),
            payload_hash BLOB NOT NULL CHECK (length(payload_hash) = 32),
            timeline_epoch_id BLOB NOT NULL,
            start_offset INTEGER NOT NULL CHECK (start_offset >= 0),
            end_offset INTEGER CHECK (end_offset >= start_offset),
            timing_certainty TEXT NOT NULL CHECK (timing_certainty IN ('exact', 'estimated', 'unknown')),
            CHECK (kind = 'gap' OR end_offset IS NOT NULL),
            PRIMARY KEY (session_id, source_id, stream_epoch, event_id, kind),
            UNIQUE (session_id, source_id, stream_epoch, event_id),
            UNIQUE (session_id, commit_version),
            FOREIGN KEY (session_id, source_id, stream_epoch, timeline_epoch_id)
                REFERENCES audio_streams (session_id, source_id, stream_epoch, timeline_epoch_id)
        ) STRICT;
        CREATE INDEX transcript_events_by_start ON transcript_events (session_id, timeline_epoch_id, start_offset);

        CREATE TABLE transcript_segments (
            id BLOB NOT NULL PRIMARY KEY CHECK (length(id) = 16 AND id <> zeroblob(16)),
            session_id BLOB NOT NULL,
            source_id BLOB NOT NULL,
            stream_epoch INTEGER NOT NULL,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            current_final_revision INTEGER NOT NULL CHECK (current_final_revision >= 0),
            UNIQUE (session_id, source_id, stream_epoch, ordinal),
            UNIQUE (id, session_id, source_id, stream_epoch),
            FOREIGN KEY (session_id, source_id, stream_epoch)
                REFERENCES audio_streams (session_id, source_id, stream_epoch),
            FOREIGN KEY (id, current_final_revision)
                REFERENCES transcript_segment_revisions (segment_id, revision) DEFERRABLE INITIALLY DEFERRED
        ) STRICT;

        CREATE TABLE transcript_segment_revisions (
            segment_id BLOB NOT NULL,
            revision INTEGER NOT NULL CHECK (revision >= 0),
            session_id BLOB NOT NULL,
            source_id BLOB NOT NULL,
            stream_epoch INTEGER NOT NULL,
            event_id BLOB NOT NULL,
            kind TEXT NOT NULL CHECK (kind IN ('final', 'amendment')),
            timing_ranges_json TEXT NOT NULL CHECK (
                json_valid(timing_ranges_json) AND json_type(timing_ranges_json) = 'array'
                AND json_array_length(timing_ranges_json) > 0
            ),
            text TEXT NOT NULL,
            speaker_key TEXT,
            speaker_spans_json TEXT CHECK (json_valid(speaker_spans_json) AND json_type(speaker_spans_json) = 'array'
                OR speaker_spans_json IS NULL),
            amends_revision INTEGER CHECK (amends_revision < revision),
            CHECK ((kind = 'amendment') = (amends_revision IS NOT NULL)),
            PRIMARY KEY (segment_id, revision),
            UNIQUE (session_id, source_id, stream_epoch, event_id),
            FOREIGN KEY (segment_id, session_id, source_id, stream_epoch)
                REFERENCES transcript_segments (id, session_id, source_id, stream_epoch),
            FOREIGN KEY (session_id, source_id, stream_epoch, event_id, kind)
                REFERENCES transcript_events (session_id, source_id, stream_epoch, event_id, kind),
            FOREIGN KEY (segment_id, amends_revision) REFERENCES transcript_segment_revisions (segment_id, revision)
        ) STRICT;

        CREATE TABLE transcript_gaps (
            session_id BLOB NOT NULL,
            source_id BLOB NOT NULL,
            stream_epoch INTEGER NOT NULL,
            event_id BLOB NOT NULL,
            kind TEXT NOT NULL DEFAULT 'gap' CHECK (kind = 'gap'),
            reason TEXT NOT NULL,
            PRIMARY KEY (session_id, source_id, stream_epoch, event_id),
            FOREIGN KEY (session_id, source_id, stream_epoch, event_id, kind)
                REFERENCES transcript_events (session_id, source_id, stream_epoch, event_id, kind)
        ) STRICT;
        """,
        """
        ALTER TABLE transcript_segments ADD COLUMN source_segment_id BLOB
            CHECK (length(source_segment_id) = 16 AND source_segment_id <> zeroblob(16));
        CREATE UNIQUE INDEX transcript_segments_by_source_id
            ON transcript_segments (session_id, source_id, stream_epoch, coalesce(source_segment_id, id));

        CREATE TABLE transcript_event_aliases (
            session_id BLOB NOT NULL,
            source_id BLOB NOT NULL,
            stream_epoch INTEGER NOT NULL,
            event_id BLOB NOT NULL CHECK (length(event_id) = 16 AND event_id <> zeroblob(16)),
            canonical_event_id BLOB NOT NULL CHECK (canonical_event_id <> event_id),
            payload_hash BLOB NOT NULL CHECK (length(payload_hash) = 32),
            PRIMARY KEY (session_id, source_id, stream_epoch, event_id),
            FOREIGN KEY (session_id, source_id, stream_epoch, canonical_event_id)
                REFERENCES transcript_events (session_id, source_id, stream_epoch, event_id)
        ) STRICT;
        CREATE INDEX transcript_event_aliases_by_canonical
            ON transcript_event_aliases (session_id, source_id, stream_epoch, canonical_event_id);
        """
    ];

    internal static void Migrate(SqliteConnection connection, string appVersion, CancellationToken cancellationToken = default) =>
        SchemaMigrator.Apply(connection, Migrations, appVersion, cancellationToken);
}
