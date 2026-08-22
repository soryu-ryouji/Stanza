using Stanza.App.ViewModels;
using Stanza.Core;

namespace Stanza.App.Tests;

/// <summary>
/// 任务流转编排测试：TransitionTasks 的插入位置与相对顺序、§9 规范化、
/// SettleSort 键序与稳定性、DropTask、撤销快照、展开/收起（空草稿移除与记号提交）。
/// </summary>
[Collection("AppData")]
public class MainViewModelTransitionTests : StaTestHost.StaFactBase
{
    private static MainViewModel NewDoc()
    {
        var vm = new MainViewModel();
        vm.NewDocument();
        return vm;
    }

    /// <summary>创建有内容的任务并收起提交（空草稿会在收起时被移除，故必须填内容）。</summary>
    private static TaskViewModel AddTask(MainViewModel vm, BlockViewModel block, string header)
    {
        var task = vm.CreateTask(block, int.MaxValue);
        task.HeaderText = header;
        vm.CollapseExpanded();
        return task;
    }

    private static BlockViewModel Block(MainViewModel vm, TaskState state)
        => vm.Blocks.First(b => b.State == state);

    [Fact]
    public void CompleteTasks_Multiple_InsertAtDoneTopKeepRelativeOrder() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = Block(vm, TaskState.Doing);
        var e = AddTask(vm, doing, "先做");
        vm.CompleteTask(e);   // DONE 已有任务 E

        var a = AddTask(vm, doing, "A");
        var b = AddTask(vm, doing, "B");
        var c = AddTask(vm, doing, "C");
        var d = AddTask(vm, doing, "D");

        vm.CompleteTasks(new[] { b, d });   // §9：多任务进 DONE 置顶，保持相对顺序

