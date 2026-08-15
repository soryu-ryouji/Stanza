using System.Windows.Input;

namespace Stanza.App;

/// <summary>键位表中可分发的命令 ID。作为键位数据的稳定标识，供后续用户自定义键位文件引用。</summary>
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

/// <summary>
/// 应用级快捷键表：键位 → 命令的映射是数据而非 XAML 标记，
/// 为后续用户自定义键位（启动时合并覆盖 Defaults）与一键多绑做准备。
/// 语义键（Esc / Enter / Delete / Backspace 等焦点相关键）不进此表，
/// 由 MainWindow 的 KeyDown 与浮层各自处理。
/// </summary>
public static class Keymap
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

    /// <summary>按修饰键 + 主键精确匹配第一个条目；未命中返回 null。
    /// 后续支持上下文条件（when）时在此引入 context 参数，数据形状不变。</summary>
    public static KeymapEntry? Resolve(Key key, ModifierKeys modifiers)
        => Defaults.FirstOrDefault(e => e.Key == key && e.Modifiers == modifiers);
}
