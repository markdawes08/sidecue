using System.Diagnostics;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using Microsoft.Data.Sqlite;
using Sidecue.Infrastructure.Persistence;
using Sidecue.Infrastructure.Security;

const string marker = "SidecueSyntheticStorageMarker719243";
try
{
    if (args.Length == 3)
    {
        Child(args[0], args[1], args[2]);
        return 0;
    }
    if (args.Length != 2 || !Path.IsPathFullyQualified(args[0]) || !Path.IsPathFullyQualified(args[1]))
        throw new ArgumentException("Expected absolute paths for the approved library and isolated probe directory.");

    string native = args[0];
    string root = args[1];
    string primary = Path.Combine(root, "primary");
    string secondary = Path.Combine(root, "secondary");
    var paths = SidecueDataPaths.ForRoot(primary);
    var current = SidecueDataPaths.ForCurrentUser();
    string expectedRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SidecueData");
    Require(string.Equals(current.RootDirectory, expectedRoot, StringComparison.OrdinalIgnoreCase), "The default path does not use LocalAppData.");
    Require(!Directory.Exists(root), "The isolated probe directory must not exist already.");

    // Every invocation is a new process using the same Windows user profile.
    RunChild("create", native, primary);
    RunChild("reopen", native, primary);
    RunChild("create", native, secondary);
    AssertAcl(paths.RootDirectory, directory: true);
    AssertAcl(paths.KeyPath, directory: false);
    AssertAcl(paths.DatabasePath, directory: false);

    byte[] originalKey = File.ReadAllBytes(paths.KeyPath);
    byte[] databaseBytes = File.ReadAllBytes(paths.DatabasePath);
    byte[] databaseHash = SHA256.HashData(databaseBytes);
    string secondaryKey = SidecueDataPaths.ForRoot(secondary).KeyPath;
    try
    {
        File.Move(paths.KeyPath, paths.KeyPath + ".missing");
        RunChild("reject-missing", native, primary);
        File.Move(paths.KeyPath + ".missing", paths.KeyPath);
        AssertDatabaseUnchanged(paths.DatabasePath, databaseHash);

        File.WriteAllBytes(paths.KeyPath, [1, 2, 3, 4]);
        RunChild("reject-corrupt", native, primary);
        AssertDatabaseUnchanged(paths.DatabasePath, databaseHash);

        File.Copy(secondaryKey, paths.KeyPath, overwrite: true);
        RunChild("reject-wrong", native, primary);
        AssertDatabaseUnchanged(paths.DatabasePath, databaseHash);
    }
    finally
    {
        if (File.Exists(paths.KeyPath + ".missing")) File.Move(paths.KeyPath + ".missing", paths.KeyPath);
        else File.WriteAllBytes(paths.KeyPath, originalKey);
    }
    RunChild("reopen", native, primary);

    // An interrupted initialization can leave the protected key alongside an
    // empty database reservation. It must not be accepted as an empty store.
    try
    {
        File.WriteAllBytes(paths.DatabasePath, []);
        RunChild("reject-empty", native, primary);
        Require(new FileInfo(paths.DatabasePath).Length == 0, "The interrupted database was modified.");
        Require(File.ReadAllBytes(paths.KeyPath).AsSpan().SequenceEqual(originalKey), "The protected key was replaced.");
    }
    finally
    {
        File.WriteAllBytes(paths.DatabasePath, databaseBytes);
    }
    RunChild("reopen", native, primary);

    string badNative = Path.Combine(root, "altered-native.dll");
    File.Copy(native, badNative);
    using (var file = new FileStream(badNative, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
    {
        int first = file.ReadByte();
        file.Position = 0;
        file.WriteByte((byte)(first ^ 1));
    }
    string rejected = Path.Combine(root, "rejected");
    RunChild("reject-native", badNative, rejected);
    Require(!File.Exists(SidecueDataPaths.ForRoot(rejected).DatabasePath), "A database was created after library rejection.");

    foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        // The copied DLL is not user storage and cannot contain the synthetic marker.
        if (file.Equals(badNative, StringComparison.OrdinalIgnoreCase)) continue;
        AssertNoMarker(file);
    }
    Console.WriteLine("Storage probe passed: same-user process restart, key failures, ACLs, native rejection, and encrypted artifact scan.");
    Console.WriteLine($"Synthetic artifacts: {root}");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Storage probe failed: {exception.GetType().Name}: {exception.Message}");
    return 1;
}

static void Child(string operation, string native, string directory)
{
    var paths = SidecueDataPaths.ForRoot(directory);
    var store = new EncryptedDatabaseStore(paths, native);
    switch (operation)
    {
        case "create":
            using (SqliteConnection connection = store.CreateNew())
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "CREATE TABLE probe (content TEXT NOT NULL); INSERT INTO probe(content) VALUES ($content);";
                command.Parameters.AddWithValue("$content", marker);
                command.ExecuteNonQuery();
                string wal = paths.DatabasePath + "-wal";
                Require(File.Exists(wal) && new FileInfo(wal).Length > 0, "The live WAL was not created.");
                AssertNoMarker(wal);
                // Keep the first connection and its WAL open while another process
                // opens the same store, including its existing sidecars.
                RunChild("reopen", native, directory);
            }
            break;
        case "reopen":
            using (SqliteConnection connection = store.OpenExisting())
            using (SqliteCommand command = connection.CreateCommand())
            {
                command.CommandText = "SELECT content FROM probe;";
                Require(string.Equals(command.ExecuteScalar() as string, marker, StringComparison.Ordinal), "A process restart did not recover the synthetic row.");
            }
            break;
        case "reject-missing":
            try { using SqliteConnection connection = store.OpenExisting(); }
            catch (InvalidOperationException) { return; }
            throw new InvalidOperationException("A missing key was accepted.");
        case "reject-corrupt":
            try { using SqliteConnection connection = store.OpenExisting(); }
            catch (CryptographicException) { return; }
            throw new InvalidOperationException("A corrupt key was accepted.");
        case "reject-wrong":
        case "reject-empty":
            try { using SqliteConnection connection = store.OpenExisting(); }
            catch (EncryptedDatabaseException) { return; }
            throw new InvalidOperationException("An incorrect key or incomplete database was accepted.");
        case "reject-native":
            try { using SqliteConnection connection = store.CreateNew(); }
            catch (SqlCipherNativeLoadException) { return; }
            throw new InvalidOperationException("An altered native library was accepted.");
        default:
            throw new ArgumentException("Unknown probe operation.");
    }
}

