using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;

namespace Stanza.App;

/// <summary>
/// 文本框内的 macOS 风格编辑键（Emacs 绑定）：Ctrl+A/E 行首/行尾、Ctrl+B/F 左/右移一个字符、
/// Ctrl+N/P 下移/上移一行、Ctrl+D/H 前删/回删一个字符、Ctrl+K 删到行尾。
/// 经类级 PreviewKeyDown 处理器挂到所有 TextBox（App.OnStartup 注册），先于编辑框内建键行为
/// （Ctrl+A 的全选语义随之被行首替代）。键集合固定为编辑语义：文本框内优先于键位表中的
/// 应用级命令（OnPreProcessInput 让路，与编辑框内 Ctrl+Z 文本级撤销同一先例）。
/// </summary>
public static class TextEditKeys
{
    /// <summary>属于编辑键集合的手势：仅 Ctrl（无 Shift/Alt）+ 集合内字母。</summary>
    public static bool IsEditingGesture(ModifierKeys modifiers, Key key)
        => modifiers == ModifierKeys.Control
           && key is Key.A or Key.B or Key.D or Key.E or Key.F or Key.H or Key.K or Key.N or Key.P;

    /// <summary>类级 PreviewKeyDown 处理：命中集合即执行对应编辑操作并消费。</summary>
    public static void Handle(TextBox box, KeyEventArgs e)
    {
        if (!IsEditingGesture(Keyboard.Modifiers, e.Key)) return;
        e.Handled = true;
        switch (e.Key)
        {
            case Key.A: EditingCommands.MoveToLineStart.Execute(null, box); break;
            case Key.E: EditingCommands.MoveToLineEnd.Execute(null, box); break;
            case Key.B: EditingCommands.MoveLeftByCharacter.Execute(null, box); break;
            case Key.F: EditingCommands.MoveRightByCharacter.Execute(null, box); break;
            case Key.D: EditingCommands.Delete.Execute(null, box); break;
            case Key.H: EditingCommands.Backspace.Execute(null, box); break;
            case Key.N when box.AcceptsReturn: EditingCommands.MoveDownByLine.Execute(null, box); break;
            case Key.P when box.AcceptsReturn: EditingCommands.MoveUpByLine.Execute(null, box); break;
            // 单行框的 N/P 无对应行：消费掉保持无操作（macOS 单行框同款）
            case Key.K: KillToLineEnd(box); break;
        }
    }

    /// <summary>Ctrl+K 删到行尾；光标已在行尾时删换行符本身（与下一行合并，macOS kill 语义）。
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
