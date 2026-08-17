using System.Text.RegularExpressions;
using Stanza.Core;

namespace Stanza.App.ViewModels;

/// <summary>
/// 任务的可编辑视图模型。主行以纯文本形式内联编辑，但编辑文本只含 日期 + 描述：
/// 优先级（§7.2.1）、项目（§7.2.4）、标签（§7.2.5）是结构化属性，键入的完整记号被自动捕获隐藏，
/// 通过右键菜单/选择器管理。任何修改都通知 MainViewModel 触发自动保存。
/// </summary>
public sealed class TaskViewModel : ViewModelBase
{
    private readonly MainViewModel _owner;

    private TaskState _state;
    private string _headerText = "";
    private string _notesText = "";
    private bool _isExpanded;
    private bool _headerEdited;   // 用户编辑过主行（收起时才做记号捕获；未编辑的展开/收起必须无损）

    // 主行解析结果（只读，供展示）
    private char? _priority;
    private DateOnly? _due;
    private string _description = "";

    // 项目/标签：结构化存储（唯一持久来源，与优先级同模式）
    private string? _project;
    private readonly List<string> _tags = new();

    // 展示用有效值 = 结构化属性 ∪ 编辑文本中正在输入的记号（chip 实时跟随输入）
    private string? _projectEffective;
    private IReadOnlyList<string> _tagsEffective = Array.Empty<string>();

    // 时间戳属性（§7.4：以续行形式集中存储在主行之后，与备注分离，不在备注编辑器中显示）
    private DateOnly? _createdAt;
    private readonly List<DateOnly> _completedDates = new();

    public TaskViewModel(MainViewModel owner) => _owner = owner;