static void RunChild(string operation, string native, string directory)
{
    string executable = Environment.ProcessPath ?? throw new InvalidOperationException("The probe executable is unavailable.");
    var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
    if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add(operation);
    start.ArgumentList.Add(native);
    start.ArgumentList.Add(directory);
    using Process process = Process.Start(start) ?? throw new InvalidOperationException("The probe child could not start.");
    if (!process.WaitForExit(30000))
    {
        process.Kill(entireProcessTree: true);
        throw new InvalidOperationException($"The {operation} process timed out.");
    }
    string error = process.StandardError.ReadToEnd();
    if (process.ExitCode != 0) throw new InvalidOperationException($"The {operation} process failed: {error.Trim()}");
}

static void AssertAcl(string path, bool directory)
{
    using WindowsIdentity identity = WindowsIdentity.GetCurrent();
    SecurityIdentifier user = identity.User ?? throw new InvalidOperationException("The current Windows user is unavailable.");
    FileSystemSecurity acl = directory
        ? new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access)
        : new FileInfo(path).GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
    Require(user.Equals(acl.GetOwner(typeof(SecurityIdentifier))), "A storage artifact has an unexpected owner.");
    if (directory) Require(acl.AreAccessRulesProtected, "The storage directory inherits permissions.");
    bool userAllowed = false;
    foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
    {
        if (rule.AccessControlType != AccessControlType.Allow) continue;
        Require(user.Equals(rule.IdentityReference), "A storage artifact grants access to another identity.");
        userAllowed = true;
    }
    Require(userAllowed, "The current user has no allowed access to a storage artifact.");
}

static void AssertDatabaseUnchanged(string path, byte[] expected) =>
    Require(CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(path)), expected), "A rejected key altered the database.");

static void AssertNoMarker(string path)
{
    using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
    using var content = new MemoryStream();
    file.CopyTo(content);
    ReadOnlySpan<byte> bytes = content.ToArray();
    Require(bytes.IndexOf(Encoding.UTF8.GetBytes(marker)) < 0 &&
        bytes.IndexOf(Encoding.Unicode.GetBytes(marker)) < 0 &&
        bytes.IndexOf(Encoding.BigEndianUnicode.GetBytes(marker)) < 0,
        "A storage artifact exposed the synthetic marker.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
