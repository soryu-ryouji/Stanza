using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows.Input;
using System.Windows.Threading;
using Stanza.Core;

namespace Stanza.App.ViewModels;

public sealed class MainViewModel : ViewModelBase
{
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer _statusClearTimer;
    private bool _suppressDirty;

    private BlockViewModel? _selectedBlock;
    private TaskViewModel? _selectedTask;
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
        NewTaskCommand = new RelayCommand(_ => CreateTaskAtEnd(), _ => HasDocument && SelectedBlock != null);
        SelectBlockCommand = new RelayCommand(p =>
        {
            if (p is string s && int.TryParse(s, out var i) && i >= 1 && i <= Blocks.Count)
                SelectedBlock = Blocks[i - 1];
        });
        ClearBlockCommand = new RelayCommand(
            _ => ClearSelectedBlock(),
            _ => HasDocument && SelectedBlock is { HasTasks: true } b
                && b.State is TaskState.Done or TaskState.Delete);
        CompleteSelectionCommand = new RelayCommand(_ => TransitionTasks(SelectedTasks.ToList(), TaskState.Done, normalize: true), _ => HasSelection);
        DiscardSelectionCommand = new RelayCommand(
            _ => TransitionTasks(SelectedTasks.ToList(), TaskState.Delete, normalize: true),
            _ => HasSelection && SelectedBlock?.IsDeleted != true);   // 已在 DELETE 区块时无需再废弃
        RestoreSelectionCommand = new RelayCommand(_ => TransitionTasks(SelectedTasks.ToList(), TaskState.Doing), _ => HasSelection);
        DeferSelectionCommand = new RelayCommand(_ => TransitionTasks(SelectedTasks.ToList(), TaskState.Wait), _ => HasSelection);
        ActivateSelectionCommand = new RelayCommand(_ => TransitionTasks(SelectedTasks.ToList(), TaskState.Doing), _ => HasSelection);
        DeleteSelectionCommand = new RelayCommand(_ => DeleteTasksPermanently(SelectedTasks.ToList()), _ => HasSelection);

        Recents = new RecentFilesViewModel(
            openFile: OpenFile,
            notifyMissing: _ => SetStatus(SaveStatus.Info, "文件已不存在，已从列表移除"));

        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _autoSaveTimer.Tick += (_, _) => { _autoSaveTimer.Stop(); Save(); };

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
            // 切走区块时展开的空草稿视为放弃（焦点已离开），直接移除；非空任务保持展开状态
            if (ExpandedTask != null && ExpandedTask.IsEmpty)
                CollapseExpanded();
        }
    }

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

    // ---- 命令 ----

    public ICommand SaveCommand { get; }
    public ICommand OpenCommand { get; }
    public ICommand NewDocumentCommand { get; }
    public ICommand NewTaskCommand { get; }
    public ICommand SelectBlockCommand { get; }
    public ICommand ClearBlockCommand { get; }
    public ICommand CompleteSelectionCommand { get; }
    public ICommand DiscardSelectionCommand { get; }
    public ICommand RestoreSelectionCommand { get; }
    public ICommand DeferSelectionCommand { get; }
    public ICommand ActivateSelectionCommand { get; }
    public ICommand DeleteSelectionCommand { get; }

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
        SettleSort();
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
                doc.Warnings.Count > 0 ? $"已忽略 {doc.Warnings.Count} 行无效内容" : "");
        }
        catch (Exception ex)
        {
            SetStatus(SaveStatus.Error, $"打开失败：{ex.Message}");
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
                Blocks.Add(new BlockViewModel(state, existedInSource: true));
            SelectedBlock = Blocks[0];
            SelectedTask = null;
            CollapseExpanded();
            FilePath = null;
            FileName = "未命名.stanza";
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
            SetStatus(SaveStatus.Saving, "保存中…");
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
            SetStatus(SaveStatus.Saved, $"已保存 {DateTime.Now:HH:mm}");
        }
        catch (Exception ex)
        {
            SetStatus(SaveStatus.Error, $"保存失败：{ex.Message}");
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
        SetStatus(SaveStatus.Dirty, "未保存更改");
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
        NotifyContentChanged();
    }

    /// <summary>§9 规范化：规则在 Core（<see cref="TaskTransitions.NormalizeForState"/>），
    /// 这里只做模型 ↔ 编辑器文本的往返（主行重组为规范顺序，追加的时间戳增量转为任务属性）。</summary>
    private static void NormalizeForTarget(TaskViewModel task, TaskState target, DateOnly today)
    {
        if (task.State == target) return;
        var m = StanzaParser.ParseTaskHeader(task.HeaderText);
        TaskTransitions.NormalizeForState(m, task.State, target, today);
        task.HeaderText = StanzaWriter.ComposeTaskHeader(m);
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
        NotifyContentChanged();
    }

    /// <summary>清空当前 DONE/DELETE 区块的全部任务（视图层负责二次确认）。</summary>
    private void ClearSelectedBlock()
    {
        var block = SelectedBlock;
        if (block == null || block.State is not (TaskState.Done or TaskState.Delete)) return;
        foreach (var task in block.Tasks.ToList()) DetachTask(task);
        block.Items.Clear();
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

    private void LoadDocument(StanzaDocument doc)
    {
        _suppressDirty = true;
        try
        {
            Blocks.Clear();
            foreach (var state in TaskStateNames.CanonicalOrder)
            {
                var modelBlock = doc.FindBlock(state);
                var block = new BlockViewModel(state, modelBlock != null);
                if (modelBlock != null)
                    foreach (var t in modelBlock.Tasks)
                        block.Items.Add(TaskViewModel.FromModel(this, t, state));
                Blocks.Add(block);
            }
            SelectedBlock = Blocks.FirstOrDefault(b => b.HasTasks) ?? Blocks[0];
            SelectedTask = null;
            CollapseExpanded();
            SettleSort();
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
