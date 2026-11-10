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
        RunInterruptedChild(args[1], args[2], args[3]);
        return 0;
    }
    if (args.Length != 2 || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]))
        throw new ArgumentException("Expected absolute paths for the approved library and isolated probe directory.");

    string native = args[0];
    string root = args[1];
    Require(!Directory.Exists(root), "The isolated probe directory must not exist already.");

    foreach (string stage in new[] { "write-before", "write-after", "migration-before", "migration-after" })
    {
        var paths = SidecueDataPaths.ForRoot(Path.Combine(root, stage));
        var store = new EncryptedDatabaseStore(paths, native);
        CreateBaseline(store);
        InterruptAtBoundary(native, paths, stage);
        VerifyRecovered(store, stage);
        foreach (string artifact in Directory.EnumerateFiles(paths.RootDirectory)) AssertEncrypted(artifact);
        Console.WriteLine($"PASS {stage}: terminated at synchronized boundary; committed data, schema, FTS and integrity verified.");
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
    Console.WriteLine("READY");
    Console.Out.Flush();
    Thread.Sleep(Timeout.Infinite);
}

static void InterruptAtBoundary(string native, SidecueDataPaths paths, string stage)
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
        AssertEncrypted(paths.DatabasePath);
        string wal = paths.DatabasePath + "-wal";
        // After-commit cases must retain a nonempty WAL at the instant of termination.
        if (stage.EndsWith("after", StringComparison.Ordinal))
            Require(File.Exists(wal) && new FileInfo(wal).Length > 0, "A committed live WAL was not observed.");
        if (File.Exists(wal)) AssertEncrypted(wal);
        process.Kill(entireProcessTree: true);
        Require(process.WaitForExit(30000), "The child did not terminate.");
        Require(process.ExitCode != 0, "The interrupted child exited normally.");
    }
    finally
    {
        if (!process.HasExited)
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
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

static void AssertEncrypted(string path)
{
    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var content = new MemoryStream();
    file.CopyTo(content);
    ReadOnlySpan<byte> bytes = content.ToArray();
    foreach (string marker in new[] { baseline, pending })
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
