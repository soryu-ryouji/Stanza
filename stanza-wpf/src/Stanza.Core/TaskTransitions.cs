namespace Stanza.Core;

/// <summary>
/// 任务状态流转规则（RFC §9）。规则的唯一事实来源在 Core；
/// App 层负责 ViewModel 集合的编排，不得另行实现这些规则。
/// </summary>
public static class TaskTransitions
{
    /// <summary>§9：状态流转时规范化任务。进入 DONE/DELETE 移除优先级；进入 DONE 追加完成
    /// 时间戳行（§7.4.3，规范续行形式，4 空格缩进；写出层负责将其归位到主行之后的属性块）。
    /// 同状态内移动不触发任何变更。既有续行（含创建时间戳、历史完成时间戳）一律保留。</summary>
    /// <param name="today">完成日期的取值，由调用方注入以保证可测试。</param>
    public static void NormalizeForState(StanzaTask task, TaskState from, TaskState to, DateOnly today)
    {
        if (from == to) return;
        if (to is TaskState.Done or TaskState.Delete) task.Priority = null;
        if (to == TaskState.Done) task.Notes.Add("    " + TimestampLine(TimestampKind.Completed, today));
    }

    /// <summary>§7.4：时间戳行的规范文本（规范形式关键字，不含续行缩进）。</summary>
    public static string TimestampLine(TimestampKind kind, DateOnly date)
        => $"{date:yyyy-MM-dd} {TimestampKeywords.Canonical(kind)}";

    /// <summary>§9：进入目标区块的默认位置——DONE/DELETE 插到顶部，DOING/WAIT 追加到末尾。</summary>
    public static bool InsertsAtTop(TaskState state) => state is TaskState.Done or TaskState.Delete;

    /// <summary>活跃状态（DOING/WAIT）：参与自动排序（§9 排序约定）。</summary>
    public static bool IsActiveState(TaskState state) => state is TaskState.Doing or TaskState.Wait;
}

/// <summary>
/// 活跃区块（DOING/WAIT）的规范排序（RFC §7.2.1）：象限字母升序（无优先级排尾）→
/// 截止日期升序（无日期排尾）。比较器只定义键序；配合 OrderBy 使用时同键相对顺序不变
/// （稳定排序），拖拽排序即依赖这一点只调整同优先级内的相对顺序。
/// </summary>
public static class ActiveTaskOrdering
{
    public static int Compare(StanzaTask a, StanzaTask b)
        => Compare(a.Priority, a.DueDate, b.Priority, b.DueDate);

    /// <summary>键值形式的重载，供调用方对非 <see cref="StanzaTask"/> 的视图模型复用同一规则。</summary>
    public static int Compare(char? quadrantA, DateOnly? dueA, char? quadrantB, DateOnly? dueB)
    {
        var c = (quadrantA ?? char.MaxValue).CompareTo(quadrantB ?? char.MaxValue);
        if (c != 0) return c;
        return (dueA ?? DateOnly.MaxValue).CompareTo(dueB ?? DateOnly.MaxValue);
    }
}
