using System.Text.RegularExpressions;
using System.Windows.Controls;

namespace Stanza.App;

/// <summary>
/// 备注编辑框的列表项自动续接：光标所在行以列表记号（- 、- [ ] 、1. ）开头时，
/// Enter 在新行补全后续记号——无序原样、复选框重置为未勾、有序递增；缩进随行保留。
/// 在仅有记号的空列表项上按 Enter 视为放弃续接：清除记号退出列表（VS Code 同款语义）。
/// 这是编辑器内的自由文本辅助，与 RFC 语法无关，不进 Stanza.Core。
/// </summary>
public static class NotesListEditing
{
    // 行首列表记号：缩进 + （复选框 / 无序 / 有序）+ 至少一个尾空白（孤立的 "-" 不算列表项）
    private static readonly Regex Marker = new(
        @"^(?<indent>[ \t]*)(?<mark>- \[[ xX]\]|-|\d+\.)[ \t]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>备注编辑框裸 Enter 的续接处理：已补全/退出列表返回 true；
    /// 非列表行或带选区时返回 false，交给编辑框插入普通换行。</summary>
    public static bool TryHandleEnter(TextBox box)
    {
        if (box.SelectionLength > 0) return false;
        var text = box.Text;
        var caret = box.CaretIndex;

        var lineStart = caret == 0 ? 0 : text.LastIndexOf('\n', caret - 1) + 1;
        var match = Marker.Match(text[lineStart..caret]);
        if (!match.Success) return false;

        // 记号后（光标两侧合并）没有内容：空列表项 → 清除记号退出列表。
        // match 基于行内子串，换算为全文索引时需加 lineStart
        var nl = text.IndexOf('\n', caret);
        var lineEnd = nl < 0 ? text.Length : nl;
        if (string.IsNullOrWhiteSpace(text[(lineStart + match.Length)..caret] + text[caret..lineEnd]))
        {
            box.Select(lineStart, caret - lineStart);
            box.SelectedText = "";
            return true;
        }

        // 选区替换（而非 Text 赋值）保留编辑框的撤销栈；但 SelectedText 会把插入文本留为选区，
        // 替换后显式折叠回光标（落在补全记号之后）
        var insert = Environment.NewLine + NextMarker(match);
        box.Select(caret, 0);
        box.SelectedText = insert;
        box.Select(caret + insert.Length, 0);
        return true;
    }

    /// <summary>下一行的续接记号（含缩进与尾空格）：无序原样、复选框重置未勾、有序递增；
    /// 数字超出 int 范围的极端输入原样保留。</summary>
    private static string NextMarker(Match match)
    {
        var mark = match.Groups["mark"].Value;
        var body = mark.StartsWith("- [", StringComparison.Ordinal)
            ? "- [ ]"
            : mark.EndsWith('.') && int.TryParse(mark.AsSpan(0, mark.Length - 1), out var n)
                ? (n + 1).ToString() + "."
                : mark;
        return match.Groups["indent"].Value + body + " ";
    }
}
