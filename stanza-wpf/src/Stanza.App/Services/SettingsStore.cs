using System.IO;
using System.Text.Json;

namespace Stanza.App.Services;

public sealed class AppSettings
{
    public string Language { get; set; } = Loc.DefaultLanguage;
}

/// <summary>应用设置的持久化（%APPDATA%/Stanza/settings.json）。</summary>
public static class SettingsStore
{
    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Stanza", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(StorePath))
                    ?? new AppSettings();
            }
        }
        catch
        {
            // 配置损坏时用默认设置，不影响主流程
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(settings));
        }
        catch
        {
            // 配置写不进不影响主流程
        }
    }
}
