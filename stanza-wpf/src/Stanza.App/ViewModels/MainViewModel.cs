using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using Stanza.App.Services;
using Stanza.Core;

namespace Stanza.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer _statusClearTimer;
    private bool _suppressDirty;

    private BlockViewModel? _selectedBlock;
    private FacetItemViewModel? _selectedFacet;
    private TaskViewModel? _selectedTask;

    /// <summary>面板视图（选中项目/标签时）的任务集：始终按区块规范序填充，分组视图直接建立在其上。
    /// 元素类型为 object 以兼容拖拽时的占位项（与区块 Items 一致）。</summary>
    private readonly ObservableCollection<object> _panelTasks = new();
    private TaskViewModel? _expandedTask;
    private IReadOnlyList<TaskViewModel> _selectedTasks = Array.Empty<TaskViewModel>();
    private bool _hasDocument;
    private string? _filePath;
    private string _fileName = "";
    private bool _isDirty;
    private string _statusText = "";
    private SaveStatus _statusKind = SaveStatus.None;
    private bool _projectsExpanded = true;
    private bool _tagsExpanded = true;

    public MainViewModel()
    {
        SaveCommand = new RelayCommand(_ => Save(), _ => HasDocument);
        OpenCommand = new RelayCommand(_ => OpenInteractive());
        NewDocumentCommand = new RelayCommand(_ => NewDocument());
        OpenRecentCommand = new RelayCommand(_ => OpenRecentRequested?.Invoke());
        NewTaskCommand = new RelayCommand(_ => CreateTaskAtEnd(),
            _ => HasDocument && (SelectedBlock != null || SelectedFacet != null));
        SelectBlockCommand = new RelayCommand(p =>
        {
            if (p is string s && int.TryParse(s, out var i) && i >= 1 && i <= Blocks.Count)
                SelectedBlock = Blocks[i - 1];
        });
        ToggleFacetSectionCommand = new RelayCommand(p =>
        {
            if (p is "tags") TagsExpanded = !TagsExpanded;
            else ProjectsExpanded = !ProjectsExpanded;
        });
        ClearBlockCommand = new RelayCommand(
            _ => ClearSelectedBlock(),
            _ => HasDocument && SelectedBlock is { HasTasks: true } b
                && b.State is TaskState.Done or TaskState.Delete);
        CompleteSelectionCommand = new RelayCommand(_ => TransitionTasks(SelectedTasks.ToList(), TaskState.Done, normalize: true), _ => HasSelection);
        DiscardSelectionCommand = new RelayCommand(
            _ => TransitionTasks(SelectedTasks.ToList(), TaskState.Delete, normalize: true),
            _ => HasSelection && ScopeState != TaskState.Delete);   // 已废弃的任务无需再废弃
        RestoreSelectionCommand = new RelayCommand(_ => TransitionTasks(SelectedTasks.ToList(), TaskState.Doing), _ => HasSelection);
        DeferSelectionCommand = new RelayCommand(_ => TransitionTasks(SelectedTasks.ToList(), TaskState.Wait), _ => HasSelection);
        ActivateSelectionCommand = new RelayCommand(_ => TransitionTasks(SelectedTasks.ToList(), TaskState.Doing), _ => HasSelection);
        DeleteSelectionCommand = new RelayCommand(_ => DeleteTasksPermanently(SelectedTasks.ToList()), _ => HasSelection);
        SetPriorityCommand = new RelayCommand(
            p => { if (p is PriorityOption option) SetPriority(option); },
            _ => HasSelection);

        Recents = new RecentFilesViewModel(
            openFile: OpenFile,
            notifyMissing: _ => SetStatus(SaveStatus.Info, Loc.Get("Status_Missing")));

        // 面板视图按状态分段：按 TaskState 原始值分组（组头模板自行转换名称与颜色），
        // 组顺序由 _panelTasks 的填充顺序决定（RebuildPanel 始终按规范序重建）
        PanelView = new ListCollectionView(_panelTasks);
        PanelView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(TaskViewModel.State)));

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _autoSaveTimer.Tick += (_, _) => { _autoSaveTimer.Stop(); Save(); };

        // 语言切换：区块显示名（侧栏 / 大标题）与面板分组头（转换器）随当前语言重算
        Loc.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(ScopeTitle));
            foreach (var block in Blocks) block.RefreshName();
            PanelView.Refresh();
        };

        _statusClearTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        _statusClearTimer.Tick += (_, _) =>
        {
            _statusClearTimer.Stop();
            SetStatus(SaveStatus.None, "");
        };
    }

    // ---- 绑定属性 ----

    public ObservableCollection<BlockViewModel> Blocks { get; } = new();

    /// <summary>最近打开的文件列表（左下角响应区的数据源）。</summary>
    public RecentFilesViewModel Recents { get; }

    public BlockViewModel? SelectedBlock
    {
        get => _selectedBlock;
        set
        {
            if (!Set(ref _selectedBlock, value)) return;
            // 区块视图与面板视图互斥：选中区块时退出项目/标签面板
            if (value != null && _selectedFacet != null)
            {
                _selectedFacet = null;
                OnPropertyChanged(nameof(SelectedFacet));
                RebuildPanel();
            }
            // 切走区块时展开的空草稿视为放弃（焦点已离开），直接移除；非空任务保持展开状态
            if (ExpandedTask != null && ExpandedTask.IsEmpty)
                CollapseExpanded();
            NotifyScopeChanged();
        }
    }

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

    // 标题区与工具栏的作用域属性：区块模式取区块状态，面板模式取面板/首个选中任务的状态
    public string ScopeTitle => _selectedFacet?.Token ?? _selectedBlock?.Name ?? "";
    public int ScopeTaskCount => _selectedFacet != null ? _panelTasks.Count : _selectedBlock?.TaskCount ?? 0;
    public bool ScopeHasTasks => ScopeTaskCount > 0;
    public bool ShowAddTask => _selectedFacet != null || _selectedBlock?.IsActiveList == true;
    public bool ShowClear => _selectedFacet == null && _selectedBlock?.IsArchiveList == true;

    private TaskState? ScopeState =>
        _selectedFacet != null ? _selectedTasks.FirstOrDefault()?.State : _selectedBlock?.State;

    public bool ScopeIsActive => ScopeState is TaskState.Doing or TaskState.Wait;
    public bool ScopeIsDoing => ScopeState is TaskState.Doing;
    public bool ScopeIsWaiting => ScopeState is TaskState.Wait;
    public bool ScopeIsArchive => ScopeState is TaskState.Done or TaskState.Delete;
    public bool ScopeIsDeleted => ScopeState is TaskState.Delete;

    /// <summary>选中的任务（高亮）。选中与展开是两个独立状态。</summary>
    public TaskViewModel? SelectedTask
    {
        get => _selectedTask;
        set => Set(ref _selectedTask, value);
    }

    /// <summary>当前选中的任务集合（支持 Shift/Ctrl 多选，由视图同步）。</summary>
    public IReadOnlyList<TaskViewModel> SelectedTasks
    {
        get => _selectedTasks;
        private set
        {
            _selectedTasks = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSelection));
            // 面板模式下工具栏可见性取决于首个选中任务的状态
            NotifyScopeChanged();
        }
    }

    /// <summary>是否有选中任务（驱动工具栏切换为任务操作）。</summary>
    public bool HasSelection => _selectedTasks.Count > 0;

    /// <summary>视图在 ListBox 选择变化时同步选中集。</summary>
    public void UpdateSelection(IReadOnlyList<TaskViewModel> tasks) => SelectedTasks = tasks;

    /// <summary>当前展开详情的任务，至多一个。</summary>
    public TaskViewModel? ExpandedTask
    {
        get => _expandedTask;
        private set => Set(ref _expandedTask, value);
    }

    public bool HasDocument
    {
        get => _hasDocument;
        private set => Set(ref _hasDocument, value);
    }

    public string? FilePath
    {
        get => _filePath;
        private set => Set(ref _filePath, value);
    }

    public string FileName
    {
        get => _fileName;
        private set => Set(ref _fileName, value);
    }

    public bool IsDirty
    {
        get => _isDirty;
        private set => Set(ref _isDirty, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public SaveStatus StatusKind
    {
        get => _statusKind;
        private set => Set(ref _statusKind, value);
    }

    // ---- 视图提供的对话框与通知 ----

    public Func<string?>? PickOpenFile { get; set; }
    public Func<string?>? PickSaveFile { get; set; }

    /// <summary>新任务创建后触发，视图负责滚动并聚焦。</summary>
    public event EventHandler<TaskViewModel>? TaskCreated;

    /// <summary>最近文件弹层的打开/循环高亮请求（由视图实现，VS Code quick-open 语义）。</summary>
    public Action? OpenRecentRequested { get; set; }

    // ---- 命令 ----

    public ICommand SaveCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand NewDocumentCommand { get; }
    public ICommand OpenRecentCommand { get; }
    public ICommand NewTaskCommand { get; }
    public ICommand SelectBlockCommand { get; }
    public ICommand ToggleFacetSectionCommand { get; }
    public ICommand ClearBlockCommand { get; }
    public ICommand CompleteSelectionCommand { get; }
    public ICommand DiscardSelectionCommand { get; }
    public ICommand RestoreSelectionCommand { get; }
    public ICommand DeferSelectionCommand { get; }
    public ICommand ActivateSelectionCommand { get; }
    public ICommand DeleteSelectionCommand { get; }
    public ICommand SetPriorityCommand { get; }

    /// <summary>键位表命令 ID → 命令实例（Keymap 分发用；ID 是后续用户键位文件的稳定标识）。</summary>
    public ICommand? CommandFor(AppCommand command) => command switch
    {
        AppCommand.Save => SaveCommand,
        AppCommand.Open => OpenCommand,
        AppCommand.NewTask => NewTaskCommand,
        AppCommand.NewDocument => NewDocumentCommand,
        AppCommand.OpenRecent => OpenRecentCommand,
        AppCommand.SelectBlock => SelectBlockCommand,
        _ => null,
    };

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
        foreach (var t in SelectedTasks) t.SetProject(name);
        RefreshFacets();
    }

    /// <summary>清除选中任务的全部标签。</summary>
    public void ClearTagsForSelection()
    {
        foreach (var t in SelectedTasks) t.ClearTags();
        RefreshFacets();
    }

    // ---- 优先级 ----

    /// <summary>优先级菜单选项（任务右键菜单与底部工具栏共用同一套选项与样式）。</summary>
    public IReadOnlyList<PriorityOption> PriorityOptions { get; } = BuildPriorityOptions();

    private static IReadOnlyList<PriorityOption> BuildPriorityOptions() => new[]
    {
        new PriorityOption("Priority_A", 'A'),
        new PriorityOption("Priority_B", 'B'),
        new PriorityOption("Priority_C", 'C'),
        new PriorityOption("Priority_D", 'D'),
        new PriorityOption("Priority_Clear", null),
    };

    /// <summary>设置选中任务的优先级（仅限活跃状态任务）。</summary>
    private void SetPriority(PriorityOption option)
    {
        var targets = SelectedTasks.Where(t => t.IsActive).ToList();
        if (targets.Count == 0) return;
        foreach (var t in targets) t.Priority = option.Value;
        SettleSort();   // 排序键变化后重排（象限 → 截止日期）
    }

    // ---- 展开状态 ----

    /// <summary>展开指定任务（同时收起之前展开的任务）。</summary>
    public void ExpandTask(TaskViewModel task)
    {
        if (ExpandedTask != task) CollapseExpanded();   // 收起前一个；空草稿随之移除
        if (ExpandedTask == task) return;
        ExpandedTask = task;
        task.IsExpanded = true;
        SettleSort();
    }

    /// <summary>收起展开的任务；空草稿（未填写任何内容的新任务）随之移除——
    /// 空任务没有持久化价值：保存时按 IsEmpty 过滤，主行为空也无法写出（§7 / §10.3）。
    /// 所有失焦路径（Enter/Esc/点空白/点其他任务/切换区块）都经由此处获得该行为。</summary>
    public void CollapseExpanded()
    {
        if (ExpandedTask == null) return;
        var task = ExpandedTask;
        task.IsExpanded = false;
        ExpandedTask = null;
        if (task.IsEmpty)
        {
            Blocks.FirstOrDefault(b => b.Items.Contains(task))?.RemoveTask(task);
            if (SelectedTask == task) SelectedTask = null;
            NotifyContentChanged();
        }
        else
        {
            task.CommitHeader();   // 收纳编辑中输入的 项目/标签 记号为结构化属性
        }
        SettleSort();
        RefreshFacets();   // 编辑落定后更新项目/标签归属
    }

    /// <summary>任务被移走或删除前调用：解除展开/选中状态（收起不清空草稿——任务尚在流转中）。</summary>
    private void DetachTask(TaskViewModel task)
    {
        if (ExpandedTask == task)
        {
            task.IsExpanded = false;
            ExpandedTask = null;
        }
        if (SelectedTask == task) SelectedTask = null;
    }

    // ---- 文件 ----

    public void OpenFile(string path)
    {
        if (!FlushDirty()) return;
        try
        {
            var doc = StanzaParser.Parse(File.ReadAllText(path));
            LoadDocument(doc);
            FilePath = path;
            FileName = Path.GetFileName(path);
            HasDocument = true;
            IsDirty = false;
            Recents.Register(path);
            SetStatus(doc.Warnings.Count > 0
                ? SaveStatus.Info
                : SaveStatus.None,
                doc.Warnings.Count > 0 ? Loc.Format("Status_Warnings", doc.Warnings.Count) : "");
        }
        catch (Exception ex)
        {
            SetStatus(SaveStatus.Error, Loc.Format("Status_OpenFailed", ex.Message));
        }
    }

    private void OpenInteractive()
    {
        var path = PickOpenFile?.Invoke();
        if (!string.IsNullOrEmpty(path)) OpenFile(path);
    }

    /// <summary>启动时恢复上次打开的文件；没有则停留在欢迎页。</summary>
    public void OpenStartupFile()
    {
        if (Recents.LastFile is { } last && File.Exists(last))
            OpenFile(last);
    }

    public void NewDocument()
    {
        if (!FlushDirty()) return;

        _suppressDirty = true;
        try
        {
            Blocks.Clear();
            foreach (var state in TaskStateNames.CanonicalOrder)
                Blocks.Add(CreateBlock(state, existedInSource: true));
            SelectedBlock = Blocks[0];
            SelectedTask = null;
            CollapseExpanded();
            RefreshFacets();
            FilePath = null;
            FileName = Loc.Get("File_Untitled");
            HasDocument = true;
            IsDirty = false;
            SetStatus(SaveStatus.None, "");
        }
        finally
        {
            _suppressDirty = false;
        }
    }

    public void Save()
    {
        if (!HasDocument) return;
        _autoSaveTimer.Stop();

        if (FilePath == null)
        {
            var path = PickSaveFile?.Invoke();
            if (string.IsNullOrEmpty(path)) return;   // 用户取消，保持未保存状态
            FilePath = path;
            FileName = Path.GetFileName(path);
            Recents.Register(path);
        }

        try
        {
            SetStatus(SaveStatus.Saving, Loc.Get("Status_Saving"));
            var doc = new StanzaDocument();
            foreach (var b in Blocks)
            {
                var models = b.Tasks.Where(t => !t.IsEmpty).Select(t => t.ToModel()).ToList();
                // 空区块仅在源文件中存在时才写回（§6.3）
                if (models.Count == 0 && !b.ExistedInSource) continue;
                var block = doc.GetOrAddBlock(b.State);
                block.Tasks.AddRange(models);
            }
            File.WriteAllText(FilePath, StanzaWriter.Write(doc), new UTF8Encoding(false));
            // 本次写出后这些区块已存在于源文件（§6.3），之后变空也要写回区块头
            foreach (var b in Blocks)
                if (!b.ExistedInSource && b.Tasks.Any(t => !t.IsEmpty))
                    b.ExistedInSource = true;
            IsDirty = false;
            SetStatus(SaveStatus.Saved, Loc.Format("Status_Saved", DateTime.Now.ToString("HH:mm")));
        }
        catch (Exception ex)
        {
            SetStatus(SaveStatus.Error, Loc.Format("Status_SaveFailed", ex.Message));
        }
    }

    /// <summary>有未保存更改时先尝试保存；返回是否可以继续后续操作。</summary>
    private bool FlushDirty()
    {
        if (!IsDirty) return true;
        Save();
        return !IsDirty;
    }

    // ---- 变更通知 ----

    public void NotifyContentChanged()
    {
        if (_suppressDirty) return;
        IsDirty = true;
        SetStatus(SaveStatus.Dirty, Loc.Get("Status_Dirty"));
        // 新文档尚无路径，等用户显式 Ctrl+S 再弹保存对话框
        if (FilePath != null)
        {
            _autoSaveTimer.Stop();
            _autoSaveTimer.Start();
        }
    }

    // ---- 任务操作 ----

    public BlockViewModel BlockOf(TaskViewModel task)
        => Blocks.First(b => b.Items.Contains(task));

    public TaskViewModel CreateTask(BlockViewModel block, int index)
    {
        var task = new TaskViewModel(this) { State = block.State };
        // §7.4 / §9：写入创建时间戳（存为续行属性，不在备注编辑器中显示）；任务未被填写就被放弃时按空任务过滤
        task.SetCreated(DateOnly.FromDateTime(DateTime.Today));
        block.InsertTask(index, task);
        SelectedTask = task;
        ExpandTask(task);   // 新任务总是展开待编辑
        TaskCreated?.Invoke(this, task);
        NotifyContentChanged();
        return task;
    }

    private void CreateTaskAtEnd()
    {
        if (_selectedFacet is { } facet)
        {
            // 面板视图下新建：落到 DOING 末尾（§9），归属预填为对应的 项目/标签（结构化属性）
            var doing = Blocks.First(b => b.State == TaskState.Doing);
            var task = CreateTask(doing, int.MaxValue);
            if (facet.Kind == FacetKind.Project) task.SetProject(facet.Name);
            else task.AddTag(facet.Name);
            RefreshFacets();   // 让新草稿出现在面板中
            return;
        }
        if (SelectedBlock == null) return;
        CreateTask(SelectedBlock, int.MaxValue);   // §9：新任务追加到区块末尾
    }

    // ---- 任务流转 ----

    /// <summary>统一流转：把一组任务从各自区块移除并插入目标区块，保持相对顺序。
    /// 插入位置遵循 §9（<see cref="TaskTransitions.InsertsAtTop"/>）：DONE/DELETE 顶部，DOING/WAIT 末尾。</summary>
    /// <param name="normalize">目标为 DONE/DELETE 时按 §9 规范化（移除优先级；进 DONE 追加完成日期）。</param>
    private void TransitionTasks(IReadOnlyList<TaskViewModel> tasks, TaskState target, bool normalize = false)
    {
        if (tasks.Count == 0) return;
        var targetBlock = Blocks.First(b => b.State == target);
        var today = DateOnly.FromDateTime(DateTime.Today);
        foreach (var task in tasks)
        {
            BlockOf(task).RemoveTask(task);
            DetachTask(task);
            if (normalize) NormalizeForTarget(task, target, today);
        }
        if (TaskTransitions.InsertsAtTop(target))
        {
            var index = 0;
            foreach (var task in tasks) targetBlock.InsertTask(index++, task);
        }
        else
        {
            foreach (var task in tasks) targetBlock.InsertTask(int.MaxValue, task);
        }
        SettleSort();
        RefreshFacets();
        NotifyContentChanged();
    }

    /// <summary>§9 规范化：规则在 Core（<see cref="TaskTransitions.NormalizeForState"/>），
    /// 这里只做模型 ↔ 编辑器文本的往返（主行重组为规范顺序，追加的时间戳增量转为任务属性）。</summary>
    private static void NormalizeForTarget(TaskViewModel task, TaskState target, DateOnly today)
    {
        if (task.State == target) return;
        var m = StanzaParser.ParseTaskHeader(task.HeaderText);
        // 优先级/项目/标签都由 VM 结构化属性承载（编辑文本不含记号），交给 Core 规则裁决；
        // 编辑文本可能残留的未提交记号一并并入
        m.Priority = task.Priority;
        m.Project ??= task.ProjectName;
        foreach (var tag in task.Tags)
            if (!m.Tags.Contains(tag)) m.Tags.Add(tag);
        TaskTransitions.NormalizeForState(m, task.State, target, today);
        task.Priority = m.Priority;   // 回读：进入 DONE/DELETE 时 Core 已按 §9 清除
        task.ApplyHeaderModel(m);
        // 主行解析不出备注，m.Notes 即本次规范化追加的时间戳增量（§7.4），转入属性而非备注
        foreach (var line in m.Notes)
        {
            if (!StanzaParser.TryMatchTimestampLine(line, out var date, out var kind)) continue;
            if (kind == TimestampKind.Created) task.SetCreated(date);
            else task.AppendCompleted(date);
        }
    }

    /// <summary>完成：移至 DONE 顶部并规范化（§9）。</summary>
    public void CompleteTask(TaskViewModel task) => TransitionTasks(new[] { task }, TaskState.Done, normalize: true);

    /// <summary>拖拽落点提交（调用方已把任务从原集合移除）。进入 DONE/DELETE 时按 §9 规范化。</summary>
    public void DropTask(TaskViewModel task, BlockViewModel target, int index)
    {
        NormalizeForTarget(task, target.State, DateOnly.FromDateTime(DateTime.Today));
        target.InsertTask(index, task);
        SettleSort();
        RefreshFacets();
        NotifyContentChanged();
    }

    private void DeleteTasksPermanently(IReadOnlyList<TaskViewModel> tasks)
    {
        if (tasks.Count == 0) return;
        foreach (var task in tasks)
        {
            BlockOf(task).RemoveTask(task);
            DetachTask(task);
        }
        RefreshFacets();
        NotifyContentChanged();
    }

    /// <summary>清空当前 DONE/DELETE 区块的全部任务（视图层负责二次确认）。</summary>
    private void ClearSelectedBlock()
    {
        var block = SelectedBlock;
        if (block == null || block.State is not (TaskState.Done or TaskState.Delete)) return;
        foreach (var task in block.Tasks.ToList()) DetachTask(task);
        block.Items.Clear();
        RefreshFacets();
        NotifyContentChanged();
    }

    // ---- 排序 ----

    /// <summary>优先级排序是默认行为：DOING/WAIT 始终保持（优先级 → 截止日期）稳定排序。
    /// 在加载、任务收起、任务流转时自动应用。</summary>
    private void SettleSort()
    {
        var changed = false;
        foreach (var block in Blocks.Where(b => TaskTransitions.IsActiveState(b.State)))
            changed |= ApplySort(block);
        if (changed) NotifyContentChanged();
        RebuildPanel();   // 面板内顺序跟随区块排序
    }

    private static bool ApplySort(BlockViewModel block)
    {
        // 排序键规则在 Core（ActiveTaskOrdering）；OrderBy 稳定，同键任务保持相对顺序（拖拽依赖此特性）
        var sorted = block.Tasks
            .OrderBy(t => t, Comparer<TaskViewModel>.Create(
                (a, b) => ActiveTaskOrdering.Compare(a.Priority, a.Due, b.Priority, b.Due)))
            .ToList();
        if (block.Tasks.SequenceEqual(sorted)) return false;   // 已是有序，不动
        block.Items.Clear();
        foreach (var t in sorted) block.Items.Add(t);
        return true;
    }

    // ---- 内部 ----

    /// <summary>创建区块并挂钩计数变化：任务增减时同步标题区计数与面板计数。</summary>
    private BlockViewModel CreateBlock(TaskState state, bool existedInSource)
    {
        var block = new BlockViewModel(state, existedInSource);
        block.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(BlockViewModel.TaskCount))
            {
                OnPropertyChanged(nameof(ScopeTaskCount));
                OnPropertyChanged(nameof(ScopeHasTasks));
            }
        };
        return block;
    }

    // ---- 项目/标签聚合 ----

    /// <summary>重算侧栏项目/标签列表与面板内容。
    /// 触发点：文档加载/新建、任务增删与流转、任务编辑收起。
    /// 不在主行每次按键时刷新——避免正在编辑的任务因解析结果变化而中途从面板消失。</summary>
    private void RefreshFacets()
    {
        var all = Blocks.SelectMany(b => b.Tasks).ToList();
        RebuildFacetList(Projects, all.Where(t => t.ProjectName != null).Select(t => t.ProjectName!), FacetKind.Project);
        RebuildFacetList(Tags, all.SelectMany(t => t.Tags), FacetKind.Tag);
        OnPropertyChanged(nameof(HasProjects));
        OnPropertyChanged(nameof(HasTags));
        OnPropertyChanged(nameof(ShowProjects));
        OnPropertyChanged(nameof(ShowTags));

        // 当前项目/标签已没有任何任务：退出面板，回到首个有任务的区块
        if (_selectedFacet != null && !Projects.Contains(_selectedFacet) && !Tags.Contains(_selectedFacet))
        {
            SelectedFacet = null;
            SelectedBlock = Blocks.FirstOrDefault(b => b.HasTasks) ?? Blocks.FirstOrDefault();
        }
        RebuildPanel();
        NotifyScopeChanged();
    }

    /// <summary>重建侧栏列表：复用同名实例（选中/悬停状态随之保留），仅更新计数并按需增删移动。</summary>
    private static void RebuildFacetList(
        ObservableCollection<FacetItemViewModel> list, IEnumerable<string> names, FacetKind kind)
    {
        var counts = names
            .GroupBy(n => n, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.Name, StringComparer.Ordinal)
            .ToList();

        for (var i = list.Count - 1; i >= 0; i--)
            if (counts.All(c => c.Name != list[i].Name))
                list.RemoveAt(i);

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
    /// 增量对齐而非清空重填：未变化项保留容器、选中状态与滚动位置，避免视图跳动。</summary>
    private void RebuildPanel()
    {
        var matches = _selectedFacet is { } facet
            ? Blocks.SelectMany(b => b.Tasks).Where(facet.Matches).ToList()
            : new List<TaskViewModel>();
        SyncPanel(matches);
        OnPropertyChanged(nameof(ScopeTaskCount));
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

    /// <summary>标题区、任务区数据源与工具栏作用域属性的统一通知。</summary>
    private void NotifyScopeChanged()
    {
        OnPropertyChanged(nameof(TaskListSource));
        OnPropertyChanged(nameof(ScopeTitle));
        OnPropertyChanged(nameof(ScopeTaskCount));
        OnPropertyChanged(nameof(ScopeHasTasks));
        OnPropertyChanged(nameof(ShowAddTask));
        OnPropertyChanged(nameof(ShowClear));
        OnPropertyChanged(nameof(ScopeIsActive));
        OnPropertyChanged(nameof(ScopeIsDoing));
        OnPropertyChanged(nameof(ScopeIsWaiting));
        OnPropertyChanged(nameof(ScopeIsArchive));
        OnPropertyChanged(nameof(ScopeIsDeleted));
    }

    private void LoadDocument(StanzaDocument doc)
    {
        _suppressDirty = true;
        try
        {
            Blocks.Clear();
            foreach (var state in TaskStateNames.CanonicalOrder)
            {
                var modelBlock = doc.FindBlock(state);
                var block = CreateBlock(state, modelBlock != null);
                if (modelBlock != null)
                    foreach (var t in modelBlock.Tasks)
                        block.Items.Add(TaskViewModel.FromModel(this, t, state));
                Blocks.Add(block);
            }
            SelectedBlock = Blocks.FirstOrDefault(b => b.HasTasks) ?? Blocks[0];
            SelectedTask = null;
            CollapseExpanded();
            SettleSort();
            RefreshFacets();
        }
        finally
        {
            _suppressDirty = false;
        }
    }

    private void SetStatus(SaveStatus kind, string text)
    {
        _statusClearTimer.Stop();
        StatusKind = kind;
        StatusText = text;
        // “已保存”只是确认反馈，几秒后自动隐藏；错误/未保存等状态保持显示
        if (kind == SaveStatus.Saved)
            _statusClearTimer.Start();
    }
}

/// <summary>优先级菜单项：显示文本 + 取值（null 表示清除优先级）。任务右键菜单与底部工具栏共用；
/// Label 按当前语言即时取值（右键菜单每次打开时重新求值，无需刷新）。</summary>
public sealed record PriorityOption(string LabelKey, char? Value)
{
    public string Label => Loc.Get(LabelKey);
}

