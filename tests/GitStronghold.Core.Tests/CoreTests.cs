using System.Net;
using System.Text;
using GitStronghold.Core.Backup;
using GitStronghold.Core.GitHub;
using GitStronghold.Core.Paths;
using GitStronghold.Core.Security;

namespace GitStronghold.Core.Tests;

public class VaultPathTests
{
    [Fact]
    public void Branch_names_with_slashes_stay_one_folder()
    {
        var folder = VaultPath.BranchFolder("feature/a\\b");
        Assert.DoesNotContain("/", folder);
        Assert.DoesNotContain("\\", folder);
        Assert.Equal("feature\u2215a\u2215b", folder);
    }

    [Fact]
    public void Windows_invalid_names_are_made_safe()
    {
        Assert.Equal("_CON.txt", VaultPath.SanitizeSegment("CON.txt"));
        Assert.Equal("_aux", VaultPath.SanitizeSegment("aux"));
        Assert.Equal("a_b_.md", VaultPath.SanitizeSegment("a:b?.md"));
        Assert.Equal("end", VaultPath.SanitizeSegment("end."));
    }

    [Fact]
    public void Long_paths_are_folded_and_windows_prefix_is_applied()
    {
        var vault = NewVault();
        var files = VaultPath.Combine(vault, "files");
        Directory.CreateDirectory(files);
        var relative = string.Join('/', Enumerable.Repeat("directory", 30)) + "/file.txt";
        var full = VaultPath.PlaceFile(vault, files, relative, out var disk);
        Assert.StartsWith("_long" + Path.DirectorySeparatorChar, disk);
        Assert.EndsWith("file.txt", disk);
        Assert.True(disk.Length < relative.Length);
        VaultPath.EnsureInside(vault, full);

        var longWindows = new string('a', 260);
        var prefixed = VaultPath.ApplyLongPathPrefix(@"C:\vault\" + longWindows, windows: true);
        Assert.StartsWith(@"\\?\", prefixed);
        Assert.Equal("/tmp/short", VaultPath.ApplyLongPathPrefix("/tmp/short", windows: false));
    }

    [Fact]
    public void Paths_cannot_leave_the_vault()
    {
        var vault = NewVault();
        var outside = Path.Combine(Path.GetTempPath(), "gsh-keep-" + Guid.NewGuid().ToString("n"));
        File.WriteAllText(outside, "keep");
        try
        {
            Assert.Throws<VaultException>(() => VaultPath.Combine(vault, ".."));
            Assert.Throws<VaultException>(() => VaultPath.PlaceFile(vault, vault, "../escape.txt", out _));
            Assert.Throws<VaultException>(() => VaultPath.DeleteInside(vault, outside));
            Assert.Equal("keep", File.ReadAllText(outside));
            Assert.Throws<VaultException>(() => VaultPath.RequireVaultRoot(Path.GetPathRoot(vault)!));
        }
        finally
        {
            File.Delete(outside);
        }
    }

    [Theory]
    [InlineData("octocat", true)]
    [InlineData("-nope", false)]
    [InlineData("nope-", false)]
    [InlineData("has space", false)]
    [InlineData("..", false)]
    [InlineData("good-name", true)]
    public void Owner_names_follow_github_rules(string name, bool valid) =>
        Assert.Equal(valid, VaultPath.IsOwnerName(name));

    private static string NewVault()
    {
        var path = Path.Combine(Path.GetTempPath(), "gsh-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(path);
        return path;
    }
}

public class MarkdownAndLinkTests
{
    [Fact]
    public void Remote_images_scripts_and_unsafe_links_are_removed()
    {
        var input = """
            # Notes
            <script>alert(1)</script>
            <img src="https://evil.example/a.png" onerror="alert(1)">
            ![remote](https://evil.example/a.png)
            ![](https://evil.example/b.png)
            ![local](./shot.png)
            [click](javascript:alert(1))
            [site](https://github.com/octocat)
            [file](file:///etc/passwd)
            <iframe src="https://evil.example"></iframe>
            """;

        var output = MarkdownSanitizer.Sanitize(input);
        Assert.DoesNotContain("script", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil.example", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("onerror", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("javascript:", output, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("file:", output, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("![local](./shot.png)", output);
        Assert.Contains("[site](https://github.com/octocat)", output);
        Assert.Contains("click", output);
        Assert.Contains("Image omitted", output);
    }

    [Fact]
    public void Only_http_and_https_open_outside_the_app()
    {
        Assert.True(ExternalUrl.IsSafeHttp("https://github.com/settings/personal-access-tokens/new", out _));
        Assert.True(ExternalUrl.IsSafeHttp("http://example.com", out _));
        Assert.False(ExternalUrl.IsSafeHttp("javascript:alert(1)", out _));
        Assert.False(ExternalUrl.IsSafeHttp("file:///etc/passwd", out _));
        Assert.False(ExternalUrl.IsSafeHttp("/etc/passwd", out _));
        Assert.False(ExternalUrl.IsSafeHttp("git://github.com/octocat", out _));
    }

    [Fact]
    public void Tokens_are_redacted()
    {
        var token = "github_pat_" + new string('a', 22);
        var classic = "ghp_" + new string('b', 36);
        var text = "Authorization: Bearer " + token + " and " + classic;
        var redacted = SecretRedactor.Redact(text, token);
        Assert.DoesNotContain(token, redacted);
        Assert.DoesNotContain(classic, redacted);
        Assert.Contains("[redacted]", redacted);
    }
}

public class GitHubClientTests
{
    [Fact]
    public async Task Token_stays_on_the_api_host_and_is_dropped_on_redirect()
    {
        var token = "github_pat_" + new string('c', 20);
        var hub = new Hub(request =>
        {
            var host = request.RequestUri!.Host;
            if (host == "api.github.com")
            {
                Assert.Equal(token, request.Headers.Authorization?.Parameter);
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://objects.githubusercontent.com/asset");
                return response;
            }

            Assert.Equal("objects.githubusercontent.com", host);
            Assert.Null(request.Headers.Authorization);
            return Hub.Bytes(HttpStatusCode.OK, "asset"u8.ToArray());
        });

        using var client = new GitHubClient(hub, token);
        using var buffer = new MemoryStream();
        var result = await client.CopyAsync(GitHubUrls.ReleaseAsset("octo", "repo", 7), GitHubClient.AssetAccept, buffer, 1000, CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal("asset", Encoding.UTF8.GetString(buffer.ToArray()));
        Assert.Equal(2, hub.Seen.Count);
    }

    [Fact]
    public async Task Disallowed_and_insecure_urls_are_not_sent()
    {
        var hub = new Hub(_ => new HttpResponseMessage(HttpStatusCode.OK));
        using var client = new GitHubClient(hub, "ghp_" + new string('d', 20));
        var evil = await client.GetJsonAsync(new Uri("https://evil.example/steal"), CancellationToken.None);
        var plain = await client.GetJsonAsync(new Uri("http://api.github.com/users/octocat"), CancellationToken.None);
        Assert.False(evil.IsSuccess);
        Assert.False(plain.IsSuccess);
        Assert.Empty(hub.Seen);
    }

    [Fact]
    public async Task Rate_limit_waits_are_capped()
    {
        var waits = new List<TimeSpan>();
        var calls = 0;
        var hub = new Hub(_ =>
        {
            calls++;
            if (calls == 1)
            {
                var response = new HttpResponseMessage(HttpStatusCode.Forbidden);
                response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(10000));
                response.Headers.TryAddWithoutValidation("X-RateLimit-Remaining", "0");
                response.Content = new StringContent("{\"message\":\"rate limit\"}");
                return response;
            }

            return Hub.Json(HttpStatusCode.OK, "{\"login\":\"octocat\"}");
        });
        using var client = new GitHubClient(hub, null, new GitHubClientOptions
        {
            Delay = (wait, _) =>
            {
                waits.Add(wait);
                return Task.CompletedTask;
            },
            MaxSingleWait = TimeSpan.FromSeconds(20),
            MaxTotalWait = TimeSpan.FromSeconds(45)
        });
        string? phase = null;
        client.RateLimited += wait => phase = "Rate limit " + wait.TotalSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var result = await client.GetJsonAsync(GitHubUrls.User("octocat"), CancellationToken.None);
        Assert.True(result.IsSuccess);
        Assert.Equal(TimeSpan.FromSeconds(20), Assert.Single(waits));
        Assert.Equal("Rate limit 20", phase);
    }

    [Fact]
    public async Task Lists_follow_the_next_link_past_the_first_page()
    {
        var hub = new Hub(request =>
        {
            if (request.RequestUri!.Query.Contains("page=2", StringComparison.Ordinal))
            {
                return Hub.Json(HttpStatusCode.OK, "[{\"id\":2}]");
            }

            var response = Hub.Json(HttpStatusCode.OK, "[{\"id\":1}]");
            response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/gists?per_page=100&page=2>; rel=\"next\"");
            return response;
        });
        using var client = new GitHubClient(hub, null);
        var items = await client.GetAllAsync(GitHubUrls.UserGists("octocat"), CancellationToken.None);
        Assert.Equal(2, items.Count);
        Assert.Contains("per_page=100", hub.Seen[0].Query, StringComparison.Ordinal);
    }

    [Fact]
    public void Content_urls_encode_each_path_segment()
    {
        var uri = GitHubUrls.Contents("octocat", "Hello-World", "docs/my file#.md", "feature/a");
        Assert.Contains("/contents/docs/my%20file%23.md", uri.AbsolutePath, StringComparison.Ordinal);
        Assert.Contains("ref=feature%2Fa", uri.Query, StringComparison.Ordinal);
        Assert.DoesNotContain("my file", uri.AbsoluteUri, StringComparison.Ordinal);
    }
}

public class BackupEngineTests
{
    [Fact]
    public async Task Error_pages_are_not_stored_and_counts_match_the_files()
    {
        var hub = Baseline(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/git/blobs/ok", StringComparison.Ordinal))
            {
                return Hub.Bytes(HttpStatusCode.OK, "hello"u8.ToArray());
            }

            if (path.EndsWith("/git/blobs/bad", StringComparison.Ordinal) || path.Contains("/contents/", StringComparison.Ordinal))
            {
                return Hub.Json(HttpStatusCode.InternalServerError, "{\"message\":\"Not Found\",\"content\":\"<html>nope</html>\"}");
            }

            if (path.Contains("/git/trees/", StringComparison.Ordinal))
            {
                return Hub.Json(HttpStatusCode.OK, """
                    {"truncated":false,"tree":[
                      {"path":"ok.txt","mode":"100644","type":"blob","sha":"ok","size":5},
                      {"path":"bad.txt","mode":"100644","type":"blob","sha":"bad","size":4}
                    ]}
                    """);
            }

            return null;
        });

        using var temp = new TempVault();
        using var engine = new BackupEngine(hub, null);
        var progress = new List<BackupProgress>();
        var result = await engine.RunAsync(new BackupRequest { Target = "octocat/Hello", VaultRoot = temp.Path, Incremental = false }, new Progress<BackupProgress>(progress.Add), CancellationToken.None);

        var file = Path.Combine(temp.Path, "profiles", "octocat", "repos", "Hello", "branches", "main", "files", "ok.txt");
        Assert.True(File.Exists(file));
        Assert.Equal("hello", await File.ReadAllTextAsync(file));
        Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(file)!, "bad.txt")));
        foreach (var storedFile in Directory.GetFiles(temp.Path, "*", SearchOption.AllDirectories))
        {
            Assert.DoesNotContain("Not Found", File.ReadAllText(storedFile), StringComparison.Ordinal);
        }
        Assert.Equal(1, result.Failed);
        Assert.Equal(2, result.Saved);
        Assert.Contains(progress, item => item.Detail == "ok.txt");
    }

    [Fact]
    public async Task Incremental_backup_skips_an_unchanged_sha()
    {
        Hub Blob() => Baseline(request =>
            request.RequestUri!.AbsolutePath.EndsWith("/git/blobs/ok", StringComparison.Ordinal)
                ? Hub.Bytes(HttpStatusCode.OK, "hello"u8.ToArray())
                : null);

        using var temp = new TempVault();
        using (var first = new BackupEngine(Blob(), null))
        {
            var result = await first.RunAsync(new BackupRequest { Target = "octocat/Hello", VaultRoot = temp.Path }, null, CancellationToken.None);
            Assert.Equal(0, result.Failed);
            Assert.True(result.Saved >= 1);
        }

        var secondHub = Baseline(request =>
            request.RequestUri!.AbsolutePath.Contains("/git/blobs/", StringComparison.Ordinal)
                ? Hub.Json(HttpStatusCode.InternalServerError, "{\"message\":\"should not download\"}")
                : null);
        using var second = new BackupEngine(secondHub, null);
        var again = await second.RunAsync(new BackupRequest { Target = "octocat/Hello", VaultRoot = temp.Path, Incremental = true }, null, CancellationToken.None);
        Assert.Equal(0, again.Failed);
        Assert.True(again.Skipped >= 1);
        Assert.DoesNotContain(secondHub.Seen, item => item.Path.Contains("/git/blobs/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Slash_branches_invalid_names_long_paths_and_encoded_fallback_are_safe()
    {
        var longPath = string.Join('/', Enumerable.Repeat("directory", 30)) + "/file.txt";
        var hub = Baseline(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            var query = request.RequestUri.Query;
            if (path.Contains("/git/trees/", StringComparison.Ordinal))
            {
                return Hub.Json(HttpStatusCode.OK, """
                    {"truncated":false,"tree":[
                      {"path":"README.md","mode":"100644","type":"blob","sha":"readme","size":3},
                      {"path":"CON.txt","mode":"100644","type":"blob","sha":"con","size":3},
                      {"path":"a:b?.md","mode":"100644","type":"blob","sha":"odd","size":3},
                      {"path":"../escape.txt","mode":"100644","type":"blob","sha":"esc","size":3},
                      {"path":"LONG","mode":"100644","type":"blob","sha":"long","size":4}
                    ]}
                    """.Replace("LONG", longPath, StringComparison.Ordinal));
            }

            if (path.EndsWith("/git/blobs/readme", StringComparison.Ordinal))
            {
                return Hub.Bytes(HttpStatusCode.NotFound, "missing"u8.ToArray());
            }

            if (path.Contains("/contents/", StringComparison.Ordinal) && query.Contains("ref=feature%2Fa", StringComparison.Ordinal))
            {
                Assert.Contains("/contents/README.md", path, StringComparison.Ordinal);
                return Hub.Bytes(HttpStatusCode.OK, "md"u8.ToArray());
            }

            if (path.EndsWith("/git/blobs/con", StringComparison.Ordinal))
            {
                return Hub.Bytes(HttpStatusCode.OK, "con"u8.ToArray());
            }

            if (path.EndsWith("/git/blobs/odd", StringComparison.Ordinal))
            {
                return Hub.Bytes(HttpStatusCode.OK, "odd"u8.ToArray());
            }

            if (path.EndsWith("/git/blobs/long", StringComparison.Ordinal))
            {
                return Hub.Bytes(HttpStatusCode.OK, "long"u8.ToArray());
            }

            return null;
        }, branch: "feature/a");

        using var temp = new TempVault();
        using var engine = new BackupEngine(hub, null);
        var result = await engine.RunAsync(new BackupRequest { Target = "octocat/Hello", VaultRoot = temp.Path, Incremental = false }, null, CancellationToken.None);
        var branch = Path.Combine(temp.Path, "profiles", "octocat", "repos", "Hello", "branches", "feature\u2215a");
        Assert.True(Directory.Exists(branch));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "profiles", "octocat", "repos", "Hello", "branches", "feature")));
        Assert.Equal("md", await File.ReadAllTextAsync(Path.Combine(branch, "files", "README.md")));
        Assert.True(File.Exists(Path.Combine(branch, "files", "_CON.txt")));
        Assert.True(File.Exists(Path.Combine(branch, "files", "a_b_.md")));
        Assert.False(File.Exists(Path.Combine(temp.Path, "escape.txt")));
        var stored = Directory.GetFiles(Path.Combine(branch, "files"), "file.txt", SearchOption.AllDirectories).Single();
        Assert.Contains($"{Path.DirectorySeparatorChar}_long{Path.DirectorySeparatorChar}", stored);
        Assert.Equal("long", await File.ReadAllTextAsync(stored));
        Assert.True(result.Failed >= 1);
        Assert.Contains(hub.Seen, item => item.Path.Contains("README.md", StringComparison.Ordinal) && item.Query.Contains("feature%2Fa", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Truncated_trees_are_walked_and_pages_pass_one_hundred()
    {
        var branches = "[" + string.Join(',', Enumerable.Range(0, 100).Select(i => "{\"name\":\"b" + i + "\",\"commit\":{\"sha\":\"s" + i + "\"}}")) + "]";
        var hub = Baseline(request =>
        {
            var uri = request.RequestUri!;
            if (uri.AbsolutePath.EndsWith("/branches", StringComparison.Ordinal))
            {
                if (uri.Query.Contains("page=2", StringComparison.Ordinal))
                {
                    return Hub.Json(HttpStatusCode.OK, "[{\"name\":\"final\",\"commit\":{\"sha\":\"finalsha\"}}]");
                }

                var response = Hub.Json(HttpStatusCode.OK, branches);
                response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/repos/octocat/Hello/branches?per_page=100&page=2>; rel=\"next\"");
                return response;
            }

            if (uri.AbsolutePath.Contains("/git/trees/finalsha", StringComparison.Ordinal) && uri.Query.Contains("recursive=1", StringComparison.Ordinal))
            {
                return Hub.Json(HttpStatusCode.OK, """{"truncated":true,"tree":[{"path":"a.txt","mode":"100644","type":"blob","sha":"decoy","size":1}]}""");
            }

            if (uri.AbsolutePath.EndsWith("/git/trees/finalsha", StringComparison.Ordinal))
            {
                return Hub.Json(HttpStatusCode.OK, """
                    {"tree":[
                      {"path":"b.txt","mode":"100644","type":"blob","sha":"bbb","size":1},
                      {"path":"sub","mode":"040000","type":"tree","sha":"subsha"}
                    ]}
                    """);
            }

            if (uri.AbsolutePath.EndsWith("/git/trees/subsha", StringComparison.Ordinal))
            {
                return Hub.Json(HttpStatusCode.OK, """{"tree":[{"path":"c.txt","mode":"100644","type":"blob","sha":"ccc","size":1}]}""");
            }

            if (uri.AbsolutePath.EndsWith("/git/blobs/bbb", StringComparison.Ordinal))
            {
                return Hub.Bytes(HttpStatusCode.OK, "B"u8.ToArray());
            }

            if (uri.AbsolutePath.EndsWith("/git/blobs/ccc", StringComparison.Ordinal))
            {
                return Hub.Bytes(HttpStatusCode.OK, "C"u8.ToArray());
            }

            if (uri.AbsolutePath.Contains("/git/trees/", StringComparison.Ordinal))
            {
                return Hub.Json(HttpStatusCode.OK, """{"truncated":false,"tree":[]}""");
            }

            if (uri.AbsolutePath.EndsWith("/releases", StringComparison.Ordinal))
            {
                if (uri.Query.Contains("page=2", StringComparison.Ordinal))
                {
                    return Hub.Json(HttpStatusCode.OK, """[{"tag_name":"v2","body":"second","assets":[]}]""");
                }

                var response = Hub.Json(HttpStatusCode.OK, """[{"tag_name":"v1","body":"first","assets":[{"id":9,"name":"note.txt"}]}]""");
                response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/repos/octocat/Hello/releases?per_page=100&page=2>; rel=\"next\"");
                return response;
            }

            if (uri.AbsolutePath.EndsWith("/releases/assets/9", StringComparison.Ordinal))
            {
                return Hub.Bytes(HttpStatusCode.OK, "asset"u8.ToArray());
            }

            if (uri.AbsolutePath.EndsWith("/gists", StringComparison.Ordinal))
            {
                if (uri.Query.Contains("page=2", StringComparison.Ordinal))
                {
                    return Hub.Json(HttpStatusCode.OK, """[{"id":"g2"}]""");
                }

                var response = Hub.Json(HttpStatusCode.OK, """[{"id":"g1"}]""");
                response.Headers.TryAddWithoutValidation("Link", "<https://api.github.com/users/octocat/gists?per_page=100&page=2>; rel=\"next\"");
                return response;
            }

            if (uri.AbsolutePath.EndsWith("/gists/g1", StringComparison.Ordinal))
            {
                return Hub.Json(HttpStatusCode.OK, """{"id":"g1","files":{"a.txt":{"filename":"a.txt","content":"gist-a","truncated":false}}}""");
            }

            if (uri.AbsolutePath.EndsWith("/gists/g2", StringComparison.Ordinal))
            {
                return Hub.Json(HttpStatusCode.OK, """{"id":"g2","files":{"b.txt":{"filename":"b.txt","content":"gist-b","truncated":false}}}""");
            }

            return null;
        });

        using var temp = new TempVault();
        using var engine = new BackupEngine(hub, null);
        var result = await engine.RunAsync(new BackupRequest { Target = "octocat", VaultRoot = temp.Path, Incremental = false }, null, CancellationToken.None);
        var branchFolders = Directory.GetDirectories(Path.Combine(temp.Path, "profiles", "octocat", "repos", "Hello", "branches"));
        Assert.Equal(101, branchFolders.Length);
        var finalFiles = Path.Combine(temp.Path, "profiles", "octocat", "repos", "Hello", "branches", "final", "files");
        Assert.Equal("B", await File.ReadAllTextAsync(Path.Combine(finalFiles, "b.txt")));
        Assert.Equal("C", await File.ReadAllTextAsync(Path.Combine(finalFiles, "sub", "c.txt")));
        Assert.False(File.Exists(Path.Combine(finalFiles, "a.txt")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "profiles", "octocat", "repos", "Hello", "releases", "v1", "notes.md")));
        Assert.True(File.Exists(Path.Combine(temp.Path, "profiles", "octocat", "repos", "Hello", "releases", "v2", "notes.md")));
        Assert.Equal("asset", await File.ReadAllTextAsync(Directory.GetFiles(Path.Combine(temp.Path, "profiles", "octocat", "repos", "Hello", "releases", "v1", "assets")).Single()));
        Assert.Equal("gist-a", await File.ReadAllTextAsync(Path.Combine(temp.Path, "profiles", "octocat", "gists", "g1", "files", "a.txt")));
        Assert.Equal("gist-b", await File.ReadAllTextAsync(Path.Combine(temp.Path, "profiles", "octocat", "gists", "g2", "files", "b.txt")));
        Assert.Equal(2, result.GistCount);
        Assert.True(result.Saved > 0);
    }

    [Fact]
    public async Task A_token_includes_the_owners_private_repositories()
    {
        var token = "github_pat_" + new string('e', 24);
        var hub = Baseline(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path == "/user")
            {
                Assert.Equal(token, request.Headers.Authorization?.Parameter);
                return Hub.Json(HttpStatusCode.OK, """{"login":"me","type":"User"}""");
            }

            if (path == "/user/repos")
            {
                Assert.Equal(token, request.Headers.Authorization?.Parameter);
                return Hub.Json(HttpStatusCode.OK, """
                    [
                      {"name":"secret","private":true,"owner":{"login":"me"},"description":"hidden","default_branch":"main"},
                      {"name":"foreign","private":true,"owner":{"login":"other"},"description":"nope","default_branch":"main"}
                    ]
                    """);
            }

            if (path.Contains("/repos/me/secret/branches", StringComparison.Ordinal))
            {
                return Hub.Json(HttpStatusCode.OK, """[{"name":"main","commit":{"sha":"abc"}}]""");
            }

            if (path.Contains("/git/trees/", StringComparison.Ordinal))
            {
                return Hub.Json(HttpStatusCode.OK, """{"truncated":false,"tree":[{"path":"README.md","mode":"100644","type":"blob","sha":"r","size":1}]}""");
            }

            if (path.EndsWith("/git/blobs/r", StringComparison.Ordinal))
            {
                return Hub.Bytes(HttpStatusCode.OK, "p"u8.ToArray());
            }

            return null;
        }, login: "me", repo: "secret");

        using var temp = new TempVault();
        using var engine = new BackupEngine(hub, token);
        var result = await engine.RunAsync(new BackupRequest { Target = "me", VaultRoot = temp.Path, Incremental = false }, null, CancellationToken.None);
        Assert.True(File.Exists(Path.Combine(temp.Path, "profiles", "me", "repos", "secret", "branches", "main", "files", "README.md")));
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "profiles", "me", "repos", "foreign")));
        Assert.Contains(hub.Seen, item => item.Path == "/user/repos");
        Assert.DoesNotContain(token, await File.ReadAllTextAsync(result.LogFile!));
        var avatarCalls = hub.Seen.Where(item => item.Host == "avatars.githubusercontent.com");
        Assert.All(avatarCalls, item => Assert.Null(item.Auth));
    }

    [Theory]
    [InlineData("octocat", "octocat", null)]
    [InlineData("octocat/Hello-World", "octocat", "Hello-World")]
    [InlineData("https://github.com/octocat/Hello-World.git", "octocat", "Hello-World")]
    [InlineData("https://evil.example/octocat", "", null)]
    [InlineData("../etc", "", null)]
    public void Targets_accept_a_user_or_a_repository(string input, string owner, string? repo)
    {
        var ok = ParsedTarget.TryParse(input, out var target, out _);
        if (owner.Length == 0)
        {
            Assert.False(ok);
            return;
        }

        Assert.True(ok);
        Assert.Equal(owner, target.Owner);
        Assert.Equal(repo, target.Repository);
    }

    private static Hub Baseline(Func<HttpRequestMessage, HttpResponseMessage?> route, string login = "octocat", string repo = "Hello", string branch = "main")
    {
        return new Hub(request =>
        {
            var custom = route(request);
            if (custom != null)
            {
                return custom;
            }

            var path = request.RequestUri!.AbsolutePath;
            if (path == "/users/" + login)
            {
                return Hub.Json(HttpStatusCode.OK, "{\"login\":\"" + login + "\",\"name\":\"The Octocat\",\"type\":\"User\",\"avatar_url\":\"https://avatars.githubusercontent.com/u/1\"}");
            }

            if (request.RequestUri.Host == "avatars.githubusercontent.com")
            {
                return Hub.Bytes(HttpStatusCode.OK, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]);
            }

            if (path == "/repos/" + login + "/" + repo)
            {
                return Hub.Json(HttpStatusCode.OK, "{\"name\":\"" + repo + "\",\"private\":false,\"owner\":{\"login\":\"" + login + "\"},\"description\":\"hi\",\"default_branch\":\"" + branch + "\",\"stargazers_count\":1,\"html_url\":\"https://github.com/" + login + "/" + repo + "\"}");
            }

            if (path == "/users/" + login + "/repos")
            {
                return Hub.Json(HttpStatusCode.OK, "[{\"name\":\"" + repo + "\",\"private\":false,\"owner\":{\"login\":\"" + login + "\"},\"description\":\"hi\",\"default_branch\":\"" + branch + "\"}]");
            }

            if (path.EndsWith("/languages", StringComparison.Ordinal))
            {
                return Hub.Json(HttpStatusCode.OK, "{}");
            }

            if (path.EndsWith("/branches", StringComparison.Ordinal))
            {
                return Hub.Json(HttpStatusCode.OK, "[{\"name\":\"" + branch + "\",\"commit\":{\"sha\":\"abc\"}}]");
            }

            if (path.Contains("/git/trees/", StringComparison.Ordinal))
            {
                return Hub.Json(HttpStatusCode.OK, "{\"truncated\":false,\"tree\":[{\"path\":\"README.md\",\"mode\":\"100644\",\"type\":\"blob\",\"sha\":\"ok\",\"size\":5}]}");
            }

            if (path.EndsWith("/releases", StringComparison.Ordinal) || path.EndsWith("/gists", StringComparison.Ordinal))
            {
                return Hub.Json(HttpStatusCode.OK, "[]");
            }

            return Hub.Json(HttpStatusCode.NotFound, "{\"message\":\"missing\"}");
        });
    }
}

