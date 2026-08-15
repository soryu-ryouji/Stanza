namespace Stanza.App.ViewModels;

public enum FacetKind { Project, Tag }

/// <summary>
/// 侧栏的项目/标签条目。纯派生数据：由任务主行解析结果聚合而来，不持久化。
/// 选中后任务区切换为按状态分段的聚合面板（DOING/WAIT/DONE/DELETE 依次排列）。
/// </summary>
public sealed class FacetItemViewModel : ViewModelBase
{
    private int _count;

    public FacetItemViewModel(FacetKind kind, string name)
    {
        Kind = kind;
        Name = name;
    }

    public FacetKind Kind { get; }

    /// <summary>项目/标签名（不含 +/# 前缀）。</summary>
    public string Name { get; }

    /// <summary>主行中的写法（+项目 / #标签）；新建任务预填与面板标题都用它。</summary>
    public string Token => Kind == FacetKind.Project ? "+" + Name : "#" + Name;

    public int Count
    {
        get => _count;
        set => Set(ref _count, value);
    }

    public bool Matches(TaskViewModel task) => Kind == FacetKind.Project
        ? string.Equals(task.ProjectName, Name, StringComparison.Ordinal)
        : task.Tags.Contains(Name, StringComparer.Ordinal);
}
