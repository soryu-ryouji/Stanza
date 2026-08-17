using System.Windows.Input;
using Stanza.App.Services;

namespace Stanza.App;

/// <summary>键位表中可分发的命令 ID。作为键位数据的稳定标识，供用户键位文件引用。
/// 分两类：应用级（全局分发）与任务作用域（仅任务列表焦点上下文分发，见 IsTaskScoped）。</summary>
public enum AppCommand
{
    // ---- 应用级 ----
    Save,
    Open,
    NewTask,
    NewDocument,
    SelectBlock,
    OpenRecent,
    Undo,

    // ---- 任务作用域 ----
    CompleteTask,
    OpenTagPicker,
    OpenProjectPicker,
    OpenMovePicker,
    DiscardTask,
    DeleteTask,
    NavigateUp,
    NavigateDown,
    NavigateLeft,
    NavigateRight,
}

/// <summary>一条键位映射：修饰键 + 主键 → 命令（可带参数）。同一命令可有多条（一键多绑）。</summary>
public sealed record KeymapEntry(ModifierKeys Modifiers, Key Key, AppCommand Command, object? Parameter = null);

/// <summary>键位手势与字符串的互转（"Ctrl+Shift+N"）。键名保持英文（Windows 平台惯例）。</summary>
public static class Gesture
{
    private static readonly Dictionary<string, Key> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = Key.Space,
        ["Tab"] = Key.Tab,
        ["Enter"] = Key.Enter,
        ["Esc"] = Key.Escape,
        ["Backspace"] = Key.Back,
        ["Delete"] = Key.Delete,
        ["Insert"] = Key.Insert,
        ["Home"] = Key.Home,
        ["End"] = Key.End,
        ["PageUp"] = Key.PageUp,
        ["PageDown"] = Key.PageDown,
        ["Up"] = Key.Up,
        ["Down"] = Key.Down,
        ["Left"] = Key.Left,
        ["Right"] = Key.Right,
    };

    /// <summary>格式化为显示/存储字符串："Ctrl+Shift+1"。数字键 D0-D9 显示为 0-9。</summary>
    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>(5);
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(KeyName(key));
        return string.Join('+', parts);
    }

    /// <summary>解析显示/存储字符串；无法识别时返回 false。</summary>
    public static bool TryParse(string text, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0) return false;
        foreach (var part in parts.Take(parts.Length - 1))
        {
            switch (part.ToLowerInvariant())
            {
                case "ctrl": modifiers |= ModifierKeys.Control; break;
                case "shift": modifiers |= ModifierKeys.Shift; break;
                case "alt": modifiers |= ModifierKeys.Alt; break;
                case "win": modifiers |= ModifierKeys.Windows; break;
                default: return false;
            }
        }
        return TryParseKey(parts[^1], out key);
    }

    /// <summary>可作为快捷键的手势：应用级命令要求带至少一个修饰键（或 F1-F12），防止把裸字母
    /// 注册成全局快捷键；任务作用域命令只在任务列表焦点上下文分发，允许裸键。</summary>
    public static bool IsAllowedShortcut(AppCommand command, ModifierKeys modifiers, Key key)
        => Keymap.IsTaskScoped(command)
           || modifiers != ModifierKeys.None
           || key is >= Key.F1 and <= Key.F12;

    /// <summary>录制时用于忽略纯修饰键按下（等待主键）。</summary>
    public static bool IsModifierKey(Key key)
        => key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin;

    private static string KeyName(Key key) => key switch
    {
        >= Key.D0 and <= Key.D9 => ((char)('0' + (key - Key.D0))).ToString(),
        >= Key.A and <= Key.Z => key.ToString(),
        >= Key.F1 and <= Key.F12 => key.ToString(),
        Key.Back => "Backspace",
        Key.Escape => "Esc",
        _ => NamedKeys.FirstOrDefault(kv => kv.Value == key) is { Key: not null } kv ? kv.Key : key.ToString(),
    };

    private static bool TryParseKey(string name, out Key key)
    {
        key = Key.None;
        if (name.Length == 1)
        {
            var c = char.ToUpperInvariant(name[0]);
            if (c is >= 'A' and <= 'Z') { key = Enum.Parse<Key>(c.ToString()); return true; }
            if (c is >= '0' and <= '9') { key = Key.D0 + (c - '0'); return true; }
            return false;
        }
        if (name.Length is >= 2 and <= 3
            && (name[0] is 'F' or 'f')
            && int.TryParse(name[1..], out var n) && n is >= 1 and <= 12)
        {
            key = Key.F1 + (n - 1);
            return true;
        }
        return NamedKeys.TryGetValue(name, out key);
    }
}