        Assert.Equal(new[] { b, d, e }, Block(vm, TaskState.Done).Tasks);
        Assert.Equal(new[] { a, c }, doing.Tasks);
        Assert.Equal(TaskState.Done, b.State);
    });

    [Fact]
    public void RestoreTask_AppendsToDoingEnd_AndKeepsCompletionHistory() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = Block(vm, TaskState.Doing);
        var a = AddTask(vm, doing, "甲");
        var b = AddTask(vm, doing, "乙");
        vm.CompleteTask(a);

        vm.RestoreTask(a);   // §9：恢复追加到 DOING 末尾；不规范化（完成历史按 §7.4.3 保留）

        Assert.Equal(new[] { b, a }, doing.Tasks);
        Assert.Equal(TaskState.Doing, a.State);
        Assert.True(a.HasCompleted);
        Assert.Null(a.Priority);
    });

    [Fact]
    public void MoveSelectionTo_SameState_IsNoOp() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = Block(vm, TaskState.Doing);
        var a = AddTask(vm, doing, "甲");
        var b = AddTask(vm, doing, "乙");
        vm.UpdateSelection(new[] { a });

        vm.MoveSelectionTo(TaskState.Doing);   // 全部已在目标状态：不动作（避免位置扰动）

        Assert.Equal(new[] { a, b }, doing.Tasks);

        vm.MoveSelectionTo(TaskState.Wait);   // 异状态正常流转：追加到 WAIT 末尾（§9）
        Assert.Equal(new[] { b }, doing.Tasks);
        Assert.Equal(new[] { a }, Block(vm, TaskState.Wait).Tasks);
    });

    [Fact]
    public void DropTask_ToDone_NormalizesAndUpdatesState() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = Block(vm, TaskState.Doing);
        var task = AddTask(vm, doing, "(A) 拖拽我");
        Assert.Equal('A', task.Priority);   // 行首完整优先级单元已被实时捕获

        // 拖拽语义：调用方（视图）已把任务从原集合移除，DropTask 负责规范化与落位
        doing.RemoveTask(task);
        vm.DropTask(task, Block(vm, TaskState.Done), 0);

        var done = Block(vm, TaskState.Done);
        Assert.Same(task, done.Tasks.First());
        Assert.Equal(TaskState.Done, task.State);   // InsertTask 同步状态
        Assert.Null(task.Priority);                 // §9：进 DONE 清优先级
        Assert.True(task.HasCompleted);             // §9：追加完成时间戳
    });

    [Fact]
    public void SetDueForSelection_SetsDueAndResorts() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = Block(vm, TaskState.Doing);
        var a = AddTask(vm, doing, "甲");
        var b = AddTask(vm, doing, "乙");
        Assert.Equal(new[] { a, b }, doing.Tasks);   // 同键稳定序

        vm.UpdateSelection(new[] { b });
        var today = DateOnly.FromDateTime(DateTime.Today);
        vm.SetDueForSelection(today);

        Assert.Equal(today, b.Due);
        Assert.Equal(new[] { b, a }, doing.Tasks);   // 截止是排序键：有日期的提前

        vm.SetDueForSelection(null);   // 清除（b 仍在选中集）
        Assert.Null(b.Due);
    });

    [Fact]
    public void ActiveTasks_SettleSort_ByQuadrantThenDue() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = Block(vm, TaskState.Doing);
        var plain = AddTask(vm, doing, "普通");
        var quadrantC = AddTask(vm, doing, "(C) 丙");
        var lateA = AddTask(vm, doing, "(A) 2026-09-01 晚截止");
        var earlyA = AddTask(vm, doing, "(A) 2026-08-01 早截止");
        var earlyNoQuadrant = AddTask(vm, doing, "2026-07-01 无优先级早截止");
        Assert.Equal('A', earlyA.Priority);   // 排序键来自实时捕获的结构化属性

        // RFC §7.2.1：象限字母升序（无优先级排尾）→ 截止日期升序（无日期排尾）
        Assert.Equal(
            new[] { earlyA, lateA, quadrantC, earlyNoQuadrant, plain },
            doing.Tasks);
    });

    [Fact]
    public void SettleSort_StableAcrossPriorityToggle() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = Block(vm, TaskState.Doing);
        var a = AddTask(vm, doing, "甲");
        var b = AddTask(vm, doing, "乙");
        var c = AddTask(vm, doing, "丙");
        Assert.Equal(new[] { a, b, c }, doing.Tasks);   // 同键保持插入序（稳定）

        vm.UpdateSelection(new[] { b });
        vm.SetPriorityForSelection('A');
        Assert.Equal(new[] { b, a, c }, doing.Tasks);   // 排序键变化后重排

        vm.SetPriorityForSelection(null);
        // 回到同键：OrderBy 稳定排序保持当前相对顺序（拖拽排序依赖此特性），不回到最初插入序
        Assert.Equal(new[] { b, a, c }, doing.Tasks);
    });

    [Fact]
    public void Undo_RestoresTransitionSnapshot() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = Block(vm, TaskState.Doing);
        var task = AddTask(vm, doing, "(A) 撤销我");

        vm.CompleteTask(task);
        Assert.Empty(doing.Tasks);

        vm.Undo();

        // 文本快照撤销天然覆盖规范化：任务回到 DOING，优先级恢复，完成时间戳消失（实例已重建，重新取）
        var restored = Assert.Single(Block(vm, TaskState.Doing).Tasks);
        Assert.Equal('A', restored.Priority);
        Assert.False(restored.HasCompleted);
        Assert.Empty(Block(vm, TaskState.Done).Tasks);
    });

    [Fact]
    public void CollapseExpanded_RemovesEmptyDraft() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = Block(vm, TaskState.Doing);
        var task = vm.CreateTask(doing, int.MaxValue);   // 创建时间戳不计为内容（IsEmpty）

        vm.CollapseExpanded();

        Assert.Empty(doing.Tasks);          // 空草稿（未填写任何内容）随收起移除
        Assert.Null(vm.SelectedTask);
        Assert.Null(vm.ExpandedTask);
    });

    [Fact]
    public void CollapseExpanded_CommitsTokensAndRefreshesFacets() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = Block(vm, TaskState.Doing);
        var task = vm.CreateTask(doing, int.MaxValue);
        // +Work 带尾随空格被实时捕获；#例行 在行尾无尾随空白，收起时由 CommitHeader 捕获
        task.HeaderText = "写周报 +Work #例行";

        vm.CollapseExpanded();

        Assert.Contains(task, doing.Tasks);   // 非空任务保留
        Assert.Equal("Work", task.ProjectName);
        Assert.Contains("例行", task.Tags);
        // 编辑落定后聚合刷新：侧栏出现对应条目
        Assert.Equal("Work", Assert.Single(vm.Projects).Name);
        Assert.Equal("例行", Assert.Single(vm.Tags).Name);
    });

    [Fact]
    public void ExpandTask_CollapsesPrevious() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = Block(vm, TaskState.Doing);
        var a = AddTask(vm, doing, "甲");
        var b = AddTask(vm, doing, "乙");

        vm.ExpandTask(a);
        Assert.Same(a, vm.ExpandedTask);
        Assert.True(a.IsExpanded);

        vm.ExpandTask(b);   // 展开至多一个：收起前者
        Assert.False(a.IsExpanded);
        Assert.Same(b, vm.ExpandedTask);
        Assert.True(b.IsExpanded);
    });
}
