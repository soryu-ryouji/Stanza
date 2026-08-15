using System.IO;
using System.Text.Json;

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

    private static string StorePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Stanza", "recent.json");

    public static RecentState Load()
    {
        try
        {
            if (File.Exists(StorePath))
            {
                return JsonSerializer.Deserialize<RecentState>(File.ReadAllText(StorePath))
                    ?? new RecentState();
            }
        }
        catch
        {
            // 配置损坏时从空列表开始，不影响主流程
        }
        return new RecentState();
    }

    public static void Save(RecentState state)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(state));
        }
        catch
        {
            // 配置写不进不影响主流程
        }
    }
}
