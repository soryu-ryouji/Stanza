using System.Windows.Input;
using Stanza.App.Services;

namespace Stanza.App;

/// <summary>键位表中可分发的命令 ID。作为键位数据的稳定标识，供用户键位文件引用。</summary>
public enum AppCommand
{
    Save,
    Open,
    NewTask,
    NewDocument,
    SelectBlock,
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

    /// <summary>可作为快捷键的手势：带至少一个修饰键，或 F1-F12 功能键（防止把裸字母/Enter 注册成快捷键）。</summary>
    public static bool IsAllowedShortcut(ModifierKeys modifiers, Key key)
        => modifiers != ModifierKeys.None || key is >= Key.F1 and <= Key.F12;

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
/// 语义键（Esc / Enter / Delete / Backspace 等焦点相关键）不进此表。
/// </summary>
public sealed class Keymap
{
    public static readonly IReadOnlyList<KeymapEntry> Defaults =
    [
        new(ModifierKeys.Control, Key.S, AppCommand.Save),
        new(ModifierKeys.Control, Key.O, AppCommand.Open),
        new(ModifierKeys.Control, Key.N, AppCommand.NewTask),
        new(ModifierKeys.Control | ModifierKeys.Shift, Key.N, AppCommand.NewDocument),
        new(ModifierKeys.Control, Key.D1, AppCommand.SelectBlock, "1"),
        new(ModifierKeys.Control, Key.D2, AppCommand.SelectBlock, "2"),
        new(ModifierKeys.Control, Key.D3, AppCommand.SelectBlock, "3"),
        new(ModifierKeys.Control, Key.D4, AppCommand.SelectBlock, "4"),
    ];

    public static Keymap Current { get; } = new Keymap();

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

    private void Reload()
    {
        _overrides.Clear();
        foreach (var binding in KeymapStore.Load())
        {
            if (!Enum.TryParse<AppCommand>(binding.Command, out var command)) continue;
            var gestures = new List<(ModifierKeys, Key)>();
            foreach (var text in binding.Keys)
                if (Gesture.TryParse(text, out var mods, out var key))
                    gestures.Add((mods, key));
            _overrides[(command, binding.Args)] = gestures;
        }
        Rebuild();
    }

    private void Rebuild()
    {
        var keys = Defaults.Select(e => (e.Command, Param: ParamString(e.Parameter)))
            .Concat(_overrides.Keys.Select(k => (k.Command, Param: k.Parameter)))
            .Distinct();
        var entries = new List<KeymapEntry>();
        foreach (var (command, param) in keys)
        {
            if (_overrides.TryGetValue((command, param), out var gestures))
                entries.AddRange(gestures.Select(g => new KeymapEntry(g.Modifiers, g.Key, command, param)));
            else
                entries.AddRange(Defaults.Where(
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
