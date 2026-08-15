using System.Text.RegularExpressions;

namespace Stanza.Core;

/// <summary>时间戳行的语义类型（RFC §7.4）。</summary>
public enum TimestampKind
{
    /// <summary>创建时间：任务创建时写入，应当为第一条续行；多条时取第一条。</summary>
    Created,

    /// <summary>完成时间：每次进入 DONE 时在续行末尾追加；取最后一条，其余为完成历史。</summary>
    Completed,
}

/// <summary>
/// 时间戳关键字字典（RFC §7.4.2）：语义类型 → 各语言关键字。
/// 每个数组的首元素为规范书写形式（写出时使用），其余为识别用别名，匹配时大小写不敏感。
/// 新增语言只需向对应数组追加关键字，解析器自动识别。
/// </summary>
public static class TimestampKeywords
{
    public static IReadOnlyDictionary<TimestampKind, IReadOnlyList<string>> All { get; } =
        new Dictionary<TimestampKind, IReadOnlyList<string>>
        {
            [TimestampKind.Created] = new[] { "创建", "created" },
            [TimestampKind.Completed] = new[] { "完成", "completed" },
        };

    // 关键字 → 语义类型 的反查表（英文别名按 OrdinalIgnoreCase 归一）
    private static readonly IReadOnlyDictionary<string, TimestampKind> Lookup =
        All.SelectMany(p => p.Value.Select(k => (Keyword: k, p.Key)))
           .ToDictionary(x => x.Keyword, x => x.Key, StringComparer.OrdinalIgnoreCase);

    /// <summary>规范书写形式（写出工具使用）。</summary>
    public static string Canonical(TimestampKind kind) => All[kind][0];

    /// <summary>按关键字查语义类型（大小写不敏感）。</summary>
    public static bool TryGetKind(string keyword, out TimestampKind kind)
        => Lookup.TryGetValue(keyword, out kind);

    /// <summary>全部关键字的正则选择项，供解析器组装时间戳行模式。</summary>
    internal static string Alternation { get; } =
        string.Join("|", All.Values.SelectMany(v => v).Select(Regex.Escape));
}
