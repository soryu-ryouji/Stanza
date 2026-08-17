using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace Stanza.App;

/// <summary>
/// 文本框内的平台风格编辑键（挂到所有 TextBox 的类级 PreviewKeyDown，App.OnStartup 注册，
/// 先于编辑框内建键行为）。两个键盘模式（Keymap.MacOsMode，设置面板切换）：
/// Windows：Alt+A/E 行首/行尾、Alt+B/F 左/右移一个字符、Alt+N/P 下移/上移一行、
///          Alt+D/H 前删/回删一个字符、Alt+K 删到行尾；Ctrl 组保持系统惯例（全选/复制/撤销）。
/// macOS：  编辑移动键在 Ctrl（Emacs 绑定，同 macOS 文本框）；Alt 扮演 Command——
///          Alt+C/X/V/A/Z 复制/剪切/粘贴/全选/撤销（Alt+Shift+Z 重做），
///          原生 Ctrl 文本键（C/X/V/A/Z/Y）随之禁用，操作语言统一。
/// 编辑手势在文本框内优先于键位表中的应用命令（OnPreProcessInput 让路）。
/// </summary>
public static class TextEditKeys
{
    /// <summary>编辑移动键的修饰键：Windows = Alt，macOS = Ctrl。</summary>
    private static ModifierKeys EditModifier => Keymap.Current.MacOsMode ? ModifierKeys.Control : ModifierKeys.Alt;

    /// <summary>属于编辑移动键集合的手势：仅编辑修饰键（无其他修饰键）+ 集合内字母。</summary>
    public static bool IsEditingGesture(ModifierKeys modifiers, Key key)
        => modifiers == EditModifier
           && key is Key.A or Key.B or Key.D or Key.E or Key.F or Key.H or Key.K or Key.N or Key.P;

    /// <summary>类级 PreviewKeyDown 处理：命中集合即执行对应编辑操作并消费。</summary>
    public static void Handle(TextBox box, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;   // Alt 组合的主键在 SystemKey 上
        var modifiers = Keyboard.Modifiers;

        if (Keymap.Current.MacOsMode)
        {
            // Alt 扮演 Command：文本复制键（先于应用命令分发，见 OnPreProcessInput 的让路注释）
            if (modifiers == ModifierKeys.Alt
                && key is Key.C or Key.X or Key.V or Key.A or Key.Z)
            {
                e.Handled = true;
                (key switch
                {
                    Key.C => ApplicationCommands.Copy,
                    Key.X => ApplicationCommands.Cut,
                    Key.V => ApplicationCommands.Paste,
                    Key.A => ApplicationCommands.SelectAll,
                    _ => ApplicationCommands.Undo,
                }).Execute(null, box);
                return;
            }
            if (modifiers == (ModifierKeys.Alt | ModifierKeys.Shift) && key == Key.Z)
            {
                e.Handled = true;
                ApplicationCommands.Redo.Execute(null, box);
                return;
            }
            // 原生 Ctrl 文本键禁用：macOS 模式下 Ctrl 只作编辑移动键，复制组统一在 Alt 上
            // （Ctrl+A 是编辑移动键的行首，不在禁用列）
            if (modifiers == ModifierKeys.Control
                && key is Key.C or Key.X or Key.V or Key.Z or Key.Y)
            {
                e.Handled = true;
                return;
            }
        }

        if (!IsEditingGesture(modifiers, key)) return;
        e.Handled = true;
        switch (key)
        {
            case Key.A: EditingCommands.MoveToLineStart.Execute(null, box); break;
            case Key.E: EditingCommands.MoveToLineEnd.Execute(null, box); break;
            case Key.B: EditingCommands.MoveLeftByCharacter.Execute(null, box); break;
            case Key.F: EditingCommands.MoveRightByCharacter.Execute(null, box); break;
            case Key.D: EditingCommands.Delete.Execute(null, box); break;
            case Key.H: EditingCommands.Backspace.Execute(null, box); break;
            case Key.N when box.AcceptsReturn: EditingCommands.MoveDownByLine.Execute(null, box); break;
            case Key.P when box.AcceptsReturn: EditingCommands.MoveUpByLine.Execute(null, box); break;
            // 单行框的 N/P 无对应行：消费掉保持无操作
            case Key.K: KillToLineEnd(box); break;
        }
    }

    /// <summary>删到行尾；光标已在行尾时删换行符本身（与下一行合并）。
    /// 有选区时退化为删除选区。选区替换（而非 Text 赋值）保留编辑框的撤销栈。</summary>
    private static void KillToLineEnd(TextBox box)
    {
        if (box.SelectionLength > 0)
        {
            box.SelectedText = "";
            return;
        }
        var text = box.Text;
        var caret = box.CaretIndex;
        var nl = text.IndexOf('\n', caret);
        var lineEnd = nl < 0 ? text.Length : nl > 0 && text[nl - 1] == '\r' ? nl - 1 : nl;
        if (caret < lineEnd)
            box.Select(caret, lineEnd - caret);
        else if (nl >= 0)
            box.Select(lineEnd, nl + 1 - lineEnd);   // 行尾的 \r\n
        else
            return;   // 已在文档末尾，无可删
        box.SelectedText = "";
    }
}
