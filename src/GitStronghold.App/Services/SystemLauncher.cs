using System.Diagnostics;
using GitStronghold.Core.Paths;
using GitStronghold.Core.Security;

namespace GitStronghold.App.Services;

public static class SystemLauncher
{
    public static bool OpenHttp(string? url)
    {
        if (!ExternalUrl.IsSafeHttp(url, out var uri) || uri is null)
        {
            return false;
        }

        Start(uri.AbsoluteUri);
        return true;
    }

    public static void OpenVaultFile(string vault, string path)
    {
        VaultPath.EnsureInside(vault, path);
        if (!File.Exists(path))
        {
            return;
        }

        Start(path);
    }

    public static void Notify(string title, string message)
    {
        try
        {
            if (OperatingSystem.IsLinux() && File.Exists("/usr/bin/notify-send"))
            {
                var notify = new ProcessStartInfo("notify-send") { UseShellExecute = false, CreateNoWindow = true };
                notify.ArgumentList.Add(title);
                notify.ArgumentList.Add(message);
                Process.Start(notify);
            }
            else if (OperatingSystem.IsMacOS())
            {
                Process.Start("osascript", ["-e", "display notification \"" + message.Replace("\"", "") + "\" with title \"" + title.Replace("\"", "") + "\""]);
            }
        }
        catch (Exception)
        {
            // A missing notifier does not affect the backup.
        }
    }

    private static void Start(string target)
    {
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }
}
