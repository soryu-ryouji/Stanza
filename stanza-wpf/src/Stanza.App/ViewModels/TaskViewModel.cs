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
    private StanzaPriority? _priority;
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
        var vm = new TaskViewModel(owner) { _state = state };
        vm.LoadNotes(model.Notes);
        vm.SetHeaderSilently(StanzaWriter.ComposeTaskHeader(model));
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
                OnPropertyChanged(nameof(ShowPriority));
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

    /// <summary>主行原文：(A1) 2026-08-07 描述 +项目 #标签。编辑时实时解析，展示层只读解析结果。</summary>
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
        var m = StanzaParser.ParseTaskHeader(_headerText);
        _priority = m.Priority;
        _due = m.DueDate;
        _description = m.Description;
        _project = m.Project;
        _tags = m.Tags.ToArray();

        OnPropertyChanged(nameof(Priority));
        OnPropertyChanged(nameof(ShowPriority));
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

    /// <summary>四象限优先级（RFC §7.2.1）；绑定展示时使用其文本形式（<c>A</c> 或 <c>A3</c>）。</summary>
    public StanzaPriority? Priority => _priority;

    /// <summary>优先级仅在 DOING/WAIT 中展示（RFC §7.2.1）。</summary>
    public bool ShowPriority => Priority != null && State is TaskState.Doing or TaskState.Wait;

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

    /// <summary>填写过任何元数据或存在时间戳属性时，展开视图才显示解析结果行。</summary>
    public bool HasAnyMeta => Priority != null || Due != null || HasProject || HasTags || HasCreated || HasCompleted;

    // ---- 时间戳属性（§7.4） ----

    public DateOnly? CreatedAt => _createdAt;
    public bool HasCreated => _createdAt != null;
    public string CreatedDisplay => _createdAt is { } c ? $"{TimestampKeywords.Canonical(TimestampKind.Created)} {c:yyyy-MM-dd}" : "";

    /// <summary>最近一次完成时间；完整完成历史（含重开记录）保留在续行属性块中。</summary>
    public DateOnly? CompletedAt => _completedDates.Count > 0 ? _completedDates[^1] : null;
    public bool HasCompleted => _completedDates.Count > 0;
    public string CompletedDisplay => CompletedAt is { } c ? $"{TimestampKeywords.Canonical(TimestampKind.Completed)} {c:yyyy-MM-dd}" : "";

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
        OnPropertyChanged(nameof(CreatedAt));
        OnPropertyChanged(nameof(HasCreated));
        OnPropertyChanged(nameof(CreatedDisplay));
        OnPropertyChanged(nameof(CompletedAt));
        OnPropertyChanged(nameof(HasCompleted));
        OnPropertyChanged(nameof(CompletedDisplay));
        OnPropertyChanged(nameof(ShowCompleted));
        OnPropertyChanged(nameof(HasAnyMeta));
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
