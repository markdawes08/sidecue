using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using SQLitePCL;

namespace Sidecue.Infrastructure.Persistence;

public sealed record SqlCipherNativeIdentity(
    string Sha256, string CipherVersion, string SqliteVersion, string SourceId, string ProviderVersion);

public sealed class SqlCipherNativeLoadException : Exception
{
    public SqlCipherNativeLoadException() : base("The approved encrypted database library could not be initialized.") { }
}

/// <summary>
/// Initializes SQLitePCL before any other SQLite consumer. Call from a background startup path.
    /// The supplied DLL and all its ancestor directories must not be reparse points.
    /// The containing directories must also be protected from untrusted modification:
    /// Windows loads by pathname, so checking ancestors cannot prevent a concurrent
    /// directory substitution between the checks and loading.
/// </summary>
public static class SqlCipherNativeLoader
{
    public const string ApprovedSha256 = "87d07385d9b299d0a2f65ec69e7d5636bf5b8b4b873bb5b2b5a65ba2848e2a92";
    private const string CipherVersion = "4.20.0 community";
    private const string SqliteVersion = "3.54.1";
    private const string SourceId = "2026-10-16 14:45:20 555e07ef33f69a0957d24d8e555b702464a131d6f1607f4c3c1e4551e8e6alt1";
    private const string ProviderVersion = "OpenSSL 3.5.9 29 Sep 2026";
    private const uint LoadLibrarySearchSystem32 = 0x00000800;
    private static readonly object Gate = new();
    private static NativeLibraryAdapter? loadedLibrary;
    private static SqlCipherNativeIdentity? identity;
    private static string? initializedPath;
    private static bool poisoned;

