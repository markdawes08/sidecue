using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Diagnostics;
using System.Reflection;
using Microsoft.Data.Sqlite;
using SQLitePCL;

if (args.Length == 2 && args[0] == "--plaintext-reader")
{
    try
    {
        var result = PlainSqliteReader.ReadSchema(args[1]);
        if (result == 26) { Console.WriteLine("Ordinary Windows SQLite returned SQLITE_NOTADB."); return 0; }
        Console.Error.WriteLine($"Ordinary Windows SQLite returned code {result}, expected SQLITE_NOTADB.");
        return 1;
    }
    catch (Exception exception) { Console.Error.WriteLine($"Ordinary SQLite reader failed: {exception.GetType().Name}."); return 1; }
}

if (args.Length != 2 || !Path.IsPathFullyQualified(args[0]) || !File.Exists(args[0]) ||
    !Path.IsPathFullyQualified(args[1]) || !File.Exists(args[1]))
{
    Console.Error.WriteLine("Provide absolute paths to the native SQLCipher DLL and pinned build manifest.");
    return 2;
}

var nativePath = Path.GetFullPath(args[0]);
BuildManifest manifest;
try
{
    manifest = JsonSerializer.Deserialize<BuildManifest>(File.ReadAllText(args[1])) ?? throw new InvalidOperationException();
    if (string.IsNullOrWhiteSpace(manifest.NativeSha256) || string.IsNullOrWhiteSpace(manifest.CipherVersion) ||
        string.IsNullOrWhiteSpace(manifest.SqliteVersion) || string.IsNullOrWhiteSpace(manifest.SourceId) ||
        string.IsNullOrWhiteSpace(manifest.ProviderVersion)) { throw new InvalidOperationException(); }
    if (!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(nativePath))), manifest.NativeSha256, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException();
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Manifest or pre-load digest validation failed: {exception.GetType().Name}.");
    return 1;
}
var canaryDirectory = Path.Combine(AppContext.BaseDirectory, "canaries", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(canaryDirectory);
var privateTemp = Path.Combine(canaryDirectory, "temp");
Directory.CreateDirectory(privateTemp);
Environment.SetEnvironmentVariable("TEMP", privateTemp);
Environment.SetEnvironmentVariable("TMP", privateTemp);
NativeLibraryAdapter library;
string nativeHash;
try
{
    library = new NativeLibraryAdapter(nativePath);
    var actualPath = Path.GetFullPath(library.LoadedPath);
    if (!string.Equals(nativePath, actualPath, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("The loaded native path differs from the requested path.");
    nativeHash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(actualPath))).ToLowerInvariant();
    if (!string.Equals(nativeHash, manifest.NativeSha256, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Loaded native digest differs from the manifest.");
    SQLite3Provider_dynamic_cdecl.Setup("sqlcipher", library);
    raw.SetProvider(new SQLite3Provider_dynamic_cdecl());
    raw.FreezeProvider();
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Native provider initialization failed: {exception.GetType().Name}.");
    return 1;
}

const string secret = "synthetic-canary-key-not-production-8630";
const string wrongSecret = "wrong-synthetic-key-not-production-8429";
const string marker = "sidecue SyntheticDatabaseCanaryWombat843912";
const string tempMarker = "sidecue SyntheticTempCanaryNarwhal473621";
const string markerToken = "syntheticdatabasecanarywombat843912";
const string tempMarkerToken = "synthetictempcanarynarwhal473621";
var dbPath = Path.Combine(canaryDirectory, "probe.db");
var liveFiles = new List<string>();
var stage = "open";
try
{
    using (var connection = Connect(dbPath, secret, SqliteOpenMode.ReadWriteCreate))
    {
        stage = "cipher and version validation";
        var cipherVersion = Scalar(connection, "PRAGMA cipher_version;");
        var cipherStatus = Scalar(connection, "PRAGMA cipher_status;");
        var cryptoProvider = Scalar(connection, "PRAGMA cipher_provider;");
        var cryptoVersion = Scalar(connection, "PRAGMA cipher_provider_version;");
        var sqliteVersion = Scalar(connection, "SELECT sqlite_version();");
        var sqliteSource = Scalar(connection, "SELECT sqlite_source_id();");
        var tempCompiled = Scalar(connection, "SELECT sqlite_compileoption_used('TEMP_STORE=3');");
        var ftsCompiled = Scalar(connection, "SELECT sqlite_compileoption_used('ENABLE_FTS5');");
        if (string.IsNullOrEmpty(cipherVersion) || cipherStatus != "1" || string.IsNullOrEmpty(cryptoProvider) || tempCompiled != "1" || ftsCompiled != "1")
            throw new InvalidOperationException("Native cipher, FTS5, or forced memory temp capability was unavailable.");
        if (!string.Equals(cipherVersion, manifest.CipherVersion, StringComparison.Ordinal) ||
            !string.Equals(sqliteVersion, manifest.SqliteVersion, StringComparison.Ordinal) ||
            !string.Equals(sqliteSource, manifest.SourceId, StringComparison.Ordinal) ||
            !string.Equals(cryptoVersion, manifest.ProviderVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("Native version or source differs from the manifest.");
        if (!Version.TryParse(sqliteVersion, out var sqliteRuntime) || sqliteRuntime < new Version(3, 51, 3))
            throw new InvalidOperationException("The loaded SQLite version predates the required WAL-reset fix.");
        stage = "WAL and FTS";
        if (Scalar(connection, "PRAGMA journal_mode=WAL;") != "wal")
            throw new InvalidOperationException("WAL mode was not activated.");
        Execute(connection, "PRAGMA wal_autocheckpoint=0;");
        Execute(connection, "CREATE TABLE note (id INTEGER PRIMARY KEY, content TEXT NOT NULL); CREATE VIRTUAL TABLE note_index USING fts5(content);");
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO note(content) VALUES ($content); INSERT INTO note_index(content) VALUES ($content);";
            command.Parameters.AddWithValue("$content", marker);
            command.ExecuteNonQuery();
        }
        if (Scalar(connection, "SELECT count(*) FROM note_index WHERE note_index MATCH 'SyntheticDatabaseCanaryWombat843912';") != "1")
            throw new InvalidOperationException("FTS query did not return the canary.");
        stage = "temporary storage";
        Execute(connection, "PRAGMA temp_store=FILE; CREATE TEMP TABLE scratch(content TEXT NOT NULL);");
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "INSERT INTO scratch(content) VALUES ($content); SELECT content FROM scratch ORDER BY content;";
            command.Parameters.AddWithValue("$content", tempMarker);
            command.ExecuteNonQuery();
        }
        var tempPath = Scalar(connection, "SELECT file FROM pragma_database_list WHERE name='temp';");
        if (tempPath != "") throw new InvalidOperationException("Temp database was not memory backed.");
        var wal = dbPath + "-wal";
        var shm = dbPath + "-shm";
        if (!File.Exists(wal) || new FileInfo(wal).Length == 0 || !File.Exists(shm))
            throw new InvalidOperationException("Live WAL and shared memory files were not observed.");
        stage = "live artifact scan";
        liveFiles.AddRange(Directory.GetFiles(canaryDirectory, "*", SearchOption.AllDirectories));
        Scan(liveFiles, marker, tempMarker, markerToken, tempMarkerToken);
        stage = "wrong and absent keys";
        AssertUnreadable(dbPath, wrongSecret, "wrong-key");
        AssertUnreadable(dbPath, null, "no-key");
        Console.WriteLine($"Native path: {library.LoadedPath}");
        Console.WriteLine($"Native SHA256: {nativeHash}");
        Console.WriteLine($"Cipher version: {cipherVersion}");
        Console.WriteLine($"Cipher status: {cipherStatus}; crypto provider: {cryptoProvider}; version: {cryptoVersion}");
        Console.WriteLine($"SQLite version: {sqliteVersion}");
        Console.WriteLine($"SQLite source ID: {sqliteSource}");
        Console.WriteLine($"FTS5 and TEMP_STORE=3: confirmed");
        Console.WriteLine($"Live files: {string.Join(", ", liveFiles.Select(Path.GetFileName))}; WAL bytes: {new FileInfo(wal).Length}");
    }
    stage = "correct-key reopen";
    using (var reopened = Connect(dbPath, secret, SqliteOpenMode.ReadWrite))
    {
        if (Scalar(reopened, "SELECT count(*) FROM note;") != "1" ||
            Scalar(reopened, "SELECT count(*) FROM note_index WHERE note_index MATCH 'SyntheticDatabaseCanaryWombat843912';") != "1")
            throw new InvalidOperationException("Correct-key reopen failed to recover rows or FTS.");
    }
    stage = "closed artifact scan";
    var closedFiles = Directory.GetFiles(canaryDirectory, "*", SearchOption.AllDirectories);
    Scan(closedFiles, marker, tempMarker, markerToken, tempMarkerToken);
    stage = "ordinary SQLite rejection";
    AssertPlainSqliteRejects(dbPath);
    Console.WriteLine($"Wrong/no-key rejected; correct-key reopen passed; UTF-8/UTF-16 marker scan clean over {liveFiles.Count} live and {closedFiles.Length} closed files.");
    Console.WriteLine($"Synthetic artifact directory: {canaryDirectory}");
    GC.KeepAlive(library);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Canary failed during {stage}: {exception.GetType().Name}.");
    return 1;
}

static SqliteConnection Connect(string path, string? key, SqliteOpenMode mode)
{
    var builder = new SqliteConnectionStringBuilder { DataSource = path, Mode = mode, Pooling = false, Cache = SqliteCacheMode.Private };
    if (key is not null) builder.Password = key;
    var result = new SqliteConnection(builder.ToString());
    try { result.Open(); return result; }
    catch { result.Dispose(); throw; }
}
static void Execute(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
static string Scalar(SqliteConnection connection, string sql) { using var command = connection.CreateCommand(); command.CommandText = sql; return Convert.ToString(command.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) ?? ""; }
static void AssertUnreadable(string path, string? key, string label)
{
    try
    {
        using var connection = Connect(path, key, SqliteOpenMode.ReadWrite);
        _ = Scalar(connection, "SELECT count(*) FROM sqlite_master;");
    }
    catch (SqliteException exception) when (exception.SqliteErrorCode == 26) { return; }
    throw new InvalidOperationException($"{label} access was not rejected as encrypted/not-a-database.");
}
static void AssertPlainSqliteRejects(string path)
{
    var executable = Environment.ProcessPath ?? throw new InvalidOperationException();
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add("--plaintext-reader");
    start.ArgumentList.Add(path);
    using var child = Process.Start(start) ?? throw new InvalidOperationException();
    if (!child.WaitForExit(15000)) { child.Kill(); throw new InvalidOperationException(); }
    var stdout = child.StandardOutput.ReadToEnd();
    if (child.ExitCode != 0) { throw new InvalidOperationException(); }
    Console.Write(stdout);
}
static void Scan(IEnumerable<string> files, params string[] markers)
{
    foreach (var file in files)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray().AsSpan();
        foreach (var marker in markers)
        {
            if (bytes.IndexOf(Encoding.UTF8.GetBytes(marker)) >= 0 || bytes.IndexOf(Encoding.Unicode.GetBytes(marker)) >= 0 || bytes.IndexOf(Encoding.BigEndianUnicode.GetBytes(marker)) >= 0)
                throw new InvalidOperationException($"Plaintext synthetic marker detected in {Path.GetFileName(file)}.");
        }
    }
}
sealed class NativeLibraryAdapter : IGetFunctionPointer
{
    private readonly nint _handle;
    public NativeLibraryAdapter(string path) => _handle = System.Runtime.InteropServices.NativeLibrary.Load(path);
    public nint GetFunctionPointer(string name) => System.Runtime.InteropServices.NativeLibrary.TryGetExport(_handle, name, out var address) ? address : 0;
    public string LoadedPath
    {
        get
        {
            var buffer = new char[32768];
            var length = GetModuleFileName(_handle, buffer, (uint)buffer.Length);
            if (length == 0 || length >= buffer.Length) { throw new InvalidOperationException("Unable to identify loaded native module."); }
            return new string(buffer, 0, (int)length);
        }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileName(nint module, [Out] char[] fileName, uint size);
}
sealed record BuildManifest(string NativeSha256, string CipherVersion, string SqliteVersion, string SourceId, string ProviderVersion);
static class PlainSqliteReader
{
    internal static int ReadSchema(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) { throw new ArgumentException("An existing absolute database path is required.", nameof(path)); }
        var result = sqlite3_open_v2(Encoding.UTF8.GetBytes(path + '\0'), out var db, 1, 0);
        if (result != 0) { if (db != 0) { _ = sqlite3_close(db); } return result; }
        try
        {
            result = sqlite3_prepare_v2(db, Encoding.UTF8.GetBytes("SELECT count(*) FROM sqlite_master;\0"), -1, out var statement, 0);
            if (result != 0) { if (statement != 0) { _ = sqlite3_finalize(statement); } return result; }
            try { return sqlite3_step(statement); }
            finally { _ = sqlite3_finalize(statement); }
        }
        finally { _ = sqlite3_close(db); }
    }

    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_open_v2(byte[] filename, out nint db, int flags, nint vfs);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_prepare_v2(nint db, byte[] sql, int bytes, out nint statement, nint tail);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_step(nint statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_finalize(nint statement);
    [DllImport("winsqlite3.dll", CallingConvention = CallingConvention.Cdecl, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int sqlite3_close(nint db);
}
