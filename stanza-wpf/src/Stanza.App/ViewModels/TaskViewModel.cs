using Stanza.Core;

namespace Stanza.App.ViewModels;

/// <summary>
/// 任务的可编辑视图模型。主行以纯文本形式内联编辑（优先级/日期/项目/标签写在描述里），
/// 输入时实时解析，解析结果以只读属性暴露给展示层。任何修改都通知 MainViewModel 触发自动保存。
/// </summary>
public sealed class TaskViewModel : ViewModelBase
{
    private readonly MainViewModel _owner;

    private TaskState _state;
    private string _headerText = "";
    private string _notesText = "";
    private bool _isExpanded;

    // 主行解析结果（只读，供展示）
    private char? _priority;
    private DateOnly? _due;
    private string _description = "";
    private string? _project;
    private IReadOnlyList<string> _tags = Array.Empty<string>();

    // 时间戳属性（§7.4：以续行形式集中存储在主行之后，与备注分离，不在备注编辑器中显示）
    private DateOnly? _createdAt;
    private readonly List<DateOnly> _completedDates = new();

    public TaskViewModel(MainViewModel owner) => _owner = owner;

    public static TaskViewModel FromModel(MainViewModel owner, StanzaTask model, TaskState state)
    {
        var vm = new TaskViewModel(owner) { _state = state, _priority = model.Priority };
        vm.LoadNotes(model.Notes);
        // GUI 的编辑文本不含优先级前缀（§7.2.1 的文本标记仅供 CLI/文件），优先级由 Priority 属性承载
        vm.SetHeaderSilently(StanzaWriter.ComposeTaskHeader(model, includePriority: false));
        return vm;
    }

    /// <summary>加载续行：时间戳行分离为结构化属性（§7.4）——第一条创建行记入创建时间，
    /// 完成行按序构成完成历史；其余续行（含多余的创建行，§7.4.3）按备注原样保留。</summary>
    private void LoadNotes(List<string> notes)
    {
        var rest = new List<string>();
        foreach (var note in notes)
        {
            if (StanzaParser.TryMatchTimestampLine(note, out var date, out var kind))
            {
                if (kind == TimestampKind.Completed) { _completedDates.Add(date); continue; }
                if (_createdAt == null) { _createdAt = date; continue; }
            }
            rest.Add(note);
        }
        _notesText = DedentNotes(rest);
    }

    // ---- 状态 ----

