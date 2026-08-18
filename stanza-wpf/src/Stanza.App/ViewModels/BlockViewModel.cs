using System.Collections.ObjectModel;
using Stanza.App.Services;
using Stanza.Core;

namespace Stanza.App.ViewModels;

/// <summary>状态区块视图模型。Items 中除任务外，拖拽时还会临时插入 GapItem 占位。</summary>
public sealed class BlockViewModel : ViewModelBase
{
    public BlockViewModel(TaskState state, bool existedInSource)
    {
        State = state;
        ExistedInSource = existedInSource;
        Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(TaskCount));
            OnPropertyChanged(nameof(HasTasks));
        };
    }

    public TaskState State { get; }

    /// <summary>区块显示名（本地化；仅用于展示，逻辑判断一律用 State）。语言切换后经 RefreshName 刷新。</summary>
    public string Name => Loc.StateName(State);

    /// <summary>语言切换时重发 Name 变更（侧栏列表与大标题随绑定刷新）。</summary>
    internal void RefreshName() => OnPropertyChanged(nameof(Name));

    /// <summary>源文件中是否存在该区块（决定空区块是否写回，RFC §6.3）。</summary>
    public bool ExistedInSource { get; set; }

    /// <summary>任务视图模型与拖拽占位项的混合集合。</summary>
    public ObservableCollection<object> Items { get; } = new();

    public int TaskCount => Items.OfType<TaskViewModel>().Count();

    public bool HasTasks => TaskCount > 0;

    /// <summary>活跃列表（DOING/WAIT）：工具栏提供「添加任务」。</summary>
    public bool IsActiveList => State is TaskState.Doing or TaskState.Wait;

    /// <summary>归档列表（DONE/DELETE）：工具栏提供「清空」。</summary>
    public bool IsArchiveList => State is TaskState.Done or TaskState.Delete;

    public IEnumerable<TaskViewModel> Tasks => Items.OfType<TaskViewModel>();

    public void InsertTask(int index, TaskViewModel task)
    {
        task.State = State;
        Items.Insert(Math.Clamp(index, 0, Items.Count), task);
    }

    public bool RemoveTask(TaskViewModel task) => Items.Remove(task);
}
