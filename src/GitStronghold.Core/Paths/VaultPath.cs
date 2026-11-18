namespace GitStronghold.Core.Paths;

public sealed class VaultException : Exception
{
    public VaultException(string message) : base(message)
    {
    }
}

/// <summary>
/// Every backup path is sanitized and checked so it cannot leave the vault folder.
/// Branch names keep a single folder even when they contain a slash.
/// </summary>
public static class VaultPath
{
    public const int MaxRelativeLength = 160;
    public const int MaxSegmentLength = 80;

    private static readonly char[] InvalidChars = ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];
    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static string RequireVaultRoot(string vaultRoot)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot))
        {
            throw new VaultException("Choose a backup folder.");
        }

        var full = Path.GetFullPath(vaultRoot);
        var root = Path.GetPathRoot(full);
        if (string.IsNullOrEmpty(root))
        {
            throw new VaultException("Choose a backup folder.");
        }

        var trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var rootTrimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (trimmed.Length == 0 || string.Equals(trimmed, rootTrimmed, StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultException("The backup folder cannot be the root of a drive.");
        }

        return full;
    }

    public static bool IsOwnerName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 39)
        {
            return false;
        }

        if (name[0] == '-' || name[^1] == '-')
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsRepoName(string? name)
    {
        if (string.IsNullOrEmpty(name) || name.Length > 100 || name.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        if (name[0] == '.' || name[^1] == '.')
        {
            return false;
        }

        foreach (var c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    public static string BranchFolder(string branchName)
    {
        if (string.IsNullOrWhiteSpace(branchName))
        {
            throw new VaultException("A branch name is missing.");
        }

        var flat = branchName.Replace('/', '\u2215').Replace('\\', '\u2215');
        return SanitizeSegment(flat);
    }

    public static string SanitizeSegment(string segment)
    {
        if (string.IsNullOrEmpty(segment) || segment is "." or "..")
        {
            throw new VaultException("A path segment is not allowed.");
        }

        if (segment.Contains("..", StringComparison.Ordinal))
        {
            throw new VaultException("A path segment is not allowed.");
        }

        var buffer = new char[segment.Length];
        for (var i = 0; i < segment.Length; i++)
        {
            var c = segment[i];
            buffer[i] = c < 32 || Array.IndexOf(InvalidChars, c) >= 0 ? '_' : c;
        }

        var name = new string(buffer).Trim().TrimEnd('.', ' ');
        if (name.Length == 0)
        {
            name = "_";
        }

        var stem = name;
        var dot = name.IndexOf('.', StringComparison.Ordinal);
        if (dot > 0)
        {
            stem = name[..dot];
        }

        if (Reserved.Contains(stem) || Reserved.Contains(name))
        {
            name = "_" + name;
        }

        if (name.Length > MaxSegmentLength)
        {
            name = name[..60] + "_" + ShortHash(segment);
        }

        return name;
    }

    public static string[] SplitRelative(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || IsGitAbsolute(relative))
        {
            throw new VaultException("A file path must stay inside the repository.");
        }

        var parts = relative.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            throw new VaultException("A file path must stay inside the repository.");
        }

        foreach (var part in parts)
        {
            if (part is "." or ".." || part.Contains("..", StringComparison.Ordinal))
            {
                throw new VaultException("A file path must stay inside the repository.");
            }
        }

        return parts;
    }

    public static string Combine(string vaultRoot, params string[] segments)
    {
        var root = RequireVaultRoot(vaultRoot);
        var current = root;
        foreach (var segment in segments)
        {
            if (string.IsNullOrEmpty(segment) || segment.IndexOfAny(['/', '\\']) >= 0 || IsGitAbsolute(segment))
            {
                throw new VaultException("A path segment cannot contain a directory separator.");
            }

            current = Path.Combine(current, SanitizeSegment(segment));
            EnsureInside(root, current);
        }

        return ApplyLongPathPrefix(current, OperatingSystem.IsWindows());
    }

    /// <summary>
    /// Maps a repository-relative path onto a file under <paramref name="directoryInsideVault"/>.
    /// The returned disk path is always inside the vault. <paramref name="diskRelative"/> uses
    /// the platform separator and is relative to that directory.
    /// </summary>
    public static string PlaceFile(string vaultRoot, string directoryInsideVault, string originalRelativePath, out string diskRelative)
    {
        var root = RequireVaultRoot(vaultRoot);
        var directory = Path.GetFullPath(directoryInsideVault);
        EnsureInside(root, directory);
        var parts = SplitRelative(originalRelativePath);
        var safe = parts.Select(SanitizeSegment).ToArray();
        var relative = string.Join('/', safe);
        if (relative.Length > MaxRelativeLength)
        {
            relative = "_long/" + ShortHash(originalRelativePath) + "/" + safe[^1];
        }

        diskRelative = relative.Replace('/', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(directory, diskRelative));
        EnsureInside(root, full);
        var tmp = full + ".gsh-tmp";
        EnsureInside(root, tmp);
        return ApplyLongPathPrefix(full, OperatingSystem.IsWindows());
    }

    public static void EnsureInside(string vaultRoot, string candidate)
    {
        var root = Path.GetFullPath(vaultRoot);
        var full = Path.GetFullPath(candidate);
        if (IsInside(root, full))
        {
            return;
        }

        throw new VaultException("That path is outside the backup folder.");
    }

    public static void DeleteInside(string vaultRoot, string candidate)
    {
        var root = RequireVaultRoot(vaultRoot);
        var full = Path.GetFullPath(candidate);
        EnsureInside(root, full);
        if (string.Equals(full, root, StringComparison.OrdinalIgnoreCase))
        {
            throw new VaultException("Refusing to delete the backup folder itself.");
        }

        if (Directory.Exists(full))
        {
            Directory.Delete(full, recursive: true);
            return;
        }

        if (File.Exists(full))
        {
            File.Delete(full);
        }
    }

    public static string ApplyLongPathPrefix(string fullPath, bool windows)
    {
        if (!windows)
        {
            return fullPath;
        }

        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            return fullPath;
        }

        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return @"\\?\UNC\" + fullPath[2..];
        }

        if (fullPath.Length >= 240)
        {
            return @"\\?\" + fullPath;
        }

        return fullPath;
    }

    public static string ShortHash(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(bytes)[..8].ToLowerInvariant();
    }

    /// <summary>
    /// A Git path is absolute only when it starts with a separator.
    /// <see cref="Path.IsPathRooted"/> also treats <c>a:name</c> as a Windows drive path,
    /// which would drop a legal repository file instead of sanitizing the colon.
    /// </summary>
    private static bool IsGitAbsolute(string path) =>
        path.StartsWith('/') || path.StartsWith('\\');

    private static bool IsInside(string root, string full)
    {
        var rootWithSep = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(rootWithSep, StringComparison.OrdinalIgnoreCase);
    }
}
