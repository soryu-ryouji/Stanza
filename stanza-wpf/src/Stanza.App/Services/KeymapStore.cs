namespace Stanza.App.Services;

/// <summary>用户键位文件中的一条：某命令（可带参数）的完整键位集合。</summary>
public sealed class UserCommandBinding
{
    public string Command { get; set; } = "";
    public string? Args { get; set; }
    public List<string> Keys { get; set; } = new();
}

/// <summary>
/// 用户自定义键位的持久化（%APPDATA%/Stanza/keymap.json）。
/// 语义同 VS Code：文件中出现的命令整体替换其默认键位（空列表 = 不绑定）；
/// 未出现的命令沿用默认。解析失败/未知命令/非法键位在 Load 时丢弃。
/// </summary>
public static class KeymapStore
{
    private static readonly string StorePath = JsonFileStore.PathFor("keymap.json");

    public static List<UserCommandBinding> Load() => JsonFileStore.Load<List<UserCommandBinding>>(StorePath);

    public static void Save(List<UserCommandBinding> bindings) => JsonFileStore.Save(StorePath, bindings);
}