    public static SqlCipherNativeIdentity Initialize(string nativeLibraryPath)
    {
        lock (Gate)
        {
            try
            {
                if (poisoned || !OperatingSystem.IsWindows() || !Path.IsPathFullyQualified(nativeLibraryPath))
                    throw new InvalidOperationException();

                var path = Path.GetFullPath(nativeLibraryPath);
                // Require a local drive path. Never use the current directory, a network path, or PATH.
                var root = Path.GetPathRoot(path);
                if (root is null || root.Length != 3 || root[1] != ':')
                    throw new InvalidOperationException();
                if (identity is not null)
                {
                    if (!string.Equals(path, initializedPath, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidOperationException();
                    return identity;
                }

                if (HasExistingProvider()) throw new InvalidOperationException();

                // Keeping this handle open without write/delete sharing protects the checked file
                // from ordinary replacement or writes while Windows loads it. The caller must
                // also secure the containing directories against path substitution.
                using var file = OpenAndValidate(path);
                VerifyDigest(file);
                loadedLibrary = new NativeLibraryAdapter(path);
                poisoned = true; // A loaded module/provider setup cannot safely be retried in-process.
                if (!string.Equals(Path.GetFullPath(loadedLibrary.LoadedPath), path, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException();
                VerifyDigest(file);
                using (var loadedFile = OpenAndValidate(loadedLibrary.LoadedPath)) VerifyDigest(loadedFile);
                ValidateRuntime(loadedLibrary);

                // SQLitePCLRaw has no atomic install-if-empty operation. Application startup must
                // serialize all provider initialization and prohibit external unfreeze/replacement.
                if (HasExistingProvider()) throw new InvalidOperationException();
                SQLite3Provider_dynamic_cdecl.Setup("sqlcipher", loadedLibrary);
                raw.SetProvider(new SQLite3Provider_dynamic_cdecl());
                if (!string.Equals(raw.GetNativeLibraryName(), "sqlcipher", StringComparison.Ordinal))
                    throw new InvalidOperationException();
                raw.FreezeProvider();
                initializedPath = path;
                identity = new SqlCipherNativeIdentity(ApprovedSha256, CipherVersion, SqliteVersion, SourceId, ProviderVersion);
                poisoned = false;
                return identity;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException and not StackOverflowException)
            {
                // Paths and native error details are intentionally excluded from the public error.
                throw new SqlCipherNativeLoadException();
            }
        }
    }

    private static bool HasExistingProvider()
    {
        try { _ = raw.GetNativeLibraryName(); return true; }
        catch (Exception exception) when (exception.Message == "You need to call SQLitePCL.raw.SetProvider().  If you are using a bundle package, this is done by calling SQLitePCL.Batteries.Init().") { return false; }
    }

    private static FileStream OpenAndValidate(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException();
        for (var directory = Directory.GetParent(path); directory is not null; directory = directory.Parent)
        {
            if ((File.GetAttributes(directory.FullName) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException();
        }
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    private static void VerifyDigest(Stream file)
    {
        file.Position = 0;
        if (!string.Equals(Convert.ToHexString(SHA256.HashData(file)), ApprovedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException();
    }

    private static void ValidateRuntime(NativeLibraryAdapter library)
    {
        var open = library.GetExport<SqliteOpen>("sqlite3_open_v2");
        var key = library.GetExport<SqliteKey>("sqlite3_key");
        var close = library.GetExport<SqliteClose>("sqlite3_close");
        var prepare = library.GetExport<SqlitePrepare>("sqlite3_prepare_v2");
        var step = library.GetExport<SqliteStep>("sqlite3_step");
        var columnText = library.GetExport<SqliteColumnText>("sqlite3_column_text");
        var finalize = library.GetExport<SqliteFinalize>("sqlite3_finalize");
        nint database = 0;
        try
        {
            if (open(Encoding.UTF8.GetBytes(":memory:\0"), out database, 6, 0) != 0 || database == 0)
                throw new InvalidOperationException();
            var syntheticKey = Encoding.UTF8.GetBytes("sidecue-native-validation-key");
            if (key(database, syntheticKey, syntheticKey.Length) != 0) throw new InvalidOperationException();
            string Scalar(string sql)
            {
                nint statement = 0;
                try
                {
                    if (prepare(database, Encoding.UTF8.GetBytes(sql + '\0'), -1, out statement, 0) != 0 ||
                        statement == 0 || step(statement) != 100)
                        throw new InvalidOperationException();
                    return Marshal.PtrToStringUTF8(columnText(statement, 0)) ?? "";
                }
                finally { if (statement != 0) _ = finalize(statement); }
            }

            if (Scalar("PRAGMA cipher_version;") != CipherVersion ||
                Scalar("PRAGMA cipher_status;") != "1" ||
                Scalar("PRAGMA cipher_provider_version;") != ProviderVersion ||
                Scalar("SELECT sqlite_version();") != SqliteVersion ||
                Scalar("SELECT sqlite_source_id();") != SourceId ||
                Scalar("SELECT sqlite_compileoption_used('TEMP_STORE=3');") != "1" ||
                Scalar("SELECT sqlite_compileoption_used('ENABLE_FTS5');") != "1")
                throw new InvalidOperationException();
        }
        finally { if (database != 0) _ = close(database); }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SqliteOpen(byte[] path, out nint database, int flags, nint vfs);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SqliteClose(nint database);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SqliteKey(nint database, byte[] key, int length);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SqlitePrepare(nint database, byte[] sql, int length, out nint statement, nint tail);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SqliteStep(nint statement);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate nint SqliteColumnText(nint statement, int column);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int SqliteFinalize(nint statement);

    private sealed class NativeLibraryAdapter : IGetFunctionPointer
    {
        private readonly nint handle;

        public NativeLibraryAdapter(string path)
        {
            handle = LoadLibraryEx(path, 0, LoadLibrarySearchSystem32);
            if (handle == 0) throw new InvalidOperationException();
        }

        public nint GetFunctionPointer(string name) => System.Runtime.InteropServices.NativeLibrary.TryGetExport(handle, name, out var address) ? address : 0;

        public T GetExport<T>(string name) where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(GetFunctionPointer(name));

        public string LoadedPath
        {
            get
            {
                var buffer = new char[32768];
                var length = GetModuleFileName(handle, buffer, (uint)buffer.Length);
                if (length == 0 || length >= buffer.Length) throw new InvalidOperationException();
                return new string(buffer, 0, (int)length);
            }
        }

        // The module is deliberately never freed: SQLitePCL retains native delegates process-wide.
        [DllImport("kernel32.dll", EntryPoint = "LoadLibraryExW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern nint LoadLibraryEx(string fileName, nint reserved, uint flags);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleFileNameW", CharSet = CharSet.Unicode, SetLastError = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        private static extern uint GetModuleFileName(nint module, [Out] char[] fileName, uint size);
    }
}
