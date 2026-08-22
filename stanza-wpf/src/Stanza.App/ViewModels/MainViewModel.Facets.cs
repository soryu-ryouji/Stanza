using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using Stanza.App.Services;
using Stanza.Core;

namespace Stanza.App.ViewModels;

/// <summary>
/// 项目/标签聚合：侧栏列表（Projects/Tags，计数只统计活跃任务，计数归零的条目保留显示 0）、
/// 选中 facet 时的分段面板（PanelView 按状态分组）、选择器候选（FacetNames）与批量属性操作。
/// 聚合触发点：文档加载/新建、任务增删与流转、任务编辑收起（不在主行每次按键时刷新）。
/// </summary>
public sealed partial class MainViewModel
{
    private bool _projectsExpanded = true;
    private bool _tagsExpanded = true;


    /// <summary>侧栏选中的项目/标签；非 null 时任务区显示按状态分段的聚合面板。</summary>
    public FacetItemViewModel? SelectedFacet
    {
        get => _selectedFacet;
        set
        {
            if (!Set(ref _selectedFacet, value)) return;
            if (value != null)
            {
                // 与区块选择互斥
                if (_selectedBlock != null)
                {
                    _selectedBlock = null;
                    OnPropertyChanged(nameof(SelectedBlock));
                }
                // 进入面板时放弃未填写的空草稿（它还不属于任何项目/标签）
                if (ExpandedTask != null && ExpandedTask.IsEmpty)
                    CollapseExpanded();
            }
            RebuildPanel();
            NotifyScopeChanged();
        }
    }

    // ---- 项目/标签面板 ----

    /// <summary>侧栏「项目」列表（按任务数降序）。</summary>
    public ObservableCollection<FacetItemViewModel> Projects { get; } = new();

    /// <summary>侧栏「标签」列表（按任务数降序）。</summary>
    public ObservableCollection<FacetItemViewModel> Tags { get; } = new();

    public bool HasProjects => Projects.Count > 0;
    public bool HasTags => Tags.Count > 0;

    /// <summary>侧栏「项目」分组是否展开。</summary>
    public bool ProjectsExpanded
    {
        get => _projectsExpanded;
        private set
        {
            if (Set(ref _projectsExpanded, value))
                OnPropertyChanged(nameof(ShowProjects));
        }
    }

    /// <summary>侧栏「标签」分组是否展开。</summary>
    public bool TagsExpanded
    {
        get => _tagsExpanded;
        private set
        {
            if (Set(ref _tagsExpanded, value))
                OnPropertyChanged(nameof(ShowTags));
        }
    }

    /// <summary>分组列表可见性 = 有内容且处于展开状态。</summary>
    public bool ShowProjects => HasProjects && _projectsExpanded;
    public bool ShowTags => HasTags && _tagsExpanded;

    /// <summary>面板视图的分组视图（按状态分段，DOING/WAIT/DONE/DELETE 依次排列）。</summary>
    public ListCollectionView PanelView { get; }

    /// <summary>面板任务集。拖拽时由视图直接操作（插入/移动占位项），与区块 Items 同等待遇。</summary>
    public ObservableCollection<object> PanelItems => _panelTasks;

    /// <summary>任务区数据源：区块视图为区块任务集，面板视图为按状态分组的全局匹配集。</summary>
    public object? TaskListSource => _selectedFacet != null ? PanelView : _selectedBlock?.Items;

    // ---- 标签/项目选择器（右键「标签…/项目…」弹出） ----

    /// <summary>内置常用标签（参考 Things 3）：随界面语言取值（Strings 资源，逗号分隔）。
    /// 仅作为选择器候选展示，不写入文件；被应用后才成为文档中的真实标签（§7.2.5）。</summary>
    private static IReadOnlyList<string> PresetTags
        => Loc.Get("Preset_Tags").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>某类 facet 在选择器中的候选名称：标签为内置常用项在前、文档既有项随后（去重）；
    /// 项目无内置项，与侧栏聚合列表同序。</summary>
    public IReadOnlyList<string> FacetNames(FacetKind kind)
        => kind == FacetKind.Tag
            ? PresetTags.Concat(Tags.Select(t => t.Name)).Distinct().ToList()
            : Projects.Select(p => p.Name).ToList();

