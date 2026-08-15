using Stanza.Core;

namespace Stanza.App.ViewModels;

/// <summary>拖拽排序 / 拖拽新建时的位置预览占位项（不是任务）。</summary>
public sealed class GapItem
{
    public double Height { get; set; } = 40;

    /// <summary>面板视图中占位项所属的分段状态（分组视图按此归位）；区块拖拽中不使用。</summary>
    public TaskState State { get; set; }
}