    public TaskState State
    {
        get => _state;
        set
        {
            if (Set(ref _state, value))
            {
                OnPropertyChanged(nameof(DisplayQuadrant));
                OnPropertyChanged(nameof(IsOverdue));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsDone));
                OnPropertyChanged(nameof(IsDeleted));
                OnPropertyChanged(nameof(CanRestore));
                OnPropertyChanged(nameof(IsDoing));
                OnPropertyChanged(nameof(IsWaiting));
                OnPropertyChanged(nameof(ShowCompleted));
            }
        }
    }

    public bool IsActive => State is TaskState.Doing or TaskState.Wait;
    public bool IsDone => State is TaskState.Done;
    public bool IsDeleted => State is TaskState.Delete;
    public bool CanRestore => State is TaskState.Done or TaskState.Delete;
    public bool IsDoing => State is TaskState.Doing;
    public bool IsWaiting => State is TaskState.Wait;

    // ---- 主行（内联元数据） ----

    /// <summary>主行编辑文本：<c>2026-08-07 描述 +项目 #标签</c>。
    /// 不含优先级前缀（§7.2.1 的前缀由 <see cref="Priority"/> 属性承载，输入前缀会被自动接管）。
    /// 编辑时实时解析，展示层只读解析结果。</summary>
    public string HeaderText
    {
        get => _headerText;
        set
        {
            if (Set(ref _headerText, value ?? ""))
            {
                RefreshParsed();
                _owner.NotifyContentChanged();
            }
        }
    }

    private void SetHeaderSilently(string text)
    {
        _headerText = text;
        RefreshParsed();
    }

    private void RefreshParsed()
    {
        // 用户手动输入的优先级前缀被接管为结构化属性并从编辑文本剥除（GUI 不展示文本标记）。
        // 循环剥除以容忍 "(A1) (B) 任务" 这类连续前缀（后者覆盖前者）；
        // 已知代价：剥除导致文本变短时光标位置可能轻微偏移，属次要的幂等输入路径
        while (StanzaParser.TrySplitPriority(_headerText, out var typed, out var rest))
        {
            _priority = typed;
            _headerText = rest;
        }

        var m = StanzaParser.ParseTaskHeader(_headerText);
        _due = m.DueDate;
        _description = m.Description;
        _project = m.Project;
        _tags = m.Tags.ToArray();

        OnPropertyChanged(nameof(HeaderText));
        OnPropertyChanged(nameof(Priority));
        OnPropertyChanged(nameof(DisplayQuadrant));
        OnPropertyChanged(nameof(Due));
        OnPropertyChanged(nameof(DueDisplay));
        OnPropertyChanged(nameof(HasDue));
        OnPropertyChanged(nameof(IsOverdue));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(HasProject));
        OnPropertyChanged(nameof(ProjectDisplay));
        OnPropertyChanged(nameof(Tags));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(HasAnyMeta));
    }

    /// <summary>四象限优先级（RFC §7.2.1）。GUI 中的结构化属性：编辑文本不含优先级前缀，
    /// 通过右键菜单/工具栏设置；直接输入文本前缀会被自动接管到此属性。</summary>
    public char? Priority
    {
        get => _priority;
        set
        {
            if (Set(ref _priority, value))
            {
                OnPropertyChanged(nameof(DisplayQuadrant));
                _owner.NotifyContentChanged();
            }
        }
    }

    /// <summary>用于标题着色的象限字母：仅在 DOING/WAIT 且有优先级时非 null（其余情况标题保持默认墨色）。</summary>
    public char? DisplayQuadrant => State is TaskState.Doing or TaskState.Wait ? Priority : null;

    public DateOnly? Due => _due;
    public string DueDisplay => Due?.ToString("yyyy-MM-dd") ?? "";
    public bool HasDue => Due != null;
    public bool IsOverdue => Due is { } d && d < DateOnly.FromDateTime(DateTime.Today) && IsActive;

    public string Description => _description;

    public bool HasProject => _project != null;
    public string? ProjectName => _project;
    public string ProjectDisplay => "+" + _project;

    public IReadOnlyList<string> Tags => _tags;
    public bool HasTags => _tags.Count > 0;

    /// <summary>详情元数据行（截止/创建/完成/项目/标签）存在可展示内容时，展开视图才显示该行。
    /// 优先级不在其列：它以标题文字颜色表达。</summary>
    public bool HasAnyMeta => Due != null || HasProject || HasTags || HasCreated || HasCompleted;

    // ---- 时间戳属性（§7.4） ----

    public bool HasCreated => _createdAt != null;
    public string CreatedDisplay => _createdAt is { } c ? $"{TimestampKeywords.Canonical(TimestampKind.Created)} {c:yyyy-MM-dd}" : "";

    public bool HasCompleted => _completedDates.Count > 0;
    /// <summary>最近一次完成时间；完整完成历史（含重开记录）保留在续行属性块中。</summary>
    public string CompletedDisplay => _completedDates.Count > 0 ? $"{TimestampKeywords.Canonical(TimestampKind.Completed)} {_completedDates[^1]:yyyy-MM-dd}" : "";

    /// <summary>折叠态仅在 DONE 任务上展示完成日期（重开后历史保留，但不宜在标题行展示）。</summary>
    public bool ShowCompleted => IsDone && HasCompleted;

    /// <summary>写入创建时间（§7.4.3：每个任务至多一条，既有创建时间不被覆盖）。</summary>
    public void SetCreated(DateOnly date)
    {
        if (_createdAt != null) return;
        _createdAt = date;
        NotifyTimestampsChanged();
        _owner.NotifyContentChanged();
    }

    /// <summary>追加一条完成时间（§7.4.3：每次进入 DONE 追加一条，历史完整保留）。</summary>
    public void AppendCompleted(DateOnly date)
    {
        _completedDates.Add(date);
        NotifyTimestampsChanged();
        _owner.NotifyContentChanged();
    }

    private void NotifyTimestampsChanged()
    {
        OnPropertyChanged(nameof(HasCreated));
        OnPropertyChanged(nameof(CreatedDisplay));
        OnPropertyChanged(nameof(HasCompleted));
        OnPropertyChanged(nameof(CompletedDisplay));
        OnPropertyChanged(nameof(ShowCompleted));
        OnPropertyChanged(nameof(HasAnyMeta));
    }

    // ---- 批量标签/项目操作（右键菜单） ----

    /// <summary>为任务添加标签；已存在同名标签时不变。经 解析→修改→重组 往返，规则由 Core 承载。</summary>
    public void AddTag(string tag)
    {
        var m = StanzaParser.ParseTaskHeader(HeaderText);
        if (m.Tags.Contains(tag)) return;
        m.Tags.Add(tag);
        HeaderText = StanzaWriter.ComposeTaskHeader(m, includePriority: false);
    }

    /// <summary>把任务移到指定项目（§7.2.4 每条任务至多一个项目，直接替换）；传 null 清除项目。</summary>
    public void SetProject(string? project)
    {
        var m = StanzaParser.ParseTaskHeader(HeaderText);
        if (m.Project == project) return;
        m.Project = project;
        HeaderText = StanzaWriter.ComposeTaskHeader(m, includePriority: false);
    }

    /// <summary>从任务移除标签；不含该标签时不变。</summary>
    public void RemoveTag(string tag)
    {
        var m = StanzaParser.ParseTaskHeader(HeaderText);
        if (!m.Tags.Remove(tag)) return;
        HeaderText = StanzaWriter.ComposeTaskHeader(m, includePriority: false);
    }

    /// <summary>清除任务的全部标签（选择器的「清除」按钮）。</summary>
    public void ClearTags()
    {
        if (_tags.Count == 0) return;
        var m = StanzaParser.ParseTaskHeader(HeaderText);
        m.Tags.Clear();
        HeaderText = StanzaWriter.ComposeTaskHeader(m, includePriority: false);
    }

    // ---- 备注 ----

    public string NotesText
    {
        get => _notesText;
        set
        {
            if (Set(ref _notesText, value ?? ""))
            {
                OnPropertyChanged(nameof(HasNotes));
                _owner.NotifyContentChanged();
            }
        }
    }

    public bool HasNotes => NotesText.Trim().Length > 0;

    // ---- 展开 ----

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>是否完全没有内容（保存时丢弃）。时间戳是工具写入的属性，不计为内容（§7.4）。</summary>
    public bool IsEmpty =>
        HeaderText.Trim().Length == 0
        && NotesText.Split('\n').All(line => line.Trim().Length == 0);

    public StanzaTask ToModel()
    {
        var task = StanzaParser.ParseTaskHeader(HeaderText);
        task.Priority = _priority;   // 编辑文本不含优先级前缀，以结构化属性为准

        // §7.4：时间戳属性集中写为主行之后的首批续行（创建在前，完成历史按序随后），与备注分离
        if (_createdAt is { } created)
            task.Notes.Add("    " + TaskTransitions.TimestampLine(TimestampKind.Created, created));
        foreach (var d in _completedDates)
            task.Notes.Add("    " + TaskTransitions.TimestampLine(TimestampKind.Completed, d));

        // 编辑器中不显示缩进；写出时统一加 4 空格缩进（§7.3 续行必须缩进），空白行保持为空串
        foreach (var line in (NotesText ?? "").Replace("\r\n", "\n").Split('\n'))
            task.Notes.Add(line.Trim().Length == 0 ? "" : "    " + line);
        while (task.Notes.Count > 0 && task.Notes[^1].Length == 0)
            task.Notes.RemoveAt(task.Notes.Count - 1);

        StanzaParser.ExtractTimestamps(task);   // 保持 CreatedAt/CompletedAt 与续行一致（§7.4）
        return task;
    }

    /// <summary>去除备注的公共缩进，编辑器中按顶格显示。</summary>
    private static string DedentNotes(List<string> notes)
    {
        var nonBlank = notes.Where(n => n.Trim().Length > 0).ToList();
        if (nonBlank.Count == 0) return "";
        var min = nonBlank.Min(n => n.TakeWhile(c => c is ' ' or '\t').Count());
        var lines = notes.Select(n => n.Trim().Length == 0 ? "" : n[Math.Min(min, n.Length)..]).ToList();
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines);
    }
}
