using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Sidecue.Infrastructure.Security;
using SQLitePCL;

namespace Sidecue.Infrastructure.Persistence;

/// <summary>Creates or opens the encrypted user store. Invoke off the UI thread.</summary>
public sealed class EncryptedDatabaseStore
{
    private readonly SidecueDataPaths paths;
    private readonly string nativeLibraryPath;

    public EncryptedDatabaseStore(SidecueDataPaths paths, string nativeLibraryPath)
    {
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));
        ArgumentException.ThrowIfNullOrWhiteSpace(nativeLibraryPath);
        this.nativeLibraryPath = nativeLibraryPath;
    }

    /// <summary>Creates a store only when no existing or interrupted store is present.</summary>
    public SqliteConnection CreateNew()
    {
        SqlCipherNativeLoader.Initialize(nativeLibraryPath);
        using DatabaseKey key = new UserProtectedDatabaseKeyStore(paths).CreateNew();

        // Reserve the filename without replacing a file created by another process.
        // If initialization fails, the remaining artifacts require explicit recovery.
        using (UserProtectedDatabaseKeyStore.CreateRestrictedFile(paths.DatabasePath)) { }
        return Open(key, SqliteOpenMode.ReadWrite, initialize: true);
    }

    /// <summary>Opens an existing store without generating or replacing a key.</summary>
    public SqliteConnection OpenExisting()
    {
        SqlCipherNativeLoader.Initialize(nativeLibraryPath);
        using DatabaseKey key = new UserProtectedDatabaseKeyStore(paths).OpenExisting();
        if (new FileInfo(paths.DatabasePath).Length == 0)
            throw new EncryptedDatabaseException("The encrypted database is incomplete; recovery is required.");
        return Open(key, SqliteOpenMode.ReadWrite, initialize: false);
    }

    private SqliteConnection Open(DatabaseKey key, SqliteOpenMode mode, bool initialize)
    {
        var options = new SqliteConnectionStringBuilder
        {
            DataSource = paths.DatabasePath,
            Mode = mode,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        };
        var connection = new SqliteConnection(options.ToString());
        try
        {
            // With no password or connection PRAGMAs, Microsoft.Data.Sqlite opens the
            // handle without reading the schema. Apply the byte key before SQL runs.
            connection.Open();
            Span<byte> rawKey = stackalloc byte[67];
            try
            {
                rawKey[0] = (byte)'x';
                rawKey[1] = (byte)'\'';
                ReadOnlySpan<byte> hex = "0123456789abcdef"u8;
                for (int i = 0; i < key.Bytes.Length; i++)
                {
                    rawKey[2 + (i * 2)] = hex[key.Bytes[i] >> 4];
                    rawKey[3 + (i * 2)] = hex[key.Bytes[i] & 15];
                }
                rawKey[^1] = (byte)'\'';
                if (raw.sqlite3_key(connection.Handle!, rawKey) != raw.SQLITE_OK)
                    throw new EncryptedDatabaseException("The encrypted database key could not be applied.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(rawKey);
            }

            if (Scalar(connection, "PRAGMA cipher_status;") != "1")
                throw new EncryptedDatabaseException("Database encryption is unavailable.");

            // Reading the schema verifies the key. Never replace a failed store.
            _ = Scalar(connection, "SELECT count(*) FROM sqlite_master;");
            string journalMode = Scalar(connection, initialize ? "PRAGMA journal_mode=WAL;" : "PRAGMA journal_mode;");
            if (journalMode != "wal")
                throw new EncryptedDatabaseException("The encrypted database is incomplete or WAL mode is unavailable.");
            Execute(connection, "PRAGMA synchronous=FULL; PRAGMA foreign_keys=ON; PRAGMA temp_store=MEMORY;");
            return connection;
        }
        catch (SqliteException)
        {
            connection.Dispose();
            throw new EncryptedDatabaseException("The encrypted database is unavailable or its key is unusable; recovery is required.");
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private static string Scalar(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>A storage failure that must not be treated as permission to start over.</summary>
public sealed class EncryptedDatabaseException(string message) : Exception(message);
