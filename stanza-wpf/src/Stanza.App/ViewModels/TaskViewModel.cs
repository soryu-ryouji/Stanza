using System.Windows.Input;
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

    public TaskViewModel(MainViewModel owner)
    {
        _owner = owner;
        CompleteCommand = new RelayCommand(_ => _owner.CompleteTask(this));
        DiscardCommand = new RelayCommand(_ => _owner.DiscardTask(this));
        RestoreCommand = new RelayCommand(_ => _owner.RestoreTask(this));
        DeletePermanentCommand = new RelayCommand(_ => _owner.DeletePermanent(this));
        MoveToWaitCommand = new RelayCommand(_ => _owner.DeferTask(this));
        MoveToDoingCommand = new RelayCommand(_ => _owner.ActivateTask(this));
    }

    public static TaskViewModel FromModel(MainViewModel owner, StanzaTask model, TaskState state)
    {
        var vm = new TaskViewModel(owner)
        {
            _state = state,
            _notesText = DedentNotes(model.Notes),
        };
        vm.SetHeaderSilently(StanzaWriter.ComposeTaskHeader(model));
        return vm;
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

    /// <summary>主行原文：(A) 2026-08-07 描述 +项目 #标签。编辑时实时解析，展示层只读解析结果。</summary>
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

    public char? Priority => _priority;

    /// <summary>优先级仅在 DOING/WAIT 中展示（RFC §7.2.1）。</summary>
    public bool ShowPriority => Priority != null && State is TaskState.Doing or TaskState.Wait;

    public DateOnly? Due => _due;
    public string DueDisplay => Due?.ToString("yyyy-MM-dd") ?? "";
    public bool HasDue => Due != null;
    public bool IsOverdue => Due is { } d && d < DateOnly.FromDateTime(DateTime.Today) && IsActive;

    public string Description => _description;

    public bool HasProject => _project != null;
    public string ProjectDisplay => "+" + _project;

    public IReadOnlyList<string> Tags => _tags;
    public bool HasTags => _tags.Count > 0;

    /// <summary>填写过任何元数据时，展开视图才显示解析结果行。</summary>
    public bool HasAnyMeta => Priority != null || Due != null || HasProject || HasTags;

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

    // ---- 操作 ----

    public ICommand CompleteCommand { get; }
    public ICommand DiscardCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand DeletePermanentCommand { get; }
    public ICommand MoveToWaitCommand { get; }
    public ICommand MoveToDoingCommand { get; }

    /// <summary>是否完全没有内容（保存时丢弃）。</summary>
    public bool IsEmpty =>
        HeaderText.Trim().Length == 0 && NotesText.Trim().Length == 0;

    public void AppendNote(string line)
    {
        var current = NotesText.TrimEnd();
        NotesText = current.Length == 0 ? line : current + "\n" + line;
    }

    public StanzaTask ToModel()
    {
        var task = StanzaParser.ParseTaskHeader(HeaderText);

        // 编辑器中不显示缩进；写出时统一加 4 空格缩进（§7.3 续行必须缩进），空白行保持为空串
        foreach (var line in (NotesText ?? "").Replace("\r\n", "\n").Split('\n'))
            task.Notes.Add(line.Trim().Length == 0 ? "" : "    " + line);
        while (task.Notes.Count > 0 && task.Notes[^1].Length == 0)
            task.Notes.RemoveAt(task.Notes.Count - 1);

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
