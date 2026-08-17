namespace Stanza.App.Services;

public sealed class AppSettings
{
    public string Language { get; set; } = Loc.DefaultLanguage;

    /// <summary>macOS 键盘模式：Alt 扮演 Command（应用快捷键与文本复制键整体迁到 Alt），
    /// Ctrl 留给文本编辑移动键（Emacs 绑定）。false = Windows 模式（文本编辑键在 Alt 上）。</summary>
    public bool MacOsMode { get; set; }
}

/// <summary>应用设置的持久化（%APPDATA%/Stanza/settings.json）。</summary>
public static class SettingsStore
{
    private static readonly string StorePath = JsonFileStore.PathFor("settings.json");

    public static AppSettings Load() => JsonFileStore.Load<AppSettings>(StorePath);

    public static void Save(AppSettings settings) => JsonFileStore.Save(StorePath, settings);
}