    /// <summary>选中任务是否全部带有该名称（标签：全部拥有；项目：全部属于）。驱动选择器的 ✓ 标记。</summary>
    public bool SelectionHasFacet(FacetKind kind, string name)
        => SelectedTasks.Count > 0 && SelectedTasks.All(t =>
            kind == FacetKind.Tag ? t.Tags.Contains(name) : t.ProjectName == name);

    /// <summary>选中任务中是否存在任何该类 facet（驱动选择器「清除」按钮的显示）。</summary>
    public bool SelectionHasAnyFacet(FacetKind kind)
        => SelectedTasks.Any(t => kind == FacetKind.Tag ? t.Tags.Count > 0 : t.ProjectName != null);

    /// <summary>切换选中任务的标签：全部已拥有则移除，否则添加。</summary>
    public void ToggleTag(string name)
    {
        var remove = SelectionHasFacet(FacetKind.Tag, name);
        PushUndoSnapshot();
        foreach (var t in SelectedTasks)
        {
            if (remove) t.RemoveTag(name);
            else t.AddTag(name);
        }
        RefreshFacets();
    }

    /// <summary>把选中任务移到指定项目；null 表示清除项目。</summary>
    public void SetProjectForSelection(string? name)
    {
        PushUndoSnapshot();
        foreach (var t in SelectedTasks) t.SetProject(name);
        RefreshFacets();
    }

    /// <summary>清除选中任务的全部标签。</summary>
    public void ClearTagsForSelection()
    {
        PushUndoSnapshot();
        foreach (var t in SelectedTasks) t.ClearTags();
        RefreshFacets();
    }

    // ---- 项目/标签聚合 ----

    /// <summary>重算侧栏项目/标签列表与面板内容。
    /// 侧栏计数只覆盖活跃任务（DOING/WAIT）：归档任务（DONE/DELETE）不推高计数；
    /// 活跃任务全部归档的项目/标签保留显示 0，只有文档中不再存在（任务被永久删除）时才从侧栏移除。
    /// 正在浏览其面板且计数归零时退出回区块视图（见下方回退逻辑）。
    /// 面板内容不受此限：仍按状态分段显示全部匹配任务。
    /// 触发点：文档加载/新建、任务增删与流转、任务编辑收起。
    /// 不在主行每次按键时刷新——避免正在编辑的任务因解析结果变化而中途从面板消失。</summary>
    private void RefreshFacets()
    {
        var all = Blocks.SelectMany(b => b.Tasks).ToList();
        var active = all.Where(t => t.IsActive).ToList();
        RebuildFacetList(Projects,
            active.Where(t => t.ProjectName != null).Select(t => t.ProjectName!),
            all.Where(t => t.ProjectName != null).Select(t => t.ProjectName!),
            FacetKind.Project);
        RebuildFacetList(Tags, active.SelectMany(t => t.Tags), all.SelectMany(t => t.Tags), FacetKind.Tag);
        OnPropertyChanged(nameof(HasProjects));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(ShowProjects));
        OnPropertyChanged(nameof(ShowTags));

