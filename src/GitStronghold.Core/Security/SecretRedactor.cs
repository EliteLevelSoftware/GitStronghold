using System.Text.RegularExpressions;

namespace GitStronghold.Core.Security;

public static partial class SecretRedactor
{
    public static string Redact(string? text, string? token = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return "";
        }

        var result = text;
        if (!string.IsNullOrEmpty(token) && token.Length >= 6)
        {
            result = result.Replace(token, "[redacted]", StringComparison.Ordinal);
        }

        result = TokenLike().Replace(result, "[redacted]");
        result = Authorization().Replace(result, "$1[redacted]");
        return result;
    }

    [GeneratedRegex(@"\b(?:ghp|gho|ghu|ghs|ghr)_[A-Za-z0-9]{8,}\b|github_pat_[A-Za-z0-9_]{8,}", RegexOptions.CultureInvariant)]
    private static partial Regex TokenLike();

    [GeneratedRegex(@"(?i)(authorization:\s*(?:bearer|token)\s+)\S+")]
    private static partial Regex Authorization();
}
