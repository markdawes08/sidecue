using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace Sidecue.Infrastructure.Security;

/// <summary>Stores a random database key protected for the current Windows user.</summary>
public sealed class UserProtectedDatabaseKeyStore
{
    private const int KeySize = 32;
    private const int MaximumProtectedKeySize = 16 * 1024;
    private readonly SidecueDataPaths paths;
    private string TemporaryKeyPath => paths.KeyPath + ".creating";

    public UserProtectedDatabaseKeyStore(SidecueDataPaths paths) =>
        this.paths = paths ?? throw new ArgumentNullException(nameof(paths));

    /// <summary>
    /// Initializes only an empty store. An interrupted initialization is left intact
    /// and must be resolved explicitly rather than silently generating another key.
    /// </summary>
    public DatabaseKey CreateNew()
    {
        EnsureSecureDirectory();
        if (AnyDatabaseArtifactExists() || Path.Exists(paths.KeyPath) || Path.Exists(TemporaryKeyPath))
        {
            throw new InvalidOperationException("Existing storage artifacts require recovery; a new key was not created.");
        }

        // CreateNew is the interprocess initialization guard. A crash leaves this
        // artifact behind, so a later attempt cannot mistake the store for empty.
        using FileStream stream = CreateRestrictedFile(TemporaryKeyPath);
        if (AnyDatabaseArtifactExists() || Path.Exists(paths.KeyPath))
        {
            // This process created an empty guard after another initializer had
            // already published storage. Remove only our own unused guard.
            stream.Dispose();
            File.Delete(TemporaryKeyPath);
            throw new InvalidOperationException("Existing storage artifacts require recovery; a new key was not created.");
        }

        byte[] key = RandomNumberGenerator.GetBytes(KeySize);
        try
        {
            byte[] protectedKey = ProtectedData.Protect(key, optionalEntropy: null, DataProtectionScope.CurrentUser);
            stream.Write(protectedKey);
            stream.Flush(flushToDisk: true);
            stream.Dispose();
            EnsureSecureFile(TemporaryKeyPath);
            File.Move(TemporaryKeyPath, paths.KeyPath, overwrite: false);
            EnsureSecureFile(paths.KeyPath);
            return new DatabaseKey(key);
        }
        catch
        {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
    }

    /// <summary>Reopens an existing store without creating or replacing a key.</summary>
    public DatabaseKey OpenExisting()
    {
        EnsureSecureDirectory(createIfMissing: false);
        if (!File.Exists(paths.DatabasePath) || !File.Exists(paths.KeyPath) || Path.Exists(TemporaryKeyPath))
        {
            throw new InvalidOperationException("The database and protected key are inconsistent or missing; recovery is required.");
        }

        EnsureSecureFile(paths.DatabasePath);
        EnsureSecureFile(paths.KeyPath);
        foreach (string suffix in new[] { "-wal", "-shm", "-journal" })
        {
            string sidecar = paths.DatabasePath + suffix;
            if (Path.Exists(sidecar))
            {
                EnsureSecureFile(sidecar, allowProcessDefaultOwner: true);
            }
        }
        using var stream = new FileStream(paths.KeyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 or > MaximumProtectedKeySize)
        {
            throw new CryptographicException("The protected database key is invalid or unavailable.");
        }

        byte[] protectedKey = new byte[(int)stream.Length];
        stream.ReadExactly(protectedKey);
        byte[] key;
        try
        {
            key = ProtectedData.Unprotect(protectedKey, optionalEntropy: null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            throw new CryptographicException("The protected database key is invalid or unavailable.");
        }

        if (key.Length != KeySize)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("The protected database key is invalid or unavailable.");
        }

        return new DatabaseKey(key);
    }

    private bool AnyDatabaseArtifactExists() =>
        Path.Exists(paths.DatabasePath)
        || Path.Exists(paths.DatabasePath + "-wal")
        || Path.Exists(paths.DatabasePath + "-shm")
        || Path.Exists(paths.DatabasePath + "-journal");

    internal static FileStream CreateRestrictedFile(string path)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new InvalidOperationException("The current Windows user is unavailable.");
        var security = new FileSecurity();
        security.SetOwner(user);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl,
            FileShare.None, 4096, FileOptions.WriteThrough, security);
    }

    private void EnsureSecureDirectory(bool createIfMissing = true)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new InvalidOperationException("The current Windows user is unavailable.");
        var directory = new DirectoryInfo(paths.RootDirectory);
        SidecueDataPaths.RejectReparsePointAncestors(paths.RootDirectory);
        if (createIfMissing)
        {
            var security = new DirectorySecurity();
            security.SetOwner(user);
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            directory.Create(security);
        }
        else if (!directory.Exists)
        {
            throw new InvalidOperationException("The user data directory is missing; recovery is required.");
        }

        directory.Refresh();
        SidecueDataPaths.RejectReparsePointAncestors(paths.RootDirectory);
        RejectReparsePoint(directory);
        DirectorySecurity actual = directory.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        if (!actual.AreAccessRulesProtected || !user.Equals(actual.GetOwner(typeof(SecurityIdentifier))))
        {
            throw new UnauthorizedAccessException("The user data directory does not have the required owner and protected permissions.");
        }

        EnsureUserOnly(actual, user);
        bool childFilesAreProtected = actual.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>()
            .Any(rule => rule.AccessControlType == AccessControlType.Allow
                && user.Equals(rule.IdentityReference)
                && (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl
                && (rule.InheritanceFlags & InheritanceFlags.ObjectInherit) != 0
                && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0);
        if (!childFilesAreProtected)
        {
            throw new UnauthorizedAccessException("The user data directory must grant inherited access to its files.");
        }
    }

    private static void EnsureSecureFile(string path, bool allowProcessDefaultOwner = false)
    {
        using WindowsIdentity identity = WindowsIdentity.GetCurrent();
        SecurityIdentifier user = identity.User ?? throw new InvalidOperationException("The current Windows user is unavailable.");
        var file = new FileInfo(path);
        RejectReparsePoint(file);
        FileSecurity security = file.GetAccessControl(AccessControlSections.Owner | AccessControlSections.Access);
        IdentityReference? owner = security.GetOwner(typeof(SecurityIdentifier));
        // SQLite creates its sidecars itself. An elevated Windows token may use
        // Administrators as its default owner, while the inherited DACL remains
        // user-only. Other owners are not acceptable.
        bool isDefaultOwner = allowProcessDefaultOwner && identity.Owner is not null && identity.Owner.Equals(owner);
        if (!user.Equals(owner) && !isDefaultOwner)
        {
            throw new UnauthorizedAccessException("A user data file has an unexpected owner.");
        }

        EnsureUserOnly(security, user);
    }

    private static void EnsureUserOnly(FileSystemSecurity security, SecurityIdentifier user)
    {
        bool userHasFullControl = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow)
            {
                continue;
            }

            if (!user.Equals(rule.IdentityReference))
            {
                throw new UnauthorizedAccessException("User data permissions grant access to another identity.");
            }

            userHasFullControl |= (rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
        }

        if (!userHasFullControl)
        {
            throw new UnauthorizedAccessException("The current user does not have the required data permissions.");
        }
    }

    private static void RejectReparsePoint(FileSystemInfo item)
    {
        if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new UnauthorizedAccessException("User data paths cannot be reparse points.");
        }
    }
}
