using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Microsoft.Data.Sqlite;
using Sidecue.Infrastructure.Persistence;
using Sidecue.Infrastructure.Security;

// Fixed synthetic fixtures only. No keys or marker values are passed as arguments.
const string baseline = "SidecueSyntheticBaselineMarker291487";
const string pending = "SidecueSyntheticWriteMarker827451";
const int insertedRows = 128;

try
{
    if (args.Length == 4 && args[0] == "child")
    {
        if (args[3] is "transcript-before" or "transcript-after")
            TranscriptDurability.RunChild(args[1], args[2], args[3], PauseUntilKilled);
        else
            RunInterruptedChild(args[1], args[2], args[3]);
        return 0;
    }
    bool transcript = args.Length == 3 && args[0] == "--transcript";
    int pathIndex = transcript ? 1 : 0;
    if (args.Length != pathIndex + 2 || !Path.IsPathFullyQualified(args[pathIndex]) ||
        !Path.IsPathFullyQualified(args[pathIndex + 1]))
        throw new ArgumentException("Expected absolute paths for the approved library and isolated probe directory.");

    string native = args[pathIndex];
    string root = args[pathIndex + 1];
    Require(!Directory.Exists(root), "The isolated probe directory must not exist already.");

    string[] stages = transcript ? ["transcript-before", "transcript-after"] :
        ["write-before", "write-after", "migration-before", "migration-after"];
    IReadOnlyList<string> canaries = transcript ? TranscriptDurability.Canaries : [baseline, pending];
    foreach (string stage in stages)
    {
        var paths = SidecueDataPaths.ForRoot(Path.Combine(root, stage));
        var store = new EncryptedDatabaseStore(paths, native);
        if (transcript) TranscriptDurability.CreateBaseline(store);
        else CreateBaseline(store);

        long liveWalBytes = InterruptAtBoundary(native, paths, stage, canaries);
        if (transcript) TranscriptDurability.VerifyRecovered(store, stage);
        else VerifyRecovered(store, stage);
        ScanStoreArtifacts(paths, canaries);

        string verified = transcript ? "revision history, gap, commit versions, retry and integrity" :
            "committed data, schema, FTS and integrity";
        Console.WriteLine($"PASS {stage}: terminated at synchronized boundary; live WAL bytes={liveWalBytes}; {verified} verified.");
    }

    Console.WriteLine("Durability probe passed using separate processes and synthetic data.");
    Console.WriteLine($"Synthetic artifacts: {root}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Durability probe failed: {exception.GetType().Name}: {exception.Message}");
    return 1;
}

static void CreateBaseline(EncryptedDatabaseStore store)
{
    using SqliteConnection connection = store.CreateNew();
    using SqliteTransaction transaction = connection.BeginTransaction();
    Execute(connection, transaction, "CREATE TABLE records (id INTEGER PRIMARY KEY, content TEXT NOT NULL); " +
        "CREATE VIRTUAL TABLE records_fts USING fts5(content); PRAGMA user_version=1;");
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = "INSERT INTO records(id, content) VALUES (1, $content); " +
        "INSERT INTO records_fts(rowid, content) VALUES (1, $content);";
    command.Parameters.AddWithValue("$content", baseline);
    command.ExecuteNonQuery();
    transaction.Commit();
}

static void RunInterruptedChild(string native, string directory, string stage)
{
    Require(stage is "write-before" or "write-after" or "migration-before" or "migration-after", "Unknown interruption boundary.");
    var store = new EncryptedDatabaseStore(SidecueDataPaths.ForRoot(directory), native);
    using SqliteConnection connection = store.OpenExisting();
    Execute(connection, null, "PRAGMA wal_autocheckpoint=0; PRAGMA cache_size=16; PRAGMA cache_spill=ON;");
    using SqliteTransaction transaction = connection.BeginTransaction();
    if (stage.StartsWith("write-", StringComparison.Ordinal))
    {
        string content = pending + " " + new string('z', 512);
        using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO records(id, content) VALUES ($id, $content); " +
            "INSERT INTO records_fts(rowid, content) VALUES ($id, $content);";
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        command.Parameters.AddWithValue("$content", content);
        for (int i = 0; i < insertedRows; i++)
        {
            id.Value = i + 2;
            command.ExecuteNonQuery();
        }
    }
    else
    {
        // The version, DDL and data transformation are one synthetic migration.
        Execute(connection, transaction, "ALTER TABLE records ADD COLUMN label TEXT; " +
            "UPDATE records SET label='synthetic'; CREATE INDEX records_label ON records(label); PRAGMA user_version=2;");
    }

    if (stage.EndsWith("after", StringComparison.Ordinal)) transaction.Commit();
    PauseUntilKilled();
}

static void PauseUntilKilled()
{
    Console.WriteLine("READY");
    Console.Out.Flush();
    // Bound the lifetime even if the coordinating process is interrupted.
    Thread.Sleep(TimeSpan.FromSeconds(35));
    throw new InvalidOperationException("The interruption coordinator did not terminate its child.");
}