    public static TaskViewModel FromModel(MainViewModel owner, StanzaTask model, TaskState state)
    {
        var vm = new TaskViewModel(owner) { _state = state, _priority = model.Priority, _project = model.Project };
        vm._tags.AddRange(model.Tags);
        vm.LoadNotes(model.Notes);
        // 编辑文本只含 日期 + 描述：优先级/项目/标签都是结构化属性（§7.2.1/§7.2.4/§7.2.5）
        vm.SetHeaderSilently(StanzaWriter.ComposeEditableHeader(model));
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
                OnPropertyChanged(nameof(ShowCompleted));
            }
        }
    }

    public bool IsActive => State is TaskState.Doing or TaskState.Wait;
    public bool IsDone => State is TaskState.Done;
    public bool IsDeleted => State is TaskState.Delete;

    // ---- 主行（内联元数据） ----

    /// <summary>主行编辑文本：<c>2026-08-07 描述</c>——仅日期与描述。
    /// 优先级（§7.2.1）、项目（§7.2.4）、标签（§7.2.5）是结构化属性：键入的完整记号被自动捕获隐藏
    /// （尾随空格实时捕获，行尾残留收起时捕获），不在编辑文本中常驻。</summary>
    public string HeaderText
    {
        get => _headerText;
        set
        {
            if (Set(ref _headerText, value ?? ""))
            {
                _headerEdited = true;
                CaptureTypedTokens();
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

    // 实时捕获用的完整记号模式：StanzaPatterns 同规则 + 尾随空白（视为输入完成的标志）；
    // 行尾未输入完成的记号不匹配，由 CommitHeader 在收起时捕获
    private static readonly Regex TerminatedProjectRegex = new(
        StanzaPatterns.Project + @"(?=[ \t])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex TerminatedTagRegex = new(
        StanzaPatterns.Tag + @"(?=[ \t])", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>把编辑文本中输入完成的记号捕获为结构化属性：优先级前缀（行首 "(A) " 完整单元，
    /// 循环剥除以容忍连续前缀，后者覆盖前者）与带尾随空白的 +项目/#标签。
    /// 已知代价：剥除导致文本变短时光标位置可能轻微偏移，属次要的幂等输入路径。</summary>
    private void CaptureTypedTokens()
    {
        while (StanzaParser.TrySplitPriority(_headerText, out var typed, out var rest))
        {
            _priority = typed;
            _headerText = rest;
        }
        while (TerminatedProjectRegex.Match(_headerText) is { Success: true } pm)
        {
            _project = pm.Value[1..];   // 多个项目记号：后者覆盖前者（与优先级一致）
            _headerText = _headerText.Remove(pm.Index, pm.Length);
        }
        while (TerminatedTagRegex.Match(_headerText) is { Success: true } tm)
        {
            var name = tm.Value[1..];
            if (!_tags.Contains(name)) _tags.Add(name);
            _headerText = _headerText.Remove(tm.Index, tm.Length);
        }
    }

    private void RefreshParsed()
    {
        var m = StanzaParser.ParseTaskHeader(_headerText);
        _due = m.DueDate;
        _description = m.Description;
        // 有效值：文本中正在输入的记号优先（项目覆盖、标签按文本在前合并），提交后并入结构化属性。
        // 标签数组必须总是新实例：与结构化列表同引用时，AddTag/RemoveTag 后引用不变，
        // WPF 绑定对引用相等的新旧值短路不刷新（卡片 chip 不更新）
        _projectEffective = m.Project ?? _project;
        _tagsEffective = m.Tags.Count == 0
            ? _tags.ToArray()
            : m.Tags.Distinct().Concat(_tags.Except(m.Tags, StringComparer.Ordinal)).ToArray();

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

    /// <summary>收起时提交主行：捕获编辑中残留的项目/标签记号（行尾未带尾随空白、未被实时捕获的），
    /// 编辑文本规范化为「日期 + 描述」。仅用户编辑过后起作用——未编辑的展开/收起必须无损。</summary>
    internal void CommitHeader()
    {
        if (!_headerEdited) return;
        _headerEdited = false;
        var m = StanzaParser.ParseTaskHeader(_headerText);
        if (m.Project == null && m.Tags.Count == 0) return;   // 无残留记号：保留用户原始文本
        if (m.Project != null) _project = m.Project;
        var merged = m.Tags.Distinct().Concat(_tags.Except(m.Tags, StringComparer.Ordinal)).ToList();
        _tags.Clear();
        _tags.AddRange(merged);
        SetHeaderSilently(StanzaWriter.ComposeEditableHeader(m));
        _owner.NotifyContentChanged();
    }

    /// <summary>把 Core 规范化（§9 流转）后的主行模型写回：项目/标签进入结构化属性，
    /// 编辑文本规范化为「日期 + 描述」。变更通知由调用方统一触发。</summary>
    internal void ApplyHeaderModel(StanzaTask m)
    {
        _project = m.Project;
        _tags.Clear();
        _tags.AddRange(m.Tags.Distinct());
        SetHeaderSilently(StanzaWriter.ComposeEditableHeader(m));
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

    public bool HasProject => _projectEffective != null;
    public string? ProjectName => _projectEffective;
    public string ProjectDisplay => "+" + _projectEffective;

    public IReadOnlyList<string> Tags => _tagsEffective;
    public bool HasTags => _tagsEffective.Count > 0;

    /// <summary>详情元数据行（截止/创建/完成）存在可展示内容时，展开视图才显示该行。
    /// 优先级以标题文字颜色表达；项目/标签固定在标题行右侧 chip，均不在此行。</summary>
    public bool HasAnyMeta => Due != null || HasCreated || HasCompleted;

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

    // ---- 批量标签/项目操作（右键菜单）：直接读写结构化属性 ----

    /// <summary>为任务添加标签；已存在同名标签时不变。</summary>
    public void AddTag(string tag)
    {
        if (_tags.Contains(tag)) return;
        _tags.Add(tag);
        AfterMetaMutation();
    }

    /// <summary>把任务移到指定项目（§7.2.4 每条任务至多一个项目，直接替换）；传 null 清除项目。</summary>
    public void SetProject(string? project)
    {
        if (_project == project) return;
        _project = project;
        AfterMetaMutation();
    }

    /// <summary>从任务移除标签；不含该标签时不变。</summary>
    public void RemoveTag(string tag)
    {
        if (!_tags.Remove(tag)) return;
        AfterMetaMutation();
    }

    /// <summary>清除任务的全部标签（选择器的「清除」按钮）。</summary>
    public void ClearTags()
    {
        if (_tags.Count == 0) return;
        _tags.Clear();
        AfterMetaMutation();
    }

    private void AfterMetaMutation()
    {
        RefreshParsed();   // 重算有效值并发出展示通知
        _owner.NotifyContentChanged();
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

    /// <summary>是否完全没有内容（保存时丢弃）。时间戳与优先级是工具/菜单写入的属性，不计为内容；
    /// 项目/标签是用户显式设置的归属信息，计为内容（仅有归属的草稿不被误弃，与预填文本时代一致）。</summary>
    public bool IsEmpty =>
        HeaderText.Trim().Length == 0
        && _project == null && _tags.Count == 0
        && NotesText.Split('\n').All(line => line.Trim().Length == 0);

    public StanzaTask ToModel()
    {
        var task = StanzaParser.ParseTaskHeader(HeaderText);
        task.Priority = _priority;   // 编辑文本不含优先级前缀，以结构化属性为准
        // 项目/标签同样以结构化属性为准；编辑文本可能残留未提交的输入中记号（自动保存发生在编辑中途），并入
        task.Project ??= _project;
        foreach (var tag in _tags)
            if (!task.Tags.Contains(tag)) task.Tags.Add(tag);

        // §7.4：时间戳属性集中写为主行之后的首批续行（创建在前，完成历史按序随后），与备注分离
        if (_createdAt is { } created)
            task.Notes.Add("    " + TaskTransitions.TimestampLine(TimestampKind.Created, created));
        foreach (var d in _completedDates)
            task.Notes.Add("    " + TaskTransitions.TimestampLine(TimestampKind.Completed, d));

        // 编辑器中不显示缩进；写出时统一加 4 空格缩进（§7.3 续行必须缩进），空白行保持为空串
        foreach (var line in NotesText.Replace("\r\n", "\n").Split('\n'))
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
