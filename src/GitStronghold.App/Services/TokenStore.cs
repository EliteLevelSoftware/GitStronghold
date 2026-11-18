using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace GitStronghold.App.Services;

/// <summary>
/// The GitHub token is stored with the operating system secret store.
/// Windows uses DPAPI, macOS uses the login keychain, and Linux uses libsecret.
/// The token is never written to settings or to a log.
/// </summary>
public static class TokenStore
{
    private const string Service = "com.elitelevel.gitstronghold";
    private const string Account = "github";

    public static bool TrySave(string token, out string error)
    {
        error = "";
        if (string.IsNullOrWhiteSpace(token) || token.Contains('\r') || token.Contains('\n'))
        {
            error = "Enter a token with no line breaks.";
            return false;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                SaveWindows(token.Trim());
                return true;
            }

            if (OperatingSystem.IsMacOS())
            {
                return MacKeychain.TrySave(token.Trim(), out error);
            }

            return LinuxSecret.TrySave(token.Trim(), out error);
        }
        catch (Exception)
        {
            error = "The token could not be saved in the system secret store.";
            return false;
        }
    }

    public static string? Load()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return LoadWindows();
            }

            if (OperatingSystem.IsMacOS())
            {
                return MacKeychain.Load();
            }

            return LinuxSecret.Load();
        }
        catch (Exception)
        {
            return null;
        }
    }

    public static void Clear()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var path = WindowsPath();
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return;
            }

            if (OperatingSystem.IsMacOS())
            {
                MacKeychain.Clear();
                return;
            }

            LinuxSecret.Clear();
        }
        catch (Exception)
        {
            // Clearing a missing secret is not a backup failure.
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static void SaveWindows(string token)
    {
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser);
        var path = WindowsPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, protectedBytes);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? LoadWindows()
    {
        var path = WindowsPath();
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), null, DataProtectionScope.CurrentUser);
        return Encoding.UTF8.GetString(bytes);
    }

    private static string WindowsPath() => Path.Combine(SettingsStore.DirectoryPath, "token.bin");

    private static class LinuxSecret
    {
        public static bool TrySave(string token, out string error)
        {
            error = "";
            if (!Exists())
            {
                error = "libsecret (secret-tool) is not available, so the token was not stored.";
                return false;
            }

            var (code, _) = Run(stdin: token, "store", "--label=GitStronghold GitHub token", "service", Service, "account", Account);
            if (code != 0)
            {
                error = "libsecret could not store the token.";
                return false;
            }

            return true;
        }

        public static string? Load()
        {
            if (!Exists())
            {
                return null;
            }

            var (code, output) = Run(stdin: null, "lookup", "service", Service, "account", Account);
            return code == 0 ? output.Trim() : null;
        }

        public static void Clear()
        {
            if (Exists())
            {
                Run(stdin: null, "clear", "service", Service, "account", Account);
            }
        }

        private static bool Exists() => File.Exists("/usr/bin/secret-tool");

        private static (int Code, string Output) Run(string? stdin, params string[] arguments)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo("secret-tool")
                {
                    RedirectStandardInput = stdin != null,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();
            if (stdin != null)
            {
                process.StandardInput.Write(stdin);
                process.StandardInput.Close();
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return (process.ExitCode, output);
        }
    }

    private static class MacKeychain
    {
        private const int ItemNotFound = -25300;

        public static bool TrySave(string token, out string error)
        {
            error = "";
            Clear();
            var bytes = Encoding.UTF8.GetBytes(token);
            var status = SecKeychainAddGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)Account.Length, Account, (uint)bytes.Length, bytes, out _);
            if (status != 0)
            {
                error = "The macOS keychain could not store the token.";
                return false;
            }

            return true;
        }

        public static string? Load()
        {
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)Account.Length, Account, out var length, out var data, out var item);
            if (status == ItemNotFound || data == IntPtr.Zero)
            {
                return null;
            }

            if (status != 0)
            {
                return null;
            }

            try
            {
                var bytes = new byte[length];
                Marshal.Copy(data, bytes, 0, (int)length);
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
                if (item != IntPtr.Zero)
                {
                    CoreFoundationRelease(item);
                }
            }
        }

        public static void Clear()
        {
            var status = SecKeychainFindGenericPassword(IntPtr.Zero, (uint)Service.Length, Service, (uint)Account.Length, Account, out _, out var data, out var item);
            if (status != 0 || item == IntPtr.Zero)
            {
                return;
            }

            SecKeychainItemDelete(item);
            if (data != IntPtr.Zero)
            {
                SecKeychainItemFreeContent(IntPtr.Zero, data);
            }

            CoreFoundationRelease(item);
        }

        [DllImport("/System/Library/Frameworks/Security.framework/Security")]
        private static extern int SecKeychainAddGenericPassword(IntPtr keychain, uint serviceLength, string service, uint accountLength, string account, uint passwordLength, byte[] password, out IntPtr item);

        [DllImport("/System/Library/Frameworks/Security.framework/Security")]
        private static extern int SecKeychainFindGenericPassword(IntPtr keychain, uint serviceLength, string service, uint accountLength, string account, out uint passwordLength, out IntPtr passwordData, out IntPtr item);

        [DllImport("/System/Library/Frameworks/Security.framework/Security")]
        private static extern int SecKeychainItemDelete(IntPtr item);

        [DllImport("/System/Library/Frameworks/Security.framework/Security")]
        private static extern int SecKeychainItemFreeContent(IntPtr attributeList, IntPtr data);

        [DllImport("/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation", EntryPoint = "CFRelease")]
        private static extern void CoreFoundationRelease(IntPtr reference);
    }
}
