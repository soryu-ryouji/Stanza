namespace Stanza.Core;

/// <summary>一份 Stanza 文档：若干状态区块（按规范顺序排列）+ 解析警告。</summary>
public sealed class StanzaDocument
{
    public List<StanzaBlock> Blocks { get; } = new();

    /// <summary>解析过程中被忽略内容的警告（孤立续行、区块外内容等）。</summary>
    public List<string> Warnings { get; } = new();

    public StanzaBlock? FindBlock(TaskState state) => Blocks.FirstOrDefault(b => b.State == state);

    /// <summary>获取指定状态的区块；不存在时按规范顺序位置新建。</summary>
    public StanzaBlock GetOrAddBlock(TaskState state)
    {
        var existing = FindBlock(state);
        if (existing != null) return existing;

        var block = new StanzaBlock(state);
        var order = TaskStateNames.CanonicalOrder;
        var myIndex = Array.IndexOf(order, state);
        var pos = Blocks.Count;
        for (var i = 0; i < Blocks.Count; i++)
        {
            if (Array.IndexOf(order, Blocks[i].State) > myIndex)
            {
                pos = i;
                break;
            }
        }
        Blocks.Insert(pos, block);
        return block;
    }
}
