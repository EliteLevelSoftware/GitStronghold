using System.Runtime.InteropServices;
using GitStronghold.Core.Security;
using Velopack;
using Velopack.Sources;

namespace GitStronghold.App.Services;

public static class UpdateFeed
{
    public const string DefaultRepository = "https://github.com/EliteLevelSoftware/GitStronghold";

    public const string FineGrainedTokenUrl = "https://github.com/settings/personal-access-tokens/new";

    public static string RuntimeId()
    {
        if (OperatingSystem.IsWindows())
        {
            return "win-x64";
        }

        if (OperatingSystem.IsMacOS())
        {
            return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "osx-arm64" : "osx-x64";
        }

        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "linux-arm64" : "linux-x64";
    }

    public static string ChannelName(string? track) =>
        RuntimeId() + "-" + (track == "prerelease" ? "prerelease" : "stable");

    public static string RepositoryUrl(string? configured)
    {
        var value = configured?.Trim() ?? "";
        if (value.Length == 0)
        {
            return DefaultRepository;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            return DefaultRepository;
        }

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return DefaultRepository;
        }

        var repo = parts[1];
        if (repo.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            repo = repo[..^4];
        }

        return "https://github.com/" + parts[0] + "/" + repo;
    }

    public static UpdateManager CreateManager(AppSettings settings)
    {
        var prerelease = settings.UpdateChannel == "prerelease";
        var source = new GithubSource(RepositoryUrl(settings.UpdateSource), null, prerelease);
        return new UpdateManager(source, new UpdateOptions
        {
            ExplicitChannel = ChannelName(prerelease ? "prerelease" : "stable"),
            AllowVersionDowngrade = false
        });
    }

    public static async Task<string> CheckAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        try
        {
            var manager = CreateManager(settings);
            if (!manager.IsInstalled)
            {
                return "Updates stay off until GitStronghold is installed from a release. This copy can still back up profiles.";
            }

            var update = await manager.CheckForUpdatesAsync().WaitAsync(cancellationToken);
            if (update is null)
            {
                return AppVersionText() + " is up to date.";
            }

            return "Update " + update.TargetFullRelease.Version + " is available. Restart from a release build to install it.";
        }
        catch (Exception ex)
        {
            return "Updates are unavailable. " + PublicError(ex);
        }
    }

    private static string AppVersionText() => GitStronghold.Core.AppVersion.ProductVersion;

    public static string PublicError(Exception exception)
    {
        var message = exception.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        message = SecretRedactor.Redact(message);
        return message.Length > 180 ? message[..180] : message;
    }
}
