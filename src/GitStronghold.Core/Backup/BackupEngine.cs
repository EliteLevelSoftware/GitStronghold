using System.Globalization;
using System.Text.Json;
using GitStronghold.Core.GitHub;
using GitStronghold.Core.Paths;
using GitStronghold.Core.Security;

namespace GitStronghold.Core.Backup;

public sealed class BackupEngine : IDisposable
{
    private const long MaxBlobBytes = 100L * 1024 * 1024;
    private const long MaxAssetBytes = 2L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly GitHubClient _client;
    private readonly string? _token;
    private readonly TimeProvider _clock;

    public BackupEngine(HttpMessageHandler handler, string? token, GitHubClientOptions? options = null, TimeProvider? clock = null)
    {
        _token = string.IsNullOrWhiteSpace(token) ? null : token.Trim();
        _clock = clock ?? TimeProvider.System;
        var clientOptions = options ?? new GitHubClientOptions();
        if (clientOptions.Clock == TimeProvider.System && clock is not null)
        {
            clientOptions = new GitHubClientOptions
            {
                MaxRateLimitWaits = clientOptions.MaxRateLimitWaits,
                MaxSingleWait = clientOptions.MaxSingleWait,
                MaxTotalWait = clientOptions.MaxTotalWait,
                Delay = clientOptions.Delay,
                Clock = clock
            };
        }

        _client = new GitHubClient(handler, _token, clientOptions, disposeHandler: false);
    }