static long InterruptAtBoundary(string native, SidecueDataPaths paths, string stage, IReadOnlyList<string> canaries)
{
    string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The probe executable is unavailable.");
    var start = new ProcessStartInfo(executable)
    {
        UseShellExecute = false,
        CreateNoWindow = true,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
    };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("child");
    start.ArgumentList.Add(native);
    start.ArgumentList.Add(paths.RootDirectory);
    start.ArgumentList.Add(stage);
    using Process process = Process.Start(start) ?? throw new InvalidOperationException("The probe child could not start.");
    try
    {
        string? signal = process.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        if (signal != "READY")
        {
            string error = process.HasExited ? process.StandardError.ReadToEnd().Trim() : "Child did not signal the requested boundary.";
            throw new InvalidOperationException($"The {stage} child did not reach its boundary: {error}");
        }
        Require(!process.HasExited, "The child exited before interruption.");
        string wal = paths.DatabasePath + "-wal";
        long liveWalBytes = File.Exists(wal) ? new FileInfo(wal).Length : 0;
        // Both transcript cases exercise the live WAL, including pre-commit spill.
        if (stage == "transcript-before" || stage.EndsWith("after", StringComparison.Ordinal))
            Require(liveWalBytes > 0, "A nonempty live WAL was not observed at the interruption boundary.");
        ScanStoreArtifacts(paths, canaries, childHoldsDatabase: true);
        process.Kill(entireProcessTree: true);
        Require(process.WaitForExit(30000), "The child did not terminate.");
        Require(process.ExitCode != 0, "The interrupted child exited normally.");
        // Scan the WAL index after Windows releases the child's byte-range locks,
        // before reopening can remove or regenerate it.
        string sharedMemory = paths.DatabasePath + "-shm";
        if (File.Exists(sharedMemory)) AssertEncrypted(sharedMemory, canaries);
        return liveWalBytes;
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            Require(process.WaitForExit(5000), "The probe child did not terminate during cleanup.");
        }
    }
}

static void VerifyRecovered(EncryptedDatabaseStore store, string stage)
{
    using SqliteConnection connection = store.OpenExisting();
    bool writeCommitted = stage == "write-after";
    bool migrationCommitted = stage == "migration-after";
    Require(Scalar(connection, "PRAGMA cipher_status;") == "1", "Encryption is not active after reopening.");
    Require(Scalar(connection, "PRAGMA journal_mode;") == "wal", "WAL mode is not active after reopening.");
    Require(Scalar(connection, "PRAGMA integrity_check;") == "ok", "SQLite integrity check failed.");
    using (SqliteCommand integrity = connection.CreateCommand())
    {
        integrity.CommandText = "PRAGMA cipher_integrity_check;";
        using SqliteDataReader reader = integrity.ExecuteReader();
        Require(!reader.Read(), "SQLCipher integrity check reported an error.");
    }
    Require(Scalar(connection, "SELECT count(*) FROM records WHERE id=1 AND content=$content;", baseline) == "1", "Committed baseline row is missing.");
    Require(Scalar(connection, "SELECT count(*) FROM records;") == (writeCommitted ? (insertedRows + 1).ToString(CultureInfo.InvariantCulture) : "1"), "The recovered row count differs from the commit boundary.");
    Require(Scalar(connection, "SELECT count(*) FROM records_fts WHERE records_fts MATCH $content;", baseline) == "1", "The baseline FTS row is missing.");
    Require(Scalar(connection, "SELECT count(*) FROM records_fts WHERE records_fts MATCH $content;", pending) == (writeCommitted ? insertedRows.ToString(CultureInfo.InvariantCulture) : "0"), "The FTS index differs from the commit boundary.");
    Require(Scalar(connection, "PRAGMA user_version;") == (migrationCommitted ? "2" : "1"), "The migration version differs from the commit boundary.");
    Require(Scalar(connection, "SELECT count(*) FROM pragma_table_info('records') WHERE name='label';") == (migrationCommitted ? "1" : "0"), "The recovered schema differs from the commit boundary.");
    Require(Scalar(connection, "SELECT count(*) FROM sqlite_master WHERE type='index' AND name='records_label';") == (migrationCommitted ? "1" : "0"), "The recovered migration index differs from the commit boundary.");
    if (migrationCommitted)
        Require(Scalar(connection, "SELECT count(*) FROM records WHERE id=1 AND label='synthetic';") == "1", "The committed migration data is missing.");
}

static void Execute(SqliteConnection connection, SqliteTransaction? transaction, string sql)
{
    using SqliteCommand command = connection.CreateCommand();
    command.Transaction = transaction;
    command.CommandText = sql;
    command.ExecuteNonQuery();
}

static string Scalar(SqliteConnection connection, string sql, string? content = null)
{
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = sql;
    if (content is not null) command.Parameters.AddWithValue("$content", content);
    return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? string.Empty;
}

static void ScanStoreArtifacts(SidecueDataPaths paths, IReadOnlyList<string> canaries, bool childHoldsDatabase = false)
{
    Require(File.Exists(paths.DatabasePath), "The probe database is missing.");
    foreach (string artifact in Directory.EnumerateFileSystemEntries(paths.RootDirectory))
    {
        Require(artifact == paths.DatabasePath || artifact == paths.KeyPath ||
            artifact == paths.DatabasePath + "-wal" || artifact == paths.DatabasePath + "-shm",
            "The probe created an unexpected storage artifact.");
        if (!childHoldsDatabase || artifact != paths.DatabasePath + "-shm")
            AssertEncrypted(artifact, canaries);
    }
}

static void AssertEncrypted(string path, IReadOnlyList<string> canaries)
{
    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var content = new MemoryStream();
    file.CopyTo(content);
    ReadOnlySpan<byte> bytes = content.ToArray();
    foreach (string marker in canaries)
    {
        Require(bytes.IndexOf(Encoding.UTF8.GetBytes(marker)) < 0 &&
            bytes.IndexOf(Encoding.Unicode.GetBytes(marker)) < 0 &&
            bytes.IndexOf(Encoding.BigEndianUnicode.GetBytes(marker)) < 0,
            "A storage artifact exposed a synthetic marker.");
    }
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