/// <summary>
/// 应用级快捷键表。合并规则（与 VS Code 同语义）：用户键位文件（%APPDATA%/Stanza/keymap.json）
/// 中出现的命令整体替换其默认键位（空列表 = 不绑定），未出现的命令沿用 Defaults。
/// 语义键中上下文多义的（Esc / Enter）不进此表，保持焦点相关硬编码。
/// </summary>
public sealed class Keymap
{
    /// <summary>默认键位：应用级命令的命令修饰键随键盘模式（Windows = Ctrl；macOS = Alt 扮演 Command，
    /// 含 Alt+Shift 组合）。区块切换 Alt+1~4 与任务作用域键两模式一致。</summary>
    private static IReadOnlyList<KeymapEntry> DefaultsFor(bool macOsMode)
    {
        var cmd = macOsMode ? ModifierKeys.Alt : ModifierKeys.Control;
        return
        [
            // ---- 应用级（全局分发，与键盘焦点无关） ----
            new(cmd, Key.S, AppCommand.Save),
            new(cmd, Key.O, AppCommand.Open),
            new(cmd, Key.N, AppCommand.NewTask),
            new(cmd | ModifierKeys.Shift, Key.N, AppCommand.NewDocument),
            new(cmd, Key.R, AppCommand.OpenRecent),
            new(cmd, Key.Z, AppCommand.Undo),
            new(ModifierKeys.Alt, Key.D1, AppCommand.SelectBlock, "1"),
            new(ModifierKeys.Alt, Key.D2, AppCommand.SelectBlock, "2"),
            new(ModifierKeys.Alt, Key.D3, AppCommand.SelectBlock, "3"),
            new(ModifierKeys.Alt, Key.D4, AppCommand.SelectBlock, "4"),

            // ---- 任务作用域（仅任务列表焦点上下文分发；裸键不进文本框，见分发处的作用域检查） ----
            new(ModifierKeys.None, Key.Space, AppCommand.CompleteTask),
            new(ModifierKeys.None, Key.T, AppCommand.OpenTagPicker),
            new(ModifierKeys.None, Key.P, AppCommand.OpenProjectPicker),
            new(ModifierKeys.None, Key.M, AppCommand.OpenMovePicker),
            new(ModifierKeys.None, Key.Back, AppCommand.DiscardTask),
            new(ModifierKeys.None, Key.Delete, AppCommand.DeleteTask),
            new(ModifierKeys.None, Key.Up, AppCommand.NavigateUp),
            new(ModifierKeys.None, Key.K, AppCommand.NavigateUp),
            new(ModifierKeys.None, Key.Down, AppCommand.NavigateDown),
            new(ModifierKeys.None, Key.J, AppCommand.NavigateDown),
            new(ModifierKeys.None, Key.Left, AppCommand.NavigateLeft),
            new(ModifierKeys.None, Key.H, AppCommand.NavigateLeft),
            new(ModifierKeys.None, Key.Right, AppCommand.NavigateRight),
            new(ModifierKeys.None, Key.L, AppCommand.NavigateRight),
        ];
    }

    /// <summary>任务作用域命令：仅在任务列表焦点上下文分发（MainWindow.Drag 中的作用域检查），
    /// 允许裸键绑定。应用级命令全局生效，必须带修饰键。未列入的新命令按应用级处理（安全方向）。</summary>
    public static bool IsTaskScoped(AppCommand command) => command is
        AppCommand.CompleteTask or AppCommand.OpenTagPicker or AppCommand.OpenProjectPicker
        or AppCommand.OpenMovePicker
        or AppCommand.DiscardTask or AppCommand.DeleteTask
        or AppCommand.NavigateUp or AppCommand.NavigateDown
        or AppCommand.NavigateLeft or AppCommand.NavigateRight;

    public static Keymap Current { get; } = new Keymap();

    /// <summary>键盘模式：false = Windows（文本编辑移动键在 Alt，应用快捷键在 Ctrl）；
    /// true = macOS（Alt 扮演 Command：应用快捷键与文本复制键在 Alt，Ctrl 留给文本编辑移动键）。
    /// 来自 settings.json（设置面板切换后调用 Reload 生效）。</summary>
    public bool MacOsMode { get; private set; }

