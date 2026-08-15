namespace Stanza.Core;

/// <summary>一个状态区块（RFC §6）。同名区块已逻辑合并（§6.4）。</summary>
public sealed class StanzaBlock
{
    public StanzaBlock(TaskState state) => State = state;

    public TaskState State { get; }

    public List<StanzaTask> Tasks { get; } = new();
}
