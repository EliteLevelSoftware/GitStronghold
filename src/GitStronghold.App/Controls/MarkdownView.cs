using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using GitStronghold.App.Services;
using GitStronghold.Core.Paths;
using GitStronghold.Core.Security;

namespace GitStronghold.App.Controls;

/// <summary>
/// Renders sanitized markdown. Remote images are already gone, and a relative image
/// is opened only when the file sits inside the vault.
/// </summary>
public class MarkdownView : StackPanel
{
    public static readonly StyledProperty<string?> MarkdownProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Markdown));

    public static readonly StyledProperty<string?> VaultProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(Vault));

    public static readonly StyledProperty<string?> BaseDirectoryProperty =
        AvaloniaProperty.Register<MarkdownView, string?>(nameof(BaseDirectory));

    public event EventHandler<string>? NavigateRelative;

    public string? Markdown
    {
        get => GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    public string? Vault
    {
        get => GetValue(VaultProperty);
        set => SetValue(VaultProperty, value);
    }

    public string? BaseDirectory
    {
        get => GetValue(BaseDirectoryProperty);
        set => SetValue(BaseDirectoryProperty, value);
    }

    public MarkdownView()
    {
        Orientation = Orientation.Vertical;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Spacing = Number("ElSpace3");
        Rebuild();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == MarkdownProperty || change.Property == VaultProperty || change.Property == BaseDirectoryProperty)
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        Children.Clear();
        if (string.IsNullOrEmpty(Markdown) || VisualRoot is null)
        {
            return;
        }

        var body = Brush("ElText2Brush");
        var heading = Brush("ElTextBrush");
        var muted = Brush("ElTextMutedBrush");
        var lines = MarkdownSanitizer.Sanitize(Markdown).Split('\n');
        var inCode = false;
        var code = new List<string>();
        foreach (var raw in lines)
        {
            var line = raw.TrimEnd();
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCode)
                {
                    Children.Add(CodeBlock(string.Join('\n', code)));
                    code.Clear();
                }

                inCode = !inCode;
                continue;
            }

            if (inCode)
            {
                code.Add(line);
                continue;
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith("### ", StringComparison.Ordinal))
            {
                Children.Add(Text(line[4..], Number("ElTypeSectionSize"), FontWeight.SemiBold, heading));
            }
            else if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                Children.Add(Text(line[3..], Number("ElTypeSectionSize"), FontWeight.Bold, heading));
            }
            else if (line.StartsWith("# ", StringComparison.Ordinal))
            {
                Children.Add(Text(line[2..], Number("ElTypeFeatureSize"), FontWeight.Bold, heading));
            }
            else if (TryImage(line, out var alt, out var url))
            {
                AddImage(alt, url, muted);
            }
            else if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal))
            {
                AddRich("• " + line[2..], body);
            }
            else
            {
                AddRich(line, body);
            }
        }

        if (code.Count > 0)
        {
            Children.Add(CodeBlock(string.Join('\n', code)));
        }
    }

    private void AddRich(string line, IBrush brush)
    {
        var panel = new WrapPanel { Orientation = Orientation.Horizontal };
        var rest = line;
        while (rest.Length > 0)
        {
            var start = rest.IndexOf('[', StringComparison.Ordinal);
            var endText = start < 0 ? -1 : rest.IndexOf(']', start);
            var open = endText < 0 ? -1 : rest.IndexOf('(', endText);
            var close = open < 0 ? -1 : rest.IndexOf(')', open);
            if (start < 0 || endText < 0 || open != endText + 1 || close < 0)
            {
                panel.Children.Add(Text(rest, Number("ElTypeBodySize"), FontWeight.Normal, brush));
                break;
            }

            if (start > 0)
            {
                panel.Children.Add(Text(rest[..start], Number("ElTypeBodySize"), FontWeight.Normal, brush));
            }

            var label = rest[(start + 1)..endText];
            var url = rest[(open + 1)..close];
            var button = new Button { Content = label, Classes = { "el-link" } };
            var target = url;
            button.Click += (_, _) => Open(target);
            panel.Children.Add(button);
            rest = rest[(close + 1)..];
        }

        Children.Add(panel);
    }

    private void AddImage(string alt, string url, IBrush muted)
    {
        var local = ResolveLocal(url);
        if (local is null)
        {
            Children.Add(Text(string.IsNullOrWhiteSpace(alt) ? "Image omitted" : alt, Number("ElTypeSmallSize"), FontWeight.Normal, muted));
            return;
        }

        try
        {
            Children.Add(new Image
            {
                Source = new Avalonia.Media.Imaging.Bitmap(local),
                MaxHeight = Number("ElSizePreview"),
                Stretch = Stretch.Uniform
            });
        }
        catch (Exception)
        {
            Children.Add(Text(alt, Number("ElTypeSmallSize"), FontWeight.Normal, muted));
        }
    }

    private void Open(string url)
    {
        if (ExternalUrl.IsSafeHttp(url, out _))
        {
            SystemLauncher.OpenHttp(url);
            return;
        }

        var local = ResolveLocal(url);
        if (local != null)
        {
            NavigateRelative?.Invoke(this, local);
        }
    }

    private string? ResolveLocal(string url)
    {
        if (string.IsNullOrWhiteSpace(Vault) || string.IsNullOrWhiteSpace(BaseDirectory) || ExternalUrl.IsSafeHttp(url, out _))
        {
            return null;
        }

        if (url.Contains("://", StringComparison.Ordinal) || url.StartsWith("//", StringComparison.Ordinal))
        {
            return null;
        }

        try
        {
            var full = Path.GetFullPath(Path.Combine(BaseDirectory, url.Replace('/', Path.DirectorySeparatorChar)));
            VaultPath.EnsureInside(Vault, full);
            return File.Exists(full) ? full : null;
        }
        catch (VaultException)
        {
            return null;
        }
    }

    private static bool TryImage(string line, out string alt, out string url)
    {
        alt = "";
        url = "";
        if (!line.StartsWith("![", StringComparison.Ordinal))
        {
            return false;
        }

        var end = line.IndexOf(']', 2);
        var open = end < 0 ? -1 : line.IndexOf('(', end);
        var close = open < 0 ? -1 : line.IndexOf(')', open);
        if (end < 0 || open < 0 || close < 0)
        {
            return false;
        }

        alt = line[2..end];
        url = line[(open + 1)..close];
        return true;
    }

    private TextBlock Text(string value, double size, FontWeight weight, IBrush brush) => new()
    {
        Text = value,
        FontSize = size,
        FontWeight = weight,
        Foreground = brush,
        TextWrapping = TextWrapping.Wrap,
        FontFamily = (FontFamily)(this.FindResource("ElFontUi") ?? FontFamily.Default)
    };

    private Border CodeBlock(string value) => new()
    {
        Background = Brush("ElCharcoal950Brush"),
        CornerRadius = (CornerRadius)(this.FindResource("ElRadiusControl") ?? new CornerRadius(0)),
        Padding = (Thickness)(this.FindResource("ElCardPadding") ?? new Thickness(0)),
        Child = new TextBlock
        {
            Text = value,
            FontFamily = (FontFamily)(this.FindResource("ElFontMono") ?? FontFamily.Default),
            FontSize = Number("ElTypeMonoSize"),
            Foreground = Brush("ElTextBrush"),
            TextWrapping = TextWrapping.Wrap
        }
    };

    private double Number(string key) => this.FindResource(key) is double value ? value : 0;

    private IBrush Brush(string key) => this.FindResource(key) as IBrush ?? Brushes.Transparent;
}
