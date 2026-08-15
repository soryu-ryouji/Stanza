using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Stanza.Core;

/// <summary>
/// Stanza 解析器，严格遵循 RFC 1.0.0 §7.1 边界规则与 §10 实现指南。
/// </summary>
public static class StanzaParser
{
    // 区块标题：状态名大小写不敏感，行尾允许空白（§6.1）
    private static readonly Regex BlockTitleRegex = new(
        @"^# (DOING|WAIT|DONE|DELETE)[ \t]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 项目与标签模式与写出器共享（StanzaPatterns），见 §7.2.4 / §7.2.5
    private static readonly Regex ProjectRegex = new(
        StanzaPatterns.Project,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TagRegex = new(
        StanzaPatterns.Tag,
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MultiSpaceRegex = new(
        @"[ \t]{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // 时间戳行：续行中整行匹配“日期 + 关键字”（§7.4）；关键字取自字典，英文大小写不敏感
    private static readonly Regex TimestampLineRegex = new(
        @"^[ \t]*(\d{4}-\d{2}-\d{2}) (" + TimestampKeywords.Alternation + @")[ \t]*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static StanzaDocument Parse(string text)
    {
        var doc = new StanzaDocument();
        if (string.IsNullOrEmpty(text)) return doc;

        // 容忍文件开头的 BOM（§4）
        if (text[0] == '\uFEFF') text = text[1..];

        StanzaBlock? block = null;   // 当前状态区块
        StanzaTask? task = null;     // 当前任务锚点
        var blankCount = 0;          // 已遇到、归属未定的连续空白行数
        var lineNo = 0;

        foreach (var rawLine in text.Split('\n'))
        {
            lineNo++;
            var line = rawLine.TrimEnd('\r');   // 同时接受 LF 与 CRLF（§4）

            var title = BlockTitleRegex.Match(line);
            if (title.Success)
            {
                // 规则 1：区块标题开启新区块，当前任务锚点立即结束；同名区块逻辑合并（§6.4）
                block = doc.GetOrAddBlock(TaskStateNames.Parse(title.Groups[1].Value));
                task = null;
                blankCount = 0;
                continue;
            }

            if (line.Trim().Length == 0)
            {
                // 规则 2：空白行暂存，归属待定
                blankCount++;
                continue;
            }

            if (line[0] == ' ' || line[0] == '\t')
            {
                // 规则 3：缩进行
                if (task != null)
                {
                    // 暂存的空白行计入该任务备注（备注内空行），续行原样保留（§7.3）
                    for (var i = 0; i < blankCount; i++) task.Notes.Add("");
                    task.Notes.Add(line);
                }
                else
                {
                    // 孤立续行：忽略并警告
                    doc.Warnings.Add($"Line {lineNo}: orphan continuation line ignored");
                }
                blankCount = 0;
                continue;
            }

            // 规则 4：无缩进行一律开启新任务锚点，暂存空白行丢弃
            if (block == null)
            {
                // 首个区块标题之前的内容：忽略并警告（§10.3 用例 8）
                doc.Warnings.Add($"Line {lineNo}: content before first block header ignored");
                continue;
            }
            task = ParseHeader(line);
            block.Tasks.Add(task);
            blankCount = 0;
        }

        // 规则 5：文件末尾暂存的空白行忽略（天然满足）
        foreach (var b in doc.Blocks)
            foreach (var t in b.Tasks)
                ExtractTimestamps(t);   // §7.4：从备注提取创建/完成时间
        return doc;
    }

    /// <summary>从备注中提取时间戳（§7.4）：创建取第一条，完成取最后一条；备注本身原样保留。
    /// 编辑器从文本重建模型后也应调用，以保持 CreatedAt/CompletedAt 与备注一致。</summary>
    public static void ExtractTimestamps(StanzaTask task)
    {
        foreach (var note in task.Notes)
        {
            if (!TryMatchTimestampLine(note, out var date, out var kind)) continue;
            if (kind == TimestampKind.Created)
                task.CreatedAt ??= date;    // 创建：取第一条，其余按普通备注保留
            else
                task.CompletedAt = date;    // 完成：取最后一条（§7.4.3 日志语义）
        }
    }

    /// <summary>判断一行文本是否为时间戳行（§7.4 整行匹配，容忍前导/尾随空白；日期必须合法）。</summary>
    public static bool IsTimestampLine(string line)
        => TryMatchTimestampLine(line, out _, out _);

    /// <summary>尝试把时间戳行解析为日期与语义类型（§7.4 整行匹配，容忍前导/尾随空白；日期必须合法）。
    /// 供展示层将时间戳从续行中分离为结构化属性。</summary>
    public static bool TryMatchTimestampLine(string line, out DateOnly date, out TimestampKind kind)
    {
        date = default;
        kind = default;
        var m = TimestampLineRegex.Match(line);
        return m.Success
            && DateOnly.TryParseExact(m.Groups[1].Value, "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out date)
            && TimestampKeywords.TryGetKind(m.Groups[2].Value, out kind);
    }

    /// <summary>解析主行文本（供编辑器实时解析）。输入必须是不含换行的单行。</summary>
    public static StanzaTask ParseTaskHeader(string line) => ParseHeader(line);

    /// <summary>解析主行：优先级 → 日期 → 描述主体（含 +项目 与 #标签）（§7.2）。</summary>
    private static StanzaTask ParseHeader(string line)
    {
        var task = new StanzaTask();
        var rest = line;

        // 1. 优先级：(A)–(Z)，右括号后必须紧跟一个空格（§7.2.1）
        if (rest.Length >= 4 && rest[0] == '('
            && rest[1] >= 'A' && rest[1] <= 'Z'
            && rest[2] == ')' && rest[3] == ' ')
        {
            task.Priority = rest[1];
            rest = rest[4..];
        }

        // 2. 日期：严格的 YYYY-MM-DD 且为合法日期才占据日期位（§7.2.2）
        if (rest.Length >= 11
            && char.IsDigit(rest[0]) && char.IsDigit(rest[1]) && char.IsDigit(rest[2]) && char.IsDigit(rest[3])
            && rest[4] == '-'
            && char.IsDigit(rest[5]) && char.IsDigit(rest[6])
            && rest[7] == '-'
            && char.IsDigit(rest[8]) && char.IsDigit(rest[9])
            && rest[10] == ' '
            && DateOnly.TryParseExact(rest[..10], "yyyy-MM-dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var due))
        {
            task.DueDate = due;
            rest = rest[11..];
        }

        // 3. 描述主体：提取至多一个项目与零至多个标签，移除后归并多余空白
        var spans = new List<(int Index, int Length)>();

        var pm = ProjectRegex.Match(rest);
        if (pm.Success)
        {
            task.Project = pm.Value[1..];
            spans.Add((pm.Index, pm.Length));
        }

        foreach (Match tm in TagRegex.Matches(rest))
        {
            task.Tags.Add(tm.Value[1..]);
            spans.Add((tm.Index, tm.Length));
        }

        // 从后往前移除，避免位移
        spans.Sort((a, b) => b.Index.CompareTo(a.Index));
        var body = new StringBuilder(rest);
        foreach (var (index, length) in spans) body.Remove(index, length);

        task.Description = MultiSpaceRegex.Replace(body.ToString(), " ").Trim();
        return task;
    }
}