        // 浏览中的项目/标签计数已归零（条目保留显示 0）或已从文档消失：退出面板，回到首个有任务的区块
        if (_selectedFacet != null &&
            (_selectedFacet.Count == 0 || !Projects.Contains(_selectedFacet) && !Tags.Contains(_selectedFacet)))
        {
            SelectedFacet = null;
            SelectedBlock = Blocks.FirstOrDefault(b => b.HasTasks) ?? Blocks.FirstOrDefault();
        }
        RebuildPanel();
        NotifyScopeChanged();
    }

    /// <summary>重建侧栏列表：复用同名实例（选中/悬停状态随之保留），仅更新计数并按需增删移动。
    /// 计数只来自活跃任务；文档中仍存在但计数为 0 的条目保留（显示 0），已彻底消失（无任何任务引用）的移除。</summary>
    private static void RebuildFacetList(
        ObservableCollection<FacetItemViewModel> list, IEnumerable<string> activeNames,
        IEnumerable<string> allNames, FacetKind kind)
    {
        var counts = activeNames
            .GroupBy(n => n, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToList();
        var existingNames = allNames.ToHashSet(StringComparer.Ordinal);

        for (var i = list.Count - 1; i >= 0; i--)
            if (!existingNames.Contains(list[i].Name))
                list.RemoveAt(i);
            else if (counts.All(c => c.Name != list[i].Name))
                list[i].Count = 0;   // 计数归零：保留条目，仅清计数

        for (var i = 0; i < counts.Count; i++)
        {
            var (name, count) = counts[i];
            var existing = list.FirstOrDefault(f => f.Name == name);
            if (existing == null)
            {
                list.Insert(Math.Min(i, list.Count), new FacetItemViewModel(kind, name) { Count = count });
            }
            else
            {
                existing.Count = count;
                if (list.IndexOf(existing) != i)
                    list.Move(list.IndexOf(existing), i);
            }
        }
    }

    /// <summary>重建面板任务集（按区块规范序填充，组顺序随之确定）。
    /// 面板只含活跃任务（DOING/WAIT）：已完成/回收站任务不进面板，避免污染显示区域；
    /// 面板计数与侧栏计数由此保持一致。
    /// 增量对齐而非清空重填：未变化项保留容器、选中状态与滚动位置，避免视图跳动。</summary>
    private void RebuildPanel()
    {
        var matches = _selectedFacet is { } facet
            ? Blocks.SelectMany(b => b.Tasks).Where(t => t.IsActive).Where(facet.Matches).ToList()
            : new List<TaskViewModel>();
        SyncPanel(matches);
        OnPropertyChanged(nameof(ScopeHasTasks));
    }

    /// <summary>把面板列表增量对齐到目标序列：删除消失项、插入新项、移动错位项。</summary>
    private void SyncPanel(List<TaskViewModel> target)
    {
        for (var i = _panelTasks.Count - 1; i >= 0; i--)
            if (_panelTasks[i] is not TaskViewModel t || !target.Contains(t))
                _panelTasks.RemoveAt(i);

        // 逐位对齐：目标项在列表中存在则移动，不存在则插入（位置 i 之前的位次已对齐，目标项只可能更靠后）
        for (var i = 0; i < target.Count; i++)
        {
            if (i < _panelTasks.Count && ReferenceEquals(_panelTasks[i], target[i])) continue;
            var existing = _panelTasks.IndexOf(target[i]);
            if (existing >= 0) _panelTasks.Move(existing, i);
            else _panelTasks.Insert(i, target[i]);
        }
    }

    /// <summary>任务区数据源与工具栏作用域属性的统一通知。</summary>
    private void NotifyScopeChanged()
    {
        OnPropertyChanged(nameof(TaskListSource));
        OnPropertyChanged(nameof(ScopeHasTasks));
        OnPropertyChanged(nameof(ShowAddTask));
        OnPropertyChanged(nameof(ShowClear));
        OnPropertyChanged(nameof(ShowClearButton));
        OnPropertyChanged(nameof(ScopeIsActive));
        OnPropertyChanged(nameof(ScopeIsDoing));
        OnPropertyChanged(nameof(ScopeIsWaiting));
        OnPropertyChanged(nameof(ScopeIsArchive));
        OnPropertyChanged(nameof(ScopeIsDeleted));
    }
}