    /// <summary>当前模式下的默认键位表（设置面板据此判定「与默认一致」，覆盖不落盘）。</summary>
    public IReadOnlyList<KeymapEntry> DefaultEntries => DefaultsFor(MacOsMode);

    /// <summary>合并后的键位表。</summary>
    private List<KeymapEntry> _entries = new();

    /// <summary>用户覆盖：命令（含参数）→ 键位集合；空集合 = 显式解绑。</summary>
    private readonly Dictionary<(AppCommand Command, string? Parameter), List<(ModifierKeys Modifiers, Key Key)>>
        _overrides = new();

    /// <summary>键位表已变化（设置面板编辑后），动态提示文本需刷新。</summary>
    public event EventHandler? Changed;

    private Keymap() => Reload();

    /// <summary>按修饰键 + 主键精确匹配第一个条目；未命中返回 null。</summary>
    public KeymapEntry? Resolve(Key key, ModifierKeys modifiers)
        => _entries.FirstOrDefault(e => e.Key == key && e.Modifiers == modifiers);

    /// <summary>某命令（含参数）当前的键位。</summary>
    public IReadOnlyList<KeymapEntry> EntriesFor(AppCommand command, object? parameter)
        => _entries.Where(e => e.Command == command && ParamString(e.Parameter) == ParamString(parameter)).ToList();

    /// <summary>该命令是否有用户覆盖（设置面板据此显示「重置」）。</summary>
    public bool HasOverride(AppCommand command, object? parameter)
        => _overrides.ContainsKey((command, ParamString(parameter)));

    /// <summary>整体替换某命令的键位集合（null = 移除覆盖、恢复默认），持久化并广播。</summary>
    public void SetOverride(AppCommand command, object? parameter, IReadOnlyList<(ModifierKeys, Key)>? gestures)
    {
        var k = (command, ParamString(parameter));
        if (gestures == null) _overrides.Remove(k);
        else _overrides[k] = gestures.ToList();
        Persist();
        Rebuild();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>键位的可读描述（"Ctrl+S / F2"）；无键位时返回 null。</summary>
    public string? Describe(AppCommand command, object? parameter)
    {
        var entries = EntriesFor(command, parameter);
        return entries.Count == 0
            ? null
            : string.Join(" / ", entries.Select(e => Gesture.Format(e.Modifiers, e.Key)));
    }

    /// <summary>重载设置与用户键位文件并重建（设置面板切换键盘模式后调用）。</summary>
    public void Reload()
    {
        MacOsMode = SettingsStore.Load().MacOsMode;
        _overrides.Clear();
        foreach (var binding in KeymapStore.Load())
        {
            if (!Enum.TryParse<AppCommand>(binding.Command, out var command)) continue;
            var gestures = new List<(ModifierKeys, Key)>();
            foreach (var text in binding.Keys)
                if (Gesture.TryParse(text, out var mods, out var key)
                    && Gesture.IsAllowedShortcut(command, mods, key))   // 手改文件的非法手势（如给应用命令绑裸键）直接丢弃
                    gestures.Add((mods, key));
            _overrides[(command, binding.Args)] = gestures;
        }
        Rebuild();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Rebuild()
    {
        var defaults = DefaultEntries;
        var keys = defaults.Select(e => (e.Command, Param: ParamString(e.Parameter)))
            .Concat(_overrides.Keys.Select(k => (k.Command, Param: k.Parameter)))
            .Distinct();
        var entries = new List<KeymapEntry>();
        foreach (var (command, param) in keys)
        {
            if (_overrides.TryGetValue((command, param), out var gestures))
                entries.AddRange(gestures.Select(g => new KeymapEntry(g.Modifiers, g.Key, command, param)));
            else
                entries.AddRange(defaults.Where(
                    e => e.Command == command && ParamString(e.Parameter) == param));
        }
        _entries = entries;
    }

    private void Persist()
        => KeymapStore.Save(_overrides.Select(kv => new UserCommandBinding
        {
            Command = kv.Key.Command.ToString(),
            Args = kv.Key.Parameter,
            Keys = kv.Value.Select(g => Gesture.Format(g.Modifiers, g.Key)).ToList(),
        }).ToList());

    private static string? ParamString(object? parameter) => parameter?.ToString();
}
