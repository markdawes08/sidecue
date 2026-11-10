namespace Sidecue.Infrastructure.Security;

/// <summary>Locations for user data, separate from the installed application.</summary>
public sealed class SidecueDataPaths
{
    private SidecueDataPaths(string rootDirectory)
    {
        RootDirectory = rootDirectory;
        DatabasePath = Path.Combine(rootDirectory, "sidecue.db");
        KeyPath = Path.Combine(rootDirectory, "sidecue.key");
    }

    public string RootDirectory { get; }
    public string DatabasePath { get; }
    public string KeyPath { get; }

    public static SidecueDataPaths ForCurrentUser()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            throw new InvalidOperationException("The current user's local application data directory is unavailable.");
        }

        return ForRoot(Path.Combine(localAppData, "SidecueData"));
    }

    /// <summary>Uses an explicit local directory, for example an isolated test directory.</summary>
    public static SidecueDataPaths ForRoot(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        if (!Path.IsPathFullyQualified(rootDirectory))
        {
            throw new ArgumentException("The data directory must be an absolute local path.", nameof(rootDirectory));
        }

        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
        string? volume = Path.GetPathRoot(root);
        if (string.IsNullOrEmpty(volume) || volume.StartsWith(@"\\", StringComparison.Ordinal)
            || string.Equals(root, Path.TrimEndingDirectorySeparator(volume), StringComparison.OrdinalIgnoreCase)
            || new DriveInfo(volume).DriveType != DriveType.Fixed)
        {
            throw new ArgumentException("The data directory must be on a fixed local drive and cannot be its root.", nameof(rootDirectory));
        }

        string appDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppContext.BaseDirectory));
        if (IsWithin(root, appDirectory))
        {
            throw new ArgumentException("The data directory must be outside the application directory.", nameof(rootDirectory));
        }

        RejectReparsePointAncestors(root);
        return new SidecueDataPaths(root);
    }

    internal static void RejectReparsePointAncestors(string path)
    {
        // Check every existing ancestor, not just the final directory. A junction
        // in the middle of a path could otherwise redirect data into the install.
        string? current = path;
        while (current is not null)
        {
            try
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                {
                    throw new UnauthorizedAccessException("User data paths cannot contain reparse points.");
                }
            }
            catch (FileNotFoundException)
            {
                // A new directory may not exist yet; still inspect its parents.
            }
            catch (DirectoryNotFoundException)
            {
                // A new parent may not exist yet; keep walking toward the root.
            }

            current = Path.GetDirectoryName(current);
        }
    }

    private static bool IsWithin(string path, string parent) =>
        string.Equals(path, parent, StringComparison.OrdinalIgnoreCase)
        || path.StartsWith(parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
