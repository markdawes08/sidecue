using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Sidecue.Infrastructure.Persistence;

internal static class SchemaMigrator
{
    internal static void Apply(
        SqliteConnection connection, IReadOnlyList<string> migrations, string appVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appVersion);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            // Take the write lock before reading history so simultaneous startup
            // connections cannot both act on the same pre-migration version.
            using SqliteTransaction transaction = connection.BeginTransaction(deferred: false);
            using SqliteCommand command = connection.CreateCommand();
            command.Transaction = transaction;

            command.CommandText = "PRAGMA foreign_keys;";
            if (command.ExecuteScalar() is not 1L)
            {
                throw new EncryptedDatabaseException("Foreign keys must be enabled before migrating the encrypted database.");
            }

            EnsureHistoryTable(command);
            int applied = ReadAppliedVersion(command, migrations);
            for (int index = applied; index < migrations.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                command.CommandText = migrations[index];
                command.ExecuteNonQuery();
                RecordMigration(command, index + 1, migrations[index], appVersion);
            }

            cancellationToken.ThrowIfCancellationRequested();
            transaction.Commit();
        }
        catch (SqliteException)
        {
            throw new EncryptedDatabaseException("The encrypted database schema could not be migrated. Recovery is required.");
        }
    }

    private static void EnsureHistoryTable(SqliteCommand command)
    {
        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE type = 'table' AND name = 'schema_versions';";
        if (command.ExecuteScalar() is 1L)
        {
            return;
        }

        command.CommandText = "SELECT count(*) FROM sqlite_schema WHERE substr(name, 1, 7) <> 'sqlite_';";
        if (command.ExecuteScalar() is not 0L)
        {
            throw new EncryptedDatabaseException("The encrypted database does not have a recognized schema history.");
        }

        command.CommandText = """
            CREATE TABLE schema_versions (
                version INTEGER PRIMARY KEY CHECK (version > 0),
                checksum BLOB NOT NULL CHECK (length(checksum) = 32),
                applied_utc INTEGER NOT NULL,
                app_version TEXT NOT NULL
            ) STRICT;
            """;
        command.ExecuteNonQuery();
    }

    private static int ReadAppliedVersion(SqliteCommand command, IReadOnlyList<string> migrations)
    {
        command.CommandText = "SELECT version, checksum FROM schema_versions ORDER BY version;";
        using SqliteDataReader history = command.ExecuteReader();
        int applied = 0;
        while (history.Read())
        {
            if (applied == migrations.Count || history.GetValue(0) is not long version || version != applied + 1 ||
                history.GetValue(1) is not byte[] storedChecksum ||
                !storedChecksum.AsSpan().SequenceEqual(Checksum(migrations[applied])))
            {
                throw new EncryptedDatabaseException("The encrypted database schema history is unsupported or inconsistent.");
            }
            applied++;
        }
        return applied;
    }

    private static void RecordMigration(SqliteCommand command, int version, string sql, string appVersion)
    {
        command.CommandText = """
            INSERT INTO schema_versions (version, checksum, applied_utc, app_version)
            VALUES ($version, $checksum, $appliedUtc, $appVersion);
            """;
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$checksum", Checksum(sql));
        command.Parameters.AddWithValue("$appliedUtc", DateTimeOffset.UtcNow.UtcTicks);
        command.Parameters.AddWithValue("$appVersion", appVersion);
        command.ExecuteNonQuery();
        command.Parameters.Clear();
    }

    // Keep fingerprints stable across LF and CRLF checkouts.
    private static byte[] Checksum(string sql) =>
        SHA256.HashData(Encoding.UTF8.GetBytes(sql.ReplaceLineEndings("\n")));
}
