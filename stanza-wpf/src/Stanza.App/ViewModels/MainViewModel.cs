using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Stanza.App.Services;
using Stanza.Core;

namespace Stanza.App.ViewModels;

public sealed partial class MainViewModel : ViewModelBase
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

    public MainViewModel()
    {
        SaveCommand = new RelayCommand(_ => Save(), _ => HasDocument);
        OpenCommand = new RelayCommand(_ => OpenInteractive());
        NewDocumentCommand = new RelayCommand(_ => NewDocument());
        OpenRecentCommand = new RelayCommand(_ => OpenRecentRequested?.Invoke());
        UndoCommand = new RelayCommand(
            _ =>
            {
                if (UndoRequested is { } animate) animate();
                else Undo();
            },
            _ => _undoStack.Count > 0);
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
        CompleteSelectionCommand = new RelayCommand(
            _ =>
            {
                if (CompleteSelectionRequested is { } animate) animate();
                else CompleteTasks(SelectedTasks.ToList());
            },
            _ => HasSelection);
        DiscardSelectionCommand = new RelayCommand(
            _ => TransitionTasks(SelectedTasks.ToList(), TaskState.Delete, normalize: true),
            _ => HasSelection && ScopeState != TaskState.Delete);   // 已废弃的任务无需再废弃
        RestoreSelectionCommand = new RelayCommand(_ => TransitionTasks(SelectedTasks.ToList(), TaskState.Doing), _ => HasSelection);
        DeferSelectionCommand = new RelayCommand(_ => TransitionTasks(SelectedTasks.ToList(), TaskState.Wait), _ => HasSelection);
        ActivateSelectionCommand = new RelayCommand(_ => TransitionTasks(SelectedTasks.ToList(), TaskState.Doing), _ => HasSelection);
        DeleteSelectionCommand = new RelayCommand(_ => DeleteTasksPermanently(SelectedTasks.ToList()), _ => HasSelection);
        SetPriorityCommand = new RelayCommand(
            p => { if (p is PriorityOption option) SetPriorityForSelection(option.Value); },
            _ => HasSelection);
        SetStateCommand = new RelayCommand(
            p => { if (p is StateOption option) MoveSelectionTo(option.State); },
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

        // 语言切换：区块显示名（侧栏）与面板分组头（转换器）随当前语言重算
        Loc.Changed += (_, _) =>
        {
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

    // 工具栏与空态的作用域属性：区块模式取区块状态，面板模式取面板/首个选中任务的状态
    public bool ScopeHasTasks => _selectedFacet != null ? _panelTasks.Count > 0 : _selectedBlock?.HasTasks == true;
    public bool ShowAddTask => _selectedFacet != null || _selectedBlock?.IsActiveList == true;
    public bool ShowClear => _selectedFacet == null && _selectedBlock?.IsArchiveList == true;
    /// <summary>清空按钮：归档区块且无选中时显示（有选中时工具栏切换为任务操作）。</summary>
    public bool ShowClearButton => ShowClear && !HasSelection;

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

    /// <summary>选中中包含活跃任务（优先级只属于活跃任务：全归档选中时优先级面板键不响应）。</summary>
    public bool HasActiveSelection => _selectedTasks.Any(t => t.IsActive);

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
    public ICommand UndoCommand { get; }
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
    public ICommand SetStateCommand { get; }

    /// <summary>键位表命令 ID → 命令实例（Keymap 分发用；ID 是后续用户键位文件的稳定标识）。
    /// 仅含应用级命令；任务作用域命令（含 NewTask）经 TryExecuteTaskCommand 的焦点检查分发，不走此映射。</summary>
    public ICommand? CommandFor(AppCommand command) => command switch
    {
        AppCommand.Save => SaveCommand,
        AppCommand.Open => OpenCommand,
        AppCommand.NewDocument => NewDocumentCommand,
        AppCommand.OpenRecent => OpenRecentCommand,
        AppCommand.Undo => UndoCommand,
        AppCommand.SelectBlock => SelectBlockCommand,
        _ => null,
    };

    // ---- 优先级 ----

    /// <summary>优先级菜单选项（任务右键菜单与底部工具栏共用同一套选项与样式）。</summary>
    public IReadOnlyList<PriorityOption> PriorityOptions { get; } = BuildPriorityOptions();

    /// <summary>状态子菜单选项（右键菜单直接展开四状态，按规范序；Label 随当前语言即时取值）。</summary>
    public IReadOnlyList<StateOption> StateOptions { get; } =
        TaskStateNames.CanonicalOrder.Select(s => new StateOption(s)).ToList();

    private static IReadOnlyList<PriorityOption> BuildPriorityOptions() => new[]
    {
        new PriorityOption("Priority_A", 'A'),
        new PriorityOption("Priority_B", 'B'),
        new PriorityOption("Priority_C", 'C'),
        new PriorityOption("Priority_D", 'D'),
        new PriorityOption("Priority_Clear", null),
    };

    /// <summary>设置选中任务的优先级（仅限活跃状态任务；null = 清除）。
    /// 右键子菜单（PriorityOption）与优先级面板（字母直达）共用的唯一入口。</summary>
    public void SetPriorityForSelection(char? value)
    {
        var targets = SelectedTasks.Where(t => t.IsActive).ToList();
        if (targets.Count == 0) return;
        PushUndoSnapshot();
        foreach (var t in targets) t.Priority = value;
        SettleSort();   // 排序键变化后重排（象限 → 截止日期）
    }

    /// <summary>设置选中任务的截止日（仅限活跃状态任务；null = 清除）。
    /// 日期选择器（浮层/右键菜单/工具栏）的唯一入口。</summary>
    public void SetDueForSelection(DateOnly? date)
    {
        var targets = SelectedTasks.Where(t => t.IsActive).ToList();
        if (targets.Count == 0) return;
        PushUndoSnapshot();
        foreach (var t in targets) t.Due = date;
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
        PushUndoSnapshot();   // 提交点：编辑内容随收起入模型（空草稿随后移除，快照自动去重）
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

    // ---- 任务操作 ----

    public BlockViewModel BlockOf(TaskViewModel task)
        => Blocks.First(b => b.Items.Contains(task));

    public TaskViewModel CreateTask(BlockViewModel block, int index)
    {
        PushUndoSnapshot();
        var task = Track(new TaskViewModel { State = block.State });
        // §7.4 / §9：写入创建时间戳（存为续行属性，不在备注编辑器中显示）；任务未被填写就被放弃时按空任务过滤
        task.SetCreated(DateOnly.FromDateTime(DateTime.Today));
        block.InsertTask(index, task);
        SelectedTask = task;
        ExpandTask(task);   // 新任务总是展开待编辑
        TaskCreated?.Invoke(this, task);
        NotifyContentChanged();
        return task;
    }

    /// <summary>挂接任务的内容变化通知：任务级变化 = 文档级变化（脏追踪/自动保存入口）。
    /// 全部实例化路径必须经此方法挂接，使「挂接」成为结构而非约定。
    /// 时序约束：挂接紧跟构造——CreateTask 的 SetCreated 即走通知路径。</summary>
    private TaskViewModel Track(TaskViewModel task)
    {
        task.ContentChanged += OnTaskContentChanged;
        return task;
    }

    private void OnTaskContentChanged(object? sender, EventArgs e) => NotifyContentChanged();

    private void CreateTaskAtEnd()
    {
        if (_selectedFacet is { } facet)
        {
            // 面板视图下新建：落到 DOING 末尾（§9），归属预填为对应的 项目/标签（结构化属性）
            var doing = Blocks.First(b => b.State == TaskState.Doing);
            var task = CreateTask(doing, int.MaxValue);
            if (facet.Kind == FacetKind.Project)
            {
                task.SetProject(facet.Name);
                task.MarkPrefilled(facet.Name, null);
            }
            else
            {
                task.AddTag(facet.Name);
                task.MarkPrefilled(null, facet.Name);
            }
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
        PushUndoSnapshot();
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
        // 优先级/项目/标签/截止日都由 VM 结构化属性承载（编辑文本不含记号），交给 Core 规则裁决；
        // 编辑文本可能残留的未提交记号一并并入
        m.Priority = task.Priority;
        m.DueDate = task.Due;
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

    /// <summary>恢复：移回 DOING 末尾（§9）。已完成任务勾选框取消勾选的路径，不播动画。</summary>
    public void RestoreTask(TaskViewModel task) => TransitionTasks(new[] { task }, TaskState.Doing);

    /// <summary>完成一组任务（§9：移至 DONE 顶部并规范化，保持相对顺序）。</summary>
    public void CompleteTasks(IReadOnlyList<TaskViewModel> tasks) => TransitionTasks(tasks, TaskState.Done, normalize: true);

    /// <summary>完成选中任务的请求：视图接管为动画流程；未接管时直接流转（供非动画上下文）。</summary>
    public Action? CompleteSelectionRequested { get; set; }

    /// <summary>「移到…」浮层的统一流转入口：跳过已在目标状态的任务（全部已在时不动作，
    /// 避免同状态移除重插造成的位置扰动）。normalize 与逐按钮路径语义一致：
    /// 进 DONE 追加完成时间戳并清优先级，进 DELETE 清优先级，活跃状态无额外变更。</summary>
    public void MoveSelectionTo(TaskState target)
    {
        var tasks = SelectedTasks.Where(t => t.State != target).ToList();
        if (tasks.Count == 0) return;
        TransitionTasks(tasks, target, normalize: true);
    }

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
        PushUndoSnapshot();
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
        PushUndoSnapshot();
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
                OnPropertyChanged(nameof(ScopeHasTasks));
            }
        };
        return block;
    }


    private void LoadDocument(StanzaDocument doc) => LoadDocument(doc, clearUndo: true);

    private void LoadDocument(StanzaDocument doc, bool clearUndo)
    {
        _suppressDirty = true;
        try
        {
            if (clearUndo) _undoStack.Clear();
            Blocks.Clear();
            foreach (var state in TaskStateNames.CanonicalOrder)
            {
                var modelBlock = doc.FindBlock(state);
                var block = CreateBlock(state, modelBlock != null);
                if (modelBlock != null)
                    foreach (var t in modelBlock.Tasks)
                        block.Items.Add(Track(TaskViewModel.FromModel(t, state)));
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

    /// <summary>菜单左侧色点：象限色（与标题文字同色）；无优先级为 Transparent 占位——
    /// 不画标识但保留图标位，文本与象限行左对齐。</summary>
    public Brush DotBrush => Value is { } q ? QuadrantToBrushConverter.Of(q) : Brushes.Transparent;
}

/// <summary>状态子菜单项：状态 + 本地化名称（Label 按当前语言即时取值，右键菜单每次打开时重新求值）。</summary>
public sealed record StateOption(TaskState State)
{
    public string Label => Loc.StateName(State);

    /// <summary>菜单左侧色点：状态色（与分组头/状态选择面板同一取色）。</summary>
    public Brush DotBrush => StateToBrushConverter.Of(State);
}