    public async Task<BackupResult> RunAsync(BackupRequest request, IProgress<BackupProgress>? progress, CancellationToken cancellationToken)
    {
        var errors = new List<string>();
        var tally = new Tally();
        string logFile = "";
        var owner = "";
        string? onlyRepo = null;
        var repoCount = 0;
        var gistCount = 0;

        void Report(string phase, string detail) =>
            progress?.Report(new BackupProgress(phase, detail, tally.Saved, tally.Failed, tally.Skipped));

        _client.RateLimited += wait =>
            Report("Rate limit", "Waiting " + Math.Ceiling(wait.TotalSeconds).ToString(CultureInfo.InvariantCulture) + " seconds for GitHub.");

        try
        {
            if (!ParsedTarget.TryParse(request.Target, out var target, out var parseError))
            {
                return Failed(parseError);
            }

            owner = target.Owner;
            onlyRepo = target.Repository;
            var vault = VaultPath.RequireVaultRoot(request.VaultRoot);
            Directory.CreateDirectory(vault);
            logFile = VaultPath.Combine(vault, "logs", _clock.GetUtcNow().ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
            Directory.CreateDirectory(Path.GetDirectoryName(logFile)!);
            Log(logFile, "Backup started for " + request.Target.Trim());
            Report("Profile", "Looking up " + owner);

            var userResult = await _client.GetJsonAsync(GitHubUrls.User(owner), cancellationToken);
            if (!userResult.IsSuccess)
            {
                var message = "GitHub returned HTTP " + userResult.Status + " for " + owner + ".";
                Log(logFile, message);
                return Failed(message, logFile, tally, owner, onlyRepo);
            }

            using var userDoc = JsonDocument.Parse(userResult.Bytes);
            var user = userDoc.RootElement;
            var login = user.GetProperty("login").GetString() ?? owner;
            if (!VaultPath.IsOwnerName(login))
            {
                return Failed("GitHub returned a name that cannot be stored.", logFile, tally, owner, onlyRepo);
            }

            owner = login;
            var kind = user.TryGetProperty("type", out var typeNode) ? typeNode.GetString() ?? "User" : "User";
            var profileDir = VaultPath.Combine(vault, "profiles", owner);
            Directory.CreateDirectory(profileDir);
            await WriteJsonAsync(Path.Combine(profileDir, "profile.json"), userResult.Bytes, cancellationToken);
            await SaveAvatarAsync(vault, profileDir, user, request.Incremental, tally, errors, logFile, cancellationToken);

            string? tokenLogin = null;
            if (_token != null)
            {
                var me = await _client.GetJsonAsync(GitHubUrls.AuthenticatedUser(), cancellationToken);
                if (me.IsSuccess)
                {
                    using var meDoc = JsonDocument.Parse(me.Bytes);
                    tokenLogin = meDoc.RootElement.GetProperty("login").GetString();
                }
                else
                {
                    errors.Add("The token was not accepted (HTTP " + me.Status + "). Public data will still be requested.");
                    Log(logFile, errors[^1]);
                }
            }

            Report("Repositories", "Listing repositories");
            var repos = await ListReposAsync(owner, onlyRepo, kind, tokenLogin, cancellationToken);
            repoCount = repos.Count;
            foreach (var repo in repos)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = repo.GetProperty("name").GetString() ?? "";
                if (!VaultPath.IsRepoName(name))
                {
                    tally.Failed++;
                    errors.Add("Skipped a repository with an unsafe name.");
                    continue;
                }

                var repoOwner = repo.TryGetProperty("owner", out var ownerNode) && ownerNode.TryGetProperty("login", out var loginNode)
                    ? loginNode.GetString() ?? owner
                    : owner;
                if (!string.Equals(repoOwner, owner, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                Report("Repository", name);
                var beforeSaved = tally.Saved;
                var beforeFailed = tally.Failed;
                var beforeSkipped = tally.Skipped;
                try
                {
                    await SaveRepoAsync(vault, owner, repo, request.Incremental, tally, errors, logFile, progress, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    tally.Failed++;
                    var message = SecretRedactor.Redact(name + ": " + ex.Message, _token);
                    errors.Add(message);
                    Log(logFile, message);
                }

                await WriteMetaAsync(vault, owner, repo, tally.Saved - beforeSaved, tally.Failed - beforeFailed, tally.Skipped - beforeSkipped, cancellationToken);
            }

            if (!string.Equals(kind, "Organization", StringComparison.OrdinalIgnoreCase))
            {
                Report("Gists", "Listing gists");
                gistCount = await SaveGistsAsync(vault, owner, tokenLogin, tally, errors, logFile, cancellationToken);
            }

            var snapshot = new
            {
                login = owner,
                name = user.TryGetProperty("name", out var nameNode) ? nameNode.GetString() ?? owner : owner,
                kind,
                backedUpAt = _clock.GetUtcNow().ToString("O", CultureInfo.InvariantCulture),
                saved = tally.Saved,
                failed = tally.Failed,
                skipped = tally.Skipped,
                repoCount,
                gistCount,
                target = onlyRepo is null ? owner : owner + "/" + onlyRepo
            };
            await File.WriteAllTextAsync(Path.Combine(profileDir, "snapshot.json"), JsonSerializer.Serialize(snapshot, JsonOptions), cancellationToken);
            Log(logFile, "Saved " + tally.Saved + ", skipped " + tally.Skipped + ", failed " + tally.Failed + ".");
            Report("Done", "Saved " + tally.Saved + ", skipped " + tally.Skipped + ", failed " + tally.Failed + ".");
            return new BackupResult
            {
                Succeeded = tally.Failed == 0,
                Owner = owner,
                Repository = onlyRepo,
                Saved = tally.Saved,
                Failed = tally.Failed,
                Skipped = tally.Skipped,
                RepoCount = repoCount,
                GistCount = gistCount,
                LogFile = logFile,
                Errors = errors
            };
        }
        catch (OperationCanceledException)
        {
            if (logFile.Length > 0)
            {
                Log(logFile, "Backup cancelled.");
            }

            return new BackupResult
            {
                Cancelled = true,
                Owner = owner,
                Repository = onlyRepo,
                Saved = tally.Saved,
                Failed = tally.Failed,
                Skipped = tally.Skipped,
                RepoCount = repoCount,
                GistCount = gistCount,
                LogFile = logFile,
                Errors = errors
            };
        }
        catch (Exception ex)
        {
            var message = SecretRedactor.Redact(ex.Message, _token);
            if (logFile.Length > 0)
            {
                Log(logFile, message);
            }

            errors.Add(message);
            return new BackupResult
            {
                Owner = owner,
                Repository = onlyRepo,
                Saved = tally.Saved,
                Failed = tally.Failed + 1,
                Skipped = tally.Skipped,
                RepoCount = repoCount,
                GistCount = gistCount,
                LogFile = logFile,
                Errors = errors
            };
        }
    }

    public void Dispose() => _client.Dispose();

    private async Task<List<JsonElement>> ListReposAsync(string owner, string? onlyRepo, string kind, string? tokenLogin, CancellationToken cancellationToken)
    {
        if (onlyRepo != null)
        {
            var one = await _client.GetJsonAsync(GitHubUrls.Repo(owner, onlyRepo), cancellationToken);
            if (!one.IsSuccess)
            {
                throw new GitHubCallException(one.Status, "HTTP " + one.Status + " for " + owner + "/" + onlyRepo + ".");
            }

            using var doc = JsonDocument.Parse(one.Bytes);
            return [doc.RootElement.Clone()];
        }

        Uri first;
        if (string.Equals(kind, "Organization", StringComparison.OrdinalIgnoreCase))
        {
            first = GitHubUrls.OrgRepos(owner);
        }
        else if (tokenLogin != null && string.Equals(tokenLogin, owner, StringComparison.OrdinalIgnoreCase))
        {
            first = GitHubUrls.AuthenticatedRepos();
        }
        else
        {
            first = GitHubUrls.UserRepos(owner);
        }

        var page = await _client.GetAllAsync(first, cancellationToken);
        return page.ToList();
    }

    private async Task SaveRepoAsync(
        string vault,
        string owner,
        JsonElement repo,
        bool incremental,
        Tally tally,
        List<string> errors,
        string logFile,
        IProgress<BackupProgress>? progress,
        CancellationToken cancellationToken)
    {
        var name = repo.GetProperty("name").GetString()!;
        var repoDir = VaultPath.Combine(vault, "profiles", owner, "repos", name);
        Directory.CreateDirectory(repoDir);
        await File.WriteAllTextAsync(Path.Combine(repoDir, "repo.json"), JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(repo.GetRawText()), JsonOptions), cancellationToken);

        var languages = await _client.GetJsonAsync(GitHubUrls.Languages(owner, name), cancellationToken);
        if (languages.IsSuccess)
        {
            await WriteJsonAsync(Path.Combine(repoDir, "languages.json"), languages.Bytes, cancellationToken);
        }

        ReportFiles(progress, name, "Listing branches", tally);
        var branches = await _client.GetAllAsync(GitHubUrls.Branches(owner, name), cancellationToken);
        var usedFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var branch in branches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var branchName = branch.GetProperty("name").GetString() ?? "";
            var sha = branch.GetProperty("commit").GetProperty("sha").GetString() ?? "";
            if (branchName.Length == 0 || sha.Length == 0)
            {
                tally.Failed++;
                errors.Add(name + ": a branch was missing its name.");
                continue;
            }

            var folder = UniqueFolder(VaultPath.BranchFolder(branchName), usedFolders);
            var branchDir = VaultPath.Combine(vault, "profiles", owner, "repos", name, "branches", folder);
            Directory.CreateDirectory(branchDir);
            var filesDir = VaultPath.Combine(vault, "profiles", owner, "repos", name, "branches", folder, "files");
            Directory.CreateDirectory(filesDir);
            await File.WriteAllTextAsync(
                Path.Combine(branchDir, "branch.json"),
                JsonSerializer.Serialize(new { name = branchName, sha, folder }, JsonOptions),
                cancellationToken);

            var previous = LoadIndex(Path.Combine(branchDir, "index.json"));
            var files = new List<IndexedFile>();
            List<TreeFile> tree;
            try
            {
                tree = await ListTreeAsync(owner, name, sha, cancellationToken);
            }
            catch (GitHubCallException ex)
            {
                tally.Failed++;
                errors.Add(name + " " + branchName + ": HTTP " + ex.Status);
                Log(logFile, errors[^1]);
                continue;
            }

            foreach (var file in tree)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (file.Kind == "commit")
                {
                    files.Add(new IndexedFile { Path = file.Path, Sha = file.Sha, Mode = file.Mode, Disk = "" });
                    continue;
                }

                ReportFiles(progress, name, file.Path, tally);
                try
                {
                    await SaveFileAsync(vault, filesDir, owner, name, branchName, file, incremental, previous, files, tally, errors, cancellationToken);
                }
                catch (VaultException ex)
                {
                    tally.Failed++;
                    errors.Add(name + ": " + SecretRedactor.Redact(ex.Message, _token));
                }
            }

            await File.WriteAllTextAsync(Path.Combine(branchDir, "index.json"), JsonSerializer.Serialize(new FileIndex { Files = files }, JsonOptions), cancellationToken);
        }

        ReportFiles(progress, name, "Listing releases", tally);
        var releases = await _client.GetAllAsync(GitHubUrls.Releases(owner, name), cancellationToken);
        foreach (var release in releases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tag = release.TryGetProperty("tag_name", out var tagNode) ? tagNode.GetString() ?? "untagged" : "untagged";
            string releaseDir;
            try
            {
                releaseDir = VaultPath.Combine(vault, "profiles", owner, "repos", name, "releases", VaultPath.SanitizeSegment(tag.Replace('/', '\u2215').Replace('\\', '\u2215')));
            }
            catch (VaultException)
            {
                tally.Failed++;
                errors.Add(name + ": a release tag could not be stored.");
                continue;
            }

            Directory.CreateDirectory(releaseDir);
            await File.WriteAllTextAsync(Path.Combine(releaseDir, "release.json"), JsonSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(release.GetRawText()), JsonOptions), cancellationToken);
            var notes = release.TryGetProperty("body", out var bodyNode) ? bodyNode.GetString() ?? "" : "";
            await File.WriteAllTextAsync(Path.Combine(releaseDir, "notes.md"), notes, cancellationToken);
            tally.Saved++;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var asset in assets.EnumerateArray())
            {
                var assetName = asset.GetProperty("name").GetString() ?? "asset";
                var id = asset.GetProperty("id").GetInt64();
                string assetPath;
                try
                {
                    assetPath = VaultPath.PlaceFile(vault, Path.Combine(releaseDir, "assets"), assetName, out _);
                }
                catch (VaultException)
                {
                    tally.Failed++;
                    errors.Add(name + ": an asset name could not be stored.");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(assetPath)!);
                var downloaded = await DownloadToFileAsync(vault, GitHubUrls.ReleaseAsset(owner, name, id), GitHubClient.AssetAccept, assetPath, MaxAssetBytes, cancellationToken);
                if (downloaded)
                {
                    tally.Saved++;
                }
                else
                {
                    tally.Failed++;
                    errors.Add(name + ": asset " + assetName + " was not saved.");
                    Log(logFile, errors[^1]);
                }
            }
        }
    }

    private async Task<int> SaveGistsAsync(string vault, string owner, string? tokenLogin, Tally tally, List<string> errors, string logFile, CancellationToken cancellationToken)
    {
        var first = tokenLogin != null && string.Equals(tokenLogin, owner, StringComparison.OrdinalIgnoreCase)
            ? GitHubUrls.AuthenticatedGists()
            : GitHubUrls.UserGists(owner);
        IReadOnlyList<JsonElement> gists;
        try
        {
            gists = await _client.GetAllAsync(first, cancellationToken);
        }
        catch (GitHubCallException ex) when (ex.Status == 404)
        {
            return 0;
        }

        var count = 0;
        foreach (var stub in gists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = stub.GetProperty("id").GetString() ?? "";
            if (id.Length == 0)
            {
                tally.Failed++;
                continue;
            }

            var detail = await _client.GetJsonAsync(GitHubUrls.Gist(id), cancellationToken);
            if (!detail.IsSuccess)
            {
                tally.Failed++;
                errors.Add("Gist " + id + " HTTP " + detail.Status);
                Log(logFile, errors[^1]);
                continue;
            }

            using var doc = JsonDocument.Parse(detail.Bytes);
            var gist = doc.RootElement;
            var gistDir = VaultPath.Combine(vault, "profiles", owner, "gists", id);
            Directory.CreateDirectory(gistDir);
            await WriteJsonAsync(Path.Combine(gistDir, "gist.json"), detail.Bytes, cancellationToken);
            count++;
            if (!gist.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var file in files.EnumerateObject())
            {
                var filename = file.Value.TryGetProperty("filename", out var filenameNode) ? filenameNode.GetString() ?? file.Name : file.Name;
                var truncated = file.Value.TryGetProperty("truncated", out var truncatedNode) && truncatedNode.GetBoolean();
                string? content = file.Value.TryGetProperty("content", out var contentNode) ? contentNode.GetString() : null;
                string disk;
                try
                {
                    disk = VaultPath.PlaceFile(vault, Path.Combine(gistDir, "files"), filename, out _);
                }
                catch (VaultException)
                {
                    tally.Failed++;
                    errors.Add("Gist " + id + ": a file name could not be stored.");
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(disk)!);
                if (!truncated && content != null)
                {
                    await File.WriteAllTextAsync(disk, content, cancellationToken);
                    tally.Saved++;
                    continue;
                }

                if (file.Value.TryGetProperty("raw_url", out var rawNode) && Uri.TryCreate(rawNode.GetString(), UriKind.Absolute, out var raw))
                {
                    var downloaded = await DownloadToFileAsync(vault, raw, GitHubClient.RawAccept, disk, MaxBlobBytes, cancellationToken);
                    if (downloaded)
                    {
                        tally.Saved++;
                    }
                    else
                    {
                        tally.Failed++;
                        errors.Add("Gist " + id + ": " + filename + " was not saved.");
                    }
                }
                else
                {
                    tally.Failed++;
                    errors.Add("Gist " + id + ": " + filename + " had no content.");
                }
            }
        }

        return count;
    }

    private async Task<List<TreeFile>> ListTreeAsync(string owner, string repo, string sha, CancellationToken cancellationToken)
    {
        var recursive = await _client.GetJsonAsync(GitHubUrls.Tree(owner, repo, sha, recursive: true), cancellationToken);
        if (!recursive.IsSuccess)
        {
            throw new GitHubCallException(recursive.Status, "HTTP " + recursive.Status);
        }

        using var doc = JsonDocument.Parse(recursive.Bytes);
        var truncated = doc.RootElement.TryGetProperty("truncated", out var truncatedNode) && truncatedNode.GetBoolean();
        if (!truncated)
        {
            return ReadTree(doc.RootElement, "");
        }

        return await WalkTreeAsync(owner, repo, sha, "", cancellationToken);
    }

    private async Task<List<TreeFile>> WalkTreeAsync(string owner, string repo, string sha, string prefix, CancellationToken cancellationToken)
    {
        var result = await _client.GetJsonAsync(GitHubUrls.Tree(owner, repo, sha, recursive: false), cancellationToken);
        if (!result.IsSuccess)
        {
            throw new GitHubCallException(result.Status, "HTTP " + result.Status);
        }

        using var doc = JsonDocument.Parse(result.Bytes);
        var files = new List<TreeFile>();
        if (!doc.RootElement.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
        {
            return files;
        }

        foreach (var entry in tree.EnumerateArray())
        {
            var type = entry.GetProperty("type").GetString() ?? "";
            var path = entry.GetProperty("path").GetString() ?? "";
            var entrySha = entry.GetProperty("sha").GetString() ?? "";
            var mode = entry.TryGetProperty("mode", out var modeNode) ? modeNode.GetString() ?? "" : "";
            var full = prefix.Length == 0 ? path : prefix + "/" + path;
            if (type == "tree")
            {
                files.AddRange(await WalkTreeAsync(owner, repo, entrySha, full, cancellationToken));
            }
            else if (type is "blob" or "commit")
            {
                long size = entry.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var parsed) ? parsed : 0;
                files.Add(new TreeFile(full, entrySha, mode, type, size));
            }
        }

        return files;
    }

    private static List<TreeFile> ReadTree(JsonElement root, string prefix)
    {
        var files = new List<TreeFile>();
        if (!root.TryGetProperty("tree", out var tree) || tree.ValueKind != JsonValueKind.Array)
        {
            return files;
        }

        foreach (var entry in tree.EnumerateArray())
        {
            var type = entry.GetProperty("type").GetString() ?? "";
            if (type is not ("blob" or "commit"))
            {
                continue;
            }

            var path = entry.GetProperty("path").GetString() ?? "";
            if (prefix.Length > 0)
            {
                path = prefix + "/" + path;
            }

            long size = entry.TryGetProperty("size", out var sizeNode) && sizeNode.TryGetInt64(out var parsed) ? parsed : 0;
            files.Add(new TreeFile(path, entry.GetProperty("sha").GetString() ?? "", entry.TryGetProperty("mode", out var modeNode) ? modeNode.GetString() ?? "" : "", type, size));
        }

        return files;
    }

    private async Task SaveFileAsync(
        string vault,
        string filesDir,
        string owner,
        string repo,
        string branchName,
        TreeFile file,
        bool incremental,
        Dictionary<string, IndexedFile> previous,
        List<IndexedFile> files,
        Tally tally,
        List<string> errors,
        CancellationToken cancellationToken)
    {
        if (incremental
            && previous.TryGetValue(file.Path, out var prior)
            && string.Equals(prior.Sha, file.Sha, StringComparison.Ordinal)
            && prior.Disk.Length > 0
            && !prior.Disk.Contains("..", StringComparison.Ordinal))
        {
            var priorFull = Path.GetFullPath(Path.Combine(filesDir, prior.Disk));
            VaultPath.EnsureInside(vault, priorFull);
            if (File.Exists(priorFull))
            {
                tally.Skipped++;
                files.Add(prior);
                return;
            }
        }

        var destination = VaultPath.PlaceFile(vault, filesDir, file.Path, out var diskRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var tmp = destination + ".gsh-tmp";
        VaultPath.EnsureInside(vault, tmp);
        var blob = await DownloadToFileAsync(vault, GitHubUrls.Blob(owner, repo, file.Sha), GitHubClient.RawAccept, tmp, MaxBlobBytes, cancellationToken);
        if (!blob)
        {
            var contents = await DownloadToFileAsync(vault, GitHubUrls.Contents(owner, repo, file.Path, branchName), GitHubClient.RawAccept, tmp, MaxBlobBytes, cancellationToken);
            if (!contents)
            {
                if (File.Exists(tmp))
                {
                    File.Delete(tmp);
                }

                tally.Failed++;
                errors.Add(repo + ": " + file.Path + " was not saved.");
                return;
            }
        }

        File.Move(tmp, destination, overwrite: true);
        tally.Saved++;
        files.Add(new IndexedFile
        {
            Path = file.Path,
            Disk = diskRelative,
            Sha = file.Sha,
            Bytes = new FileInfo(destination).Length,
            Mode = file.Mode
        });
    }

    private async Task<bool> DownloadToFileAsync(string vault, Uri uri, string accept, string destination, long maxBytes, CancellationToken cancellationToken)
    {
        var tmp = destination.EndsWith(".gsh-tmp", StringComparison.Ordinal) ? destination : destination + ".gsh-tmp";
        try
        {
            VaultPath.EnsureInside(vault, destination);
            VaultPath.EnsureInside(vault, tmp);
            Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
            await using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var result = await _client.CopyAsync(uri, accept, stream, maxBytes, cancellationToken);
                if (!result.IsSuccess)
                {
                    stream.Close();
                    if (File.Exists(tmp))
                    {
                        File.Delete(tmp);
                    }

                    return false;
                }
            }

            if (!string.Equals(tmp, destination, StringComparison.Ordinal))
            {
                File.Move(tmp, destination, overwrite: true);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or VaultException or GitHubCallException or HttpRequestException)
        {
            if (File.Exists(tmp))
            {
                File.Delete(tmp);
            }

            return false;
        }
    }

    private async Task SaveAvatarAsync(string vault, string profileDir, JsonElement user, bool incremental, Tally tally, List<string> errors, string logFile, CancellationToken cancellationToken)
    {
        if (!user.TryGetProperty("avatar_url", out var avatarNode) || !Uri.TryCreate(avatarNode.GetString(), UriKind.Absolute, out var avatar))
        {
            return;
        }

        if (incremental && Directory.Exists(profileDir) && Directory.GetFiles(profileDir, "avatar.*").Length > 0)
        {
            tally.Skipped++;
            return;
        }

        var path = Path.Combine(profileDir, "avatar.bin");
        VaultPath.EnsureInside(vault, path);
        var saved = await DownloadToFileAsync(vault, avatar, "application/octet-stream", path, 5 * 1024 * 1024, cancellationToken);
        if (!saved)
        {
            tally.Failed++;
            errors.Add("The avatar was not saved.");
            Log(logFile, errors[^1]);
            return;
        }

        var type = SniffImage(path);
        if (type is null)
        {
            File.Delete(path);
            tally.Failed++;
            errors.Add("The avatar was not an image.");
            Log(logFile, errors[^1]);
            return;
        }

        var named = Path.Combine(profileDir, "avatar" + type);
        File.Move(path, named, overwrite: true);
        tally.Saved++;
    }

    private static string? SniffImage(string path)
    {
        var header = new byte[12];
        using var stream = File.OpenRead(path);
        var read = stream.Read(header, 0, header.Length);
        if (read >= 8 && header[0] == 0x89 && header[1] == 0x50)
        {
            return ".png";
        }

        if (read >= 3 && header[0] == 0xFF && header[1] == 0xD8)
        {
            return ".jpg";
        }

        if (read >= 6 && header[0] == (byte)'G' && header[1] == (byte)'I' && header[2] == (byte)'F')
        {
            return ".gif";
        }

        if (read >= 12 && header[0] == (byte)'R' && header[8] == (byte)'W')
        {
            return ".webp";
        }

        return null;
    }

    private static async Task WriteJsonAsync(string path, byte[] bytes, CancellationToken cancellationToken)
    {
        using var doc = JsonDocument.Parse(bytes);
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(doc.RootElement, JsonOptions), cancellationToken);
    }

    private static async Task WriteMetaAsync(string vault, string owner, JsonElement repo, int saved, int failed, int skipped, CancellationToken cancellationToken)
    {
        var name = repo.GetProperty("name").GetString()!;
        var meta = new RepoSummary
        {
            Owner = owner,
            Name = name,
            Description = repo.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "",
            DefaultBranch = repo.TryGetProperty("default_branch", out var branch) ? branch.GetString() ?? "" : "",
            Language = repo.TryGetProperty("language", out var language) && language.ValueKind == JsonValueKind.String ? language.GetString() ?? "" : "",
            Private = repo.TryGetProperty("private", out var priv) && priv.GetBoolean(),
            Stars = repo.TryGetProperty("stargazers_count", out var stars) ? stars.GetInt32() : 0,
            Saved = Math.Max(0, saved),
            Failed = Math.Max(0, failed),
            Skipped = Math.Max(0, skipped),
            HtmlUrl = repo.TryGetProperty("html_url", out var html) ? html.GetString() ?? "" : ""
        };
        var path = Path.Combine(VaultPath.Combine(vault, "profiles", owner, "repos", name), "meta.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(meta, JsonOptions), cancellationToken);
    }

    private static Dictionary<string, IndexedFile> LoadIndex(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new Dictionary<string, IndexedFile>(StringComparer.Ordinal);
            }

            var index = JsonSerializer.Deserialize<FileIndex>(File.ReadAllText(path));
            return index?.Files.Where(file => file.Path.Length > 0).ToDictionary(file => file.Path, StringComparer.Ordinal)
                ?? new Dictionary<string, IndexedFile>(StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return new Dictionary<string, IndexedFile>(StringComparer.Ordinal);
        }
    }

    private static string UniqueFolder(string folder, HashSet<string> used)
    {
        var name = folder;
        var salt = 1;
        while (!used.Add(name))
        {
            name = VaultPath.SanitizeSegment(folder + "-" + salt.ToString(CultureInfo.InvariantCulture));
            salt++;
        }

        return name;
    }

    private static void ReportFiles(IProgress<BackupProgress>? progress, string repo, string detail, Tally tally) =>
        progress?.Report(new BackupProgress(repo, detail, tally.Saved, tally.Failed, tally.Skipped));

    private void Log(string logFile, string line)
    {
        var safe = SecretRedactor.Redact(line, _token);
        File.AppendAllText(logFile, safe + Environment.NewLine);
    }

    private static BackupResult Failed(string error, string logFile = "", Tally? tally = null, string owner = "", string? repo = null) =>
        new()
        {
            Owner = owner,
            Repository = repo,
            LogFile = logFile,
            Saved = tally?.Saved ?? 0,
            Failed = (tally?.Failed ?? 0) + 1,
            Skipped = tally?.Skipped ?? 0,
            Errors = [error]
        };

    private sealed class Tally
    {
        public int Saved;
        public int Failed;
        public int Skipped;
    }

    private sealed record TreeFile(string Path, string Sha, string Mode, string Kind, long Size);
}
