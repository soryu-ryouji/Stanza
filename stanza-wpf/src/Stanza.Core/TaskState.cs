namespace Stanza.Core;

/// <summary>任务的四种状态区块（RFC §6）。</summary>
public enum TaskState
{
    Doing,
    Wait,
    Done,
    Delete,
}

public static class TaskStateNames
{
    /// <summary>规范顺序，写出文件时使用。</summary>
    public static readonly TaskState[] CanonicalOrder =
        { TaskState.Doing, TaskState.Wait, TaskState.Done, TaskState.Delete };

    public static string ToHeader(TaskState state) => state switch
    {
        TaskState.Doing => "DOING",
        TaskState.Wait => "WAIT",
        TaskState.Done => "DONE",
        TaskState.Delete => "DELETE",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    /// <summary>大小写不敏感解析状态名（RFC §6.1）。</summary>
    public static TaskState Parse(string name) => name.ToUpperInvariant() switch
    {
        "DOING" => TaskState.Doing,
        "WAIT" => TaskState.Wait,
        "DONE" => TaskState.Done,
        "DELETE" => TaskState.Delete,
        _ => throw new ArgumentException($"Unknown state name: {name}", nameof(name)),
    };
}
