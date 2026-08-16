namespace Stanza.App.Services;

public sealed class RecentState
{
    public string? LastFile { get; set; }
    public List<string> RecentFiles { get; set; } = new();
}

/// <summary>最近文件列表的持久化（%APPDATA%/Stanza/recent.json）。</summary>
public static class RecentFilesStore
{
    public const int MaxRecent = 8;

    private static readonly string StorePath = JsonFileStore.PathFor("recent.json");

    public static RecentState Load() => JsonFileStore.Load<RecentState>(StorePath);

    public static void Save(RecentState state) => JsonFileStore.Save(StorePath, state);
}
