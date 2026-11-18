using System.Text.RegularExpressions;

namespace GitStronghold.Core.Security;

/// <summary>
/// Turns untrusted markdown into text the offline viewer can render.
/// Remote images and active content are removed. Relative images stay, and the
/// viewer still has to resolve them inside the vault before opening a file.
/// </summary>
public static partial class MarkdownSanitizer
{
    public static string Sanitize(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return "";
        }

        var text = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        text = DangerousBlock().Replace(text, "");
        text = DangerousOpen().Replace(text, "");
        text = HtmlComment().Replace(text, "");
        var remoteRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        text = ReferenceDefinition().Replace(text, match =>
        {
            var id = match.Groups[1].Value;
            var url = match.Groups[2].Value;
            if (IsRemote(url))
            {
                remoteRefs.Add(id);
                return "";
            }

            return match.Value;
        });
        text = MarkdownImage().Replace(text, match => DescribeImage(match.Groups[1].Value, match.Groups[2].Value));
        text = ReferenceImage().Replace(text, match =>
        {
            var alt = match.Groups[1].Value;
            var id = match.Groups[2].Value;
            if (remoteRefs.Contains(id))
            {
                return string.IsNullOrWhiteSpace(alt) ? "Image omitted" : alt;
            }

            return match.Value;
        });
        text = MarkdownLink().Replace(text, match => DescribeLink(match.Groups[1].Value, match.Groups[2].Value));
        text = HtmlTag().Replace(text, "");
        return text.Trim();
    }

    private static string DescribeImage(string alt, string url)
    {
        if (!IsRemote(url))
        {
            return "![" + alt + "](" + url + ")";
        }

        return string.IsNullOrWhiteSpace(alt) ? "Image omitted" : alt;
    }

    private static string DescribeLink(string text, string url)
    {
        if (IsHttp(url) || IsRelative(url))
        {
            return "[" + text + "](" + url + ")";
        }

        return text;
    }

    public static bool IsRemote(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return true;
        }

        var trimmed = url.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal)
            || trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("vbscript:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IsHttp(trimmed) || trimmed.Contains("://", StringComparison.Ordinal))
        {
            return true;
        }

        return false;
    }

    public static bool IsHttp(string url)
    {
        return url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("http://", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsRelative(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || IsRemote(url) || IsHttp(url))
        {
            return false;
        }

        if (url.StartsWith('#') || url.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !url.Contains("://", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"<\s*(script|style|iframe|object|embed|svg|math|noscript)\b[^>]*>.*?<\s*/\s*\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DangerousBlock();

    [GeneratedRegex(@"<\s*(script|style|iframe|object|embed|svg|math|noscript)\b[^>]*>.*", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex DangerousOpen();

    [GeneratedRegex(@"<!--.*?-->", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex HtmlComment();

    [GeneratedRegex(@"^[ \t]*\[([^\]]+)\]:[ \t]*(\S+)[^\n]*$", RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceDefinition();

    [GeneratedRegex(@"!\[([^\]]*)\]\(\s*([^)\s]+)(?:\s+(?:""[^""]*""|'[^']*'))?\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownImage();

    [GeneratedRegex(@"!\[([^\]]*)\]\[([^\]]*)\]", RegexOptions.CultureInvariant)]
    private static partial Regex ReferenceImage();

    [GeneratedRegex(@"(?<!!)\[([^\]]+)\]\(\s*([^)\s]+)(?:\s+(?:""[^""]*""|'[^']*'))?\s*\)", RegexOptions.CultureInvariant)]
    private static partial Regex MarkdownLink();

    [GeneratedRegex(@"<[^>\n]+>", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlTag();
}
