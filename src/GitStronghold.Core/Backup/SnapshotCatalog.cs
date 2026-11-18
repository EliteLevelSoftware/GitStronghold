using System.Text;
using System.Text.Json;
using GitStronghold.Core.Paths;
using GitStronghold.Core.Security;

namespace GitStronghold.Core.Backup;

public static class SnapshotCatalog
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<ProfileSummary> Profiles(string vault)
    {
        var root = ProfilesRoot(vault);
        if (!Directory.Exists(root))
        {
            return [];
        }

        var list = new List<ProfileSummary>();
        foreach (var dir in Directory.GetDirectories(root).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            VaultPath.EnsureInside(vault, dir);
            var snapshotPath = Path.Combine(dir, "snapshot.json");
            ProfileSummary summary;
            if (File.Exists(snapshotPath))
            {
                summary = JsonSerializer.Deserialize<ProfileSummary>(File.ReadAllText(snapshotPath), JsonOptions) ?? new ProfileSummary();
            }
            else
            {
                summary = new ProfileSummary { Login = Path.GetFileName(dir) };
            }

            var avatar = Directory.GetFiles(dir, "avatar.*").FirstOrDefault() ?? "";
            if (avatar.Length > 0)
            {
                VaultPath.EnsureInside(vault, avatar);
            }

            list.Add(new ProfileSummary
            {
                Login = summary.Login.Length > 0 ? summary.Login : Path.GetFileName(dir),
                Name = summary.Name,
                Kind = summary.Kind,
                BackedUpAt = summary.BackedUpAt,
                Saved = summary.Saved,
                Failed = summary.Failed,
                Skipped = summary.Skipped,
                RepoCount = summary.RepoCount > 0 ? summary.RepoCount : CountChildren(Path.Combine(dir, "repos")),
                GistCount = summary.GistCount,
                Target = summary.Target,
                AvatarPath = avatar
            });
        }

        return list;
    }

    public static IReadOnlyList<RepoSummary> Repos(string vault, string? owner)
    {
        var list = new List<RepoSummary>();
        var profiles = owner is null ? Profiles(vault).Select(profile => profile.Login) : [owner];
        foreach (var login in profiles)
        {
            if (!VaultPath.IsOwnerName(login))
            {
                continue;
            }

            var repos = Path.Combine(ProfilesRoot(vault), login, "repos");
            if (!Directory.Exists(repos))
            {
                continue;
            }

            VaultPath.EnsureInside(vault, repos);
            foreach (var dir in Directory.GetDirectories(repos).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var metaPath = Path.Combine(dir, "meta.json");
                if (!File.Exists(metaPath))
                {
                    continue;
                }

                var meta = JsonSerializer.Deserialize<RepoSummary>(File.ReadAllText(metaPath), JsonOptions);
                if (meta != null)
                {
                    list.Add(meta);
                }
            }
        }

        return list;
    }

    public static IReadOnlyList<BranchSummary> Branches(string vault, string owner, string repo)
    {
        var dir = BranchRoot(vault, owner, repo);
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var list = new List<BranchSummary>();
        foreach (var branch in Directory.GetDirectories(dir).OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
        {
            VaultPath.EnsureInside(vault, branch);
            var metaPath = Path.Combine(branch, "branch.json");
            if (!File.Exists(metaPath))
            {
                continue;
            }

            var meta = JsonSerializer.Deserialize<BranchSummary>(File.ReadAllText(metaPath), JsonOptions);
            if (meta != null)
            {
                list.Add(meta);
            }
        }

        return list;
    }

    public static IReadOnlyList<IndexedFile> Files(string vault, string owner, string repo, string folder)
    {
        var indexPath = Path.Combine(BranchRoot(vault, owner, repo), folder, "index.json");
        VaultPath.EnsureInside(vault, indexPath);
        if (!File.Exists(indexPath))
        {
            return [];
        }

        var index = JsonSerializer.Deserialize<FileIndex>(File.ReadAllText(indexPath), JsonOptions);
        return index?.Files ?? [];
    }

    public static string? ResolveFile(string vault, string owner, string repo, string folder, IndexedFile file)
    {
        if (file.Disk.Length == 0 || file.Disk.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(file.Disk))
        {
            return null;
        }

        var filesDir = Path.Combine(BranchRoot(vault, owner, repo), folder, "files");
        var full = Path.GetFullPath(Path.Combine(filesDir, file.Disk));
        VaultPath.EnsureInside(vault, full);
        return File.Exists(full) ? full : null;
    }

    public static string ReadText(string vault, string fullPath, int maxChars = 200_000)
    {
        VaultPath.EnsureInside(vault, fullPath);
        var info = new FileInfo(fullPath);
        if (!info.Exists)
        {
            return "";
        }

        using var stream = info.OpenRead();
        var probe = new byte[Math.Min(8192, (int)Math.Min(info.Length, 8192))];
        var read = stream.Read(probe, 0, probe.Length);
        if (probe.Take(read).Any(b => b == 0))
        {
            return "Binary file, " + info.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + " bytes.";
        }

        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[Math.Min(maxChars, (int)Math.Min(info.Length, maxChars))];
        var count = reader.Read(buffer, 0, buffer.Length);
        var text = new string(buffer, 0, count);
        if (info.Length > maxChars)
        {
            text += "\n\nThe rest of this file is not shown.";
        }

        return text;
    }

    public static IReadOnlyList<LogDocument> Logs(string vault)
    {
        var dir = VaultPath.Combine(vault, "logs");
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var list = new List<LogDocument>();
        foreach (var file in Directory.GetFiles(dir, "*.log").OrderByDescending(Path.GetFileName, StringComparer.Ordinal))
        {
            VaultPath.EnsureInside(vault, file);
            var text = SecretRedactor.Redact(File.ReadAllText(file));
            if (text.Length > 100_000)
            {
                text = text[^100_000..];
            }

            list.Add(new LogDocument { Name = Path.GetFileName(file), Path = file, Text = text });
        }

        return list;
    }

    public static void DeleteProfile(string vault, string login)
    {
        if (!VaultPath.IsOwnerName(login))
        {
            throw new VaultException("That profile name is not valid.");
        }

        var dir = VaultPath.Combine(vault, "profiles", login);
        VaultPath.DeleteInside(vault, dir);
    }

    private static string ProfilesRoot(string vault) => VaultPath.Combine(vault, "profiles");

    private static string BranchRoot(string vault, string owner, string repo)
    {
        if (!VaultPath.IsOwnerName(owner) || !VaultPath.IsRepoName(repo))
        {
            throw new VaultException("That repository cannot be opened.");
        }

        return VaultPath.Combine(vault, "profiles", owner, "repos", repo, "branches");
    }

    private static int CountChildren(string path) => Directory.Exists(path) ? Directory.GetDirectories(path).Length : 0;
}
