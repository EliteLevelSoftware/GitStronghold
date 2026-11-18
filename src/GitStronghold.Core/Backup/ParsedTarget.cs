using GitStronghold.Core.Paths;

namespace GitStronghold.Core.Backup;

public readonly record struct ParsedTarget(string Owner, string? Repository)
{
    public static bool TryParse(string? input, out ParsedTarget target, out string error)
    {
        target = default;
        error = "";
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "Enter a GitHub user, organization, or owner/repo.";
            return false;
        }

        var text = input.Trim();
        if (text.Contains('\\') || text.Contains("..", StringComparison.Ordinal))
        {
            error = "Enter a GitHub user, organization, or owner/repo.";
            return false;
        }

        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) || text.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase) || text.StartsWith("www.github.com/", StringComparison.OrdinalIgnoreCase))
        {
            if (uri is null && !text.Contains("://", StringComparison.Ordinal))
            {
                if (!Uri.TryCreate("https://" + text, UriKind.Absolute, out uri))
                {
                    error = "Enter a GitHub user, organization, or owner/repo.";
                    return false;
                }
            }

            if (uri is null
                || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                || (uri.Host is not "github.com" and not "www.github.com"))
            {
                error = "Only a github.com user, organization, or owner/repo is accepted.";
                return false;
            }

            text = uri.AbsolutePath.Trim('/');
        }

        text = text.Trim().Trim('/');
        if (text.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            text = text[..^4];
        }

        var parts = text.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is < 1 or > 2)
        {
            error = "Enter a GitHub user, organization, or owner/repo.";
            return false;
        }

        if (!VaultPath.IsOwnerName(parts[0]))
        {
            error = "That GitHub name is not valid.";
            return false;
        }

        if (parts.Length == 2 && !VaultPath.IsRepoName(parts[1]))
        {
            error = "That repository name is not valid.";
            return false;
        }

        target = new ParsedTarget(parts[0], parts.Length == 2 ? parts[1] : null);
        return true;
    }
}
