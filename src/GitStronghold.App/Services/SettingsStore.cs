using System.Text.Json;
using GitStronghold.Core;

namespace GitStronghold.App.Services;

public sealed class AppSettings
{
    public string VaultPath { get; set; } = "";

    public bool Incremental { get; set; } = true;

    public bool Notifications { get; set; } = true;

    public bool ReduceEffects { get; set; }

    public bool CheckUpdatesOnStartup { get; set; } = true;

    public string UpdateSource { get; set; } = "";

    public string UpdateChannel { get; set; } = "stable";
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string DirectoryPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), AppVersion.Product);

    public static string FilePath => Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        Directory.CreateDirectory(DirectoryPath);
        AppSettings settings;
        try
        {
            settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Json) ?? new AppSettings()
                : new AppSettings();
        }
        catch (JsonException)
        {
            settings = new AppSettings();
        }

        if (string.IsNullOrWhiteSpace(settings.VaultPath))
        {
            settings.VaultPath = Path.Combine(DirectoryPath, "vault");
        }

        Directory.CreateDirectory(settings.VaultPath);
        return settings;
    }

    public static void Save(AppSettings settings)
    {
        Directory.CreateDirectory(DirectoryPath);
        if (!string.IsNullOrWhiteSpace(settings.VaultPath))
        {
            Directory.CreateDirectory(settings.VaultPath);
        }

        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Json));
    }
}
