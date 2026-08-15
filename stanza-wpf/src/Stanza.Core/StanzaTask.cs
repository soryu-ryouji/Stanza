namespace Stanza.Core;

/// <summary>一条任务段落：主行元数据 + 续行备注（RFC §7）。</summary>
public sealed class StanzaTask
{
    /// <summary>优先级字母（A-Z），null 表示无优先级。仅在 DOING/WAIT 中有语义。</summary>
    public char? Priority { get; set; }

    /// <summary>截止日期，null 表示无。</summary>
    public DateOnly? DueDate { get; set; }

    /// <summary>描述主体（已移除 +项目 与 #标签）。</summary>
    public string Description { get; set; } = "";

    /// <summary>项目名（不含 + 前缀），null 表示无。</summary>
    public string? Project { get; set; }

    /// <summary>标签名列表（不含 # 前缀）。</summary>
    public List<string> Tags { get; } = new();

    /// <summary>续行备注，原样保留（含缩进；备注内空行为空串）。</summary>
    public List<string> Notes { get; } = new();
}
