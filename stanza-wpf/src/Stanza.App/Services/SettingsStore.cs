namespace Stanza.App.Services;

public sealed class AppSettings
{
    public string Language { get; set; } = Loc.DefaultLanguage;
}

/// <summary>应用设置的持久化（%APPDATA%/Stanza/settings.json）。</summary>
public static class SettingsStore
{
    private static readonly string StorePath = JsonFileStore.PathFor("settings.json");

    public static AppSettings Load() => JsonFileStore.Load<AppSettings>(StorePath);

    public static void Save(AppSettings settings) => JsonFileStore.Save(StorePath, settings);
}
