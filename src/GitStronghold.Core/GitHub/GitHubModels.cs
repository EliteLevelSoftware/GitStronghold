namespace GitStronghold.Core.GitHub;

public static class GitHubHosts
{
    public const string ApiHost = "api.github.com";

    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        "api.github.com",
        "github.com",
        "www.github.com",
        "avatars.githubusercontent.com",
        "raw.githubusercontent.com",
        "objects.githubusercontent.com",
        "release-assets.githubusercontent.com",
        "codeload.github.com",
        "gist.githubusercontent.com",
        "private-user-images.githubusercontent.com",
        "user-images.githubusercontent.com",
        "media.githubusercontent.com"
    };

    public static bool IsAllowed(string host) => Allowed.Contains(host);

    public static bool ReceivesToken(string host) =>
        host.Equals(ApiHost, StringComparison.OrdinalIgnoreCase);
}

public static class GitHubUrls
{
    public static Uri Api(string pathAndQuery)
    {
        if (!pathAndQuery.StartsWith('/'))
        {
            pathAndQuery = "/" + pathAndQuery;
        }

        return new Uri("https://api.github.com" + pathAndQuery);
    }

    public static Uri User(string owner) => Api("/users/" + Escape(owner));

    public static Uri AuthenticatedUser() => Api("/user");

    public static Uri UserRepos(string owner) =>
        Api("/users/" + Escape(owner) + "/repos?per_page=100&type=owner");

    public static Uri OrgRepos(string owner) =>
        Api("/orgs/" + Escape(owner) + "/repos?per_page=100&type=all");

    public static Uri AuthenticatedRepos() =>
        Api("/user/repos?per_page=100&visibility=all&affiliation=owner");

    public static Uri Repo(string owner, string repo) =>
        Api("/repos/" + Escape(owner) + "/" + Escape(repo));

    public static Uri Languages(string owner, string repo) =>
        Api("/repos/" + Escape(owner) + "/" + Escape(repo) + "/languages");

    public static Uri Branches(string owner, string repo) =>
        Api("/repos/" + Escape(owner) + "/" + Escape(repo) + "/branches?per_page=100");

    public static Uri Tree(string owner, string repo, string sha, bool recursive)
    {
        var path = "/repos/" + Escape(owner) + "/" + Escape(repo) + "/git/trees/" + Escape(sha);
        if (recursive)
        {
            path += "?recursive=1";
        }

        return Api(path);
    }

    public static Uri Blob(string owner, string repo, string sha) =>
        Api("/repos/" + Escape(owner) + "/" + Escape(repo) + "/git/blobs/" + Escape(sha));

    public static Uri Contents(string owner, string repo, string path, string gitRef)
    {
        var encodedPath = string.Join('/', path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Escape));
        return Api("/repos/" + Escape(owner) + "/" + Escape(repo) + "/contents/" + encodedPath + "?ref=" + Escape(gitRef));
    }

    public static Uri Releases(string owner, string repo) =>
        Api("/repos/" + Escape(owner) + "/" + Escape(repo) + "/releases?per_page=100");

    public static Uri ReleaseAsset(string owner, string repo, long id) =>
        Api("/repos/" + Escape(owner) + "/" + Escape(repo) + "/releases/assets/" + id.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static Uri UserGists(string owner) =>
        Api("/users/" + Escape(owner) + "/gists?per_page=100");

    public static Uri AuthenticatedGists() => Api("/gists?per_page=100");

    public static Uri Gist(string id) => Api("/gists/" + Escape(id));

    public static string Escape(string value) => Uri.EscapeDataString(value);
}

public sealed class GitHubClientOptions
{
    public int MaxRateLimitWaits { get; init; } = 3;

    public TimeSpan MaxSingleWait { get; init; } = TimeSpan.FromSeconds(20);

    public TimeSpan MaxTotalWait { get; init; } = TimeSpan.FromSeconds(45);

    public Func<TimeSpan, CancellationToken, Task>? Delay { get; init; }

    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

public sealed class GitHubResult
{
    public int Status { get; init; }

    public byte[] Bytes { get; init; } = [];

    public string? ContentType { get; init; }

    public string? LinkHeader { get; init; }

    public string? Error { get; init; }

    public bool IsSuccess => Status is >= 200 and < 300 && Error is null;

    public static GitHubResult Fail(string error) => new() { Status = 0, Error = error };
}

public sealed class GitHubCallException : Exception
{
    public GitHubCallException(int status, string message) : base(message)
    {
        Status = status;
    }

    public int Status { get; }
}