public class VersionTests
{
    [Fact]
    public void Assembly_version_matches_directory_build_props()
    {
        var dir = AppContext.BaseDirectory;
        string? props = null;
        while (dir != null)
        {
            var candidate = Path.Combine(dir, "Directory.Build.props");
            if (File.Exists(candidate))
            {
                props = File.ReadAllText(candidate);
                break;
            }

            dir = Path.GetDirectoryName(dir);
        }

        Assert.NotNull(props);
        var version = System.Text.RegularExpressions.Regex.Match(props, "<Version>([^<]+)</Version>");
        var company = System.Text.RegularExpressions.Regex.Match(props, "<Company>([^<]+)</Company>");
        var product = System.Text.RegularExpressions.Regex.Match(props, "<Product>([^<]+)</Product>");
        Assert.Equal(version.Groups[1].Value.Trim(), AppVersion.Current);
        Assert.Equal(company.Groups[1].Value.Trim(), AppVersion.Company);
        Assert.Equal(product.Groups[1].Value.Trim(), AppVersion.Product);
        Assert.Equal("by " + AppVersion.Company, AppVersion.Credit);
    }
}

internal sealed class Hub : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _handle;

    public Hub(Func<HttpRequestMessage, HttpResponseMessage> handle) => _handle = handle;

    public List<(string Host, string Path, string Query, string? Auth)> Seen { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Seen.Add((request.RequestUri!.Host, request.RequestUri.AbsolutePath, request.RequestUri.Query, request.Headers.Authorization?.Parameter));
        return Task.FromResult(_handle(request));
    }

    public static HttpResponseMessage Json(HttpStatusCode status, string body)
    {
        return new HttpResponseMessage(status)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }

    public static HttpResponseMessage Bytes(HttpStatusCode status, byte[] body)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = new ByteArrayContent(body)
        };
        response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        return response;
    }
}

internal sealed class TempVault : IDisposable
{
    public TempVault()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gsh-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
