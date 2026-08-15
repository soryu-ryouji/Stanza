using System.Text;
using System.Text.RegularExpressions;

namespace Stanza.Core;

/// <summary>
/// Stanza 写出器：UTF-8、LF、区块标题大写规范形式、按规范顺序输出（§4、§6）。
/// </summary>
public static class StanzaWriter
{
    // 与解析器共用同一项目模式（StanzaPatterns），用于检测描述中残留的 +名称
    private static readonly Regex ExtraProjectRegex = new(
        StanzaPatterns.Project,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Write(StanzaDocument doc)
    {
        var lines = new List<string>();

        foreach (var state in TaskStateNames.CanonicalOrder)
        {
            var block = doc.FindBlock(state);
            if (block == null) continue;

            lines.Add("# " + TaskStateNames.ToHeader(state));
            lines.Add("");

            foreach (var task in block.Tasks)
            {
                var main = ComposeMainLine(task);
                // 主行为空的任务无法表示（会成为空白行被解析器跳过），直接丢弃
                if (main.Length == 0) continue;

                lines.Add(main);
                lines.AddRange(task.Notes);   // 续行原样写出（§7.3）
                lines.Add("");                // 任务之间的视觉分隔
            }
        }

        return string.Join('\n', lines) + "\n";
    }

    /// <summary>组合主行文本（供编辑器展示规范化主行）。
    /// <cparamref name="includePriority"/> 为 false 时省略优先级前缀——
    /// GUI 的编辑文本不含优先级标记，优先级由结构化属性承载（§7.2.1 的文本形式仅供 CLI/文件）。</summary>
    public static string ComposeTaskHeader(StanzaTask task, bool includePriority = true)
        => ComposeMainLine(task, includePriority);

    private static string ComposeMainLine(StanzaTask task, bool includePriority = true)
    {
        var sb = new StringBuilder();

        if (includePriority && task.Priority is { } p) sb.Append('(').Append(p).Append(") ");
        if (task.DueDate is { } d) sb.Append(d.ToString("yyyy-MM-dd")).Append(' ');

        var description = task.Description ?? "";
        var project = string.IsNullOrEmpty(task.Project) ? null : task.Project;

        // 描述中若还残留其他 +名称，重解析时“仅第一个 +名称 识别为项目”会误取它，
        // 此时把本项目放到描述之前，保证往返一致（§7.2.4 位置不限）
        var projectFirst = project != null && ExtraProjectRegex.IsMatch(description);

        if (projectFirst) sb.Append('+').Append(project).Append(' ');
        sb.Append(description);
        if (!projectFirst && project != null)
        {
            if (description.Length > 0) sb.Append(' ');
            sb.Append('+').Append(project);
        }

        foreach (var tag in task.Tags)
        {
            if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
            sb.Append('#').Append(tag);
        }

        return sb.ToString().TrimEnd();
    }
}
