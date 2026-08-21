using Stanza.Core;

namespace Stanza.App.ViewModels;

/// <summary>
/// 撤销：文档文本快照栈（SerializeDocument 是唯一序列化路径）。变更操作入口统一打点，
/// 栈顶去重（无实际变更不产生撤销步）；拖拽等先变更后提交的操作由视图在变更开始前打点。
/// 撤销恢复快照后保持当前区块视图，视图可接管为回归动画（UndoRequested）。
/// </summary>
public sealed partial class MainViewModel
{
    // ---- 撤销 ----

    /// <summary>撤销栈容量上限。</summary>
    private const int UndoDepth = 100;

    /// <summary>文档文本快照栈：栈顶是最近一次操作前的状态（操作入口统一打点，见 PushUndoSnapshot）。</summary>
    private readonly Stack<string> _undoStack = new();

    /// <summary>变更操作前打点：当前状态入撤销栈。与栈顶相同则跳过（无效/重复快照自动去重，
    /// 无实际变更的操作（如收起未编辑的任务、取消拖拽）不产生撤销步）。拖拽等先变更后提交的操作
    /// 由视图在变更开始前调用（StartTaskDrag），保证快照是操作前状态而非中间态。</summary>
    public void PushUndoSnapshot()
    {
        if (_suppressDirty || !HasDocument) return;
        var text = SerializeDocument();
        if (_undoStack.Count > 0 && _undoStack.Peek() == text) return;
        _undoStack.Push(text);
        if (_undoStack.Count > UndoDepth)
        {
            // 容量裁剪：丢弃最旧快照（栈底）。ToArray 为顶→底序，逆序回填保持原顺序
            var keep = _undoStack.ToArray().Take(UndoDepth).Reverse().ToList();
            _undoStack.Clear();
            foreach (var item in keep) _undoStack.Push(item);
        }
    }

    /// <summary>撤销请求：视图接管为动画流程（回归任务播浮现动画）；未接管时直接恢复。</summary>
    public Action? UndoRequested { get; set; }

    /// <summary>撤销上一操作：恢复前一文档快照，保持当前区块视图。</summary>
    public void Undo()
    {
        var current = SerializeDocument();
        while (_undoStack.Count > 0 && _undoStack.Peek() == current)
            _undoStack.Pop();   // 防御：与当前一致的快照无恢复价值
        if (_undoStack.Count == 0) return;
        var scope = SelectedBlock?.State;
        LoadDocument(StanzaParser.Parse(_undoStack.Pop()), clearUndo: false);
        if (scope is { } s && SelectedBlock?.State != s)
            SelectedBlock = Blocks.First(b => b.State == s);
        // 焦点可能随任务重建落空：强制广播一次区块变更，让视图把焦点停回列表（同切区块路径）
        OnPropertyChanged(nameof(SelectedBlock));
        NotifyContentChanged();   // 撤销本身是变更：标脏并触发自动保存
    }
}
