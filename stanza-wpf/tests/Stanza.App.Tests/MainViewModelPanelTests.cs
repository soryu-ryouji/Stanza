using Stanza.App.ViewModels;
using Stanza.Core;

namespace Stanza.App.Tests;

/// <summary>
/// 项目/标签聚合面板测试：facet 选中与区块互斥、面板只含活跃任务、
/// 计数归零退出面板、面板内新建预填归属、面板顺序跟随 SettleSort（SyncPanel 增量对齐）。
/// </summary>
[Collection("AppData")]
public class MainViewModelPanelTests : StaTestHost.StaFactBase
{
    private static MainViewModel NewDoc()
    {
        var vm = new MainViewModel();
        vm.NewDocument();
        return vm;
    }

    private static TaskViewModel AddTask(MainViewModel vm, BlockViewModel block, string header)
    {
        var task = vm.CreateTask(block, int.MaxValue);
        task.HeaderText = header;   // 记号带尾随空格：实时捕获为结构化属性；收起时刷新聚合
        vm.CollapseExpanded();
        return task;
    }

    private static BlockViewModel Block(MainViewModel vm, TaskState state)
        => vm.Blocks.First(b => b.State == state);

    private static FacetItemViewModel SelectProject(MainViewModel vm, string name)
    {
        var facet = vm.Projects.Single(p => p.Name == name);
        vm.SelectedFacet = facet;
        return facet;
    }

    [Fact]
    public void FacetPanel_ShowsMatchingActiveTasks_AndMutexWithBlock() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = Block(vm, TaskState.Doing);
        var a1 = AddTask(vm, doing, "任务一 +Apollo ");
        var a2 = AddTask(vm, doing, "任务二 +Apollo ");
        AddTask(vm, doing, "任务三 +Other ");

        SelectProject(vm, "Apollo");

        Assert.Null(vm.SelectedBlock);                    // 与区块选择互斥
        Assert.Same(vm.PanelView, vm.TaskListSource);     // 任务区切换到分组面板
        Assert.Equal(new[] { a1, a2 }, vm.PanelItems.OfType<TaskViewModel>());   // 只含匹配项
    });

    [Fact]
    public void FacetPanel_TaskLeavesOnComplete_ExitsWhenCountZero() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = Block(vm, TaskState.Doing);
        var a1 = AddTask(vm, doing, "任务一 +Apollo ");
        var a2 = AddTask(vm, doing, "任务二 +Apollo ");
        AddTask(vm, doing, "任务三 +Other ");
        var facet = SelectProject(vm, "Apollo");

        vm.CompleteTask(a1);   // 归档后离开面板（面板只含活跃任务）；计数未归零，面板保持
        Assert.Equal(new[] { a2 }, vm.PanelItems.OfType<TaskViewModel>());
        Assert.Same(facet, vm.SelectedFacet);

        vm.CompleteTask(a2);   // 浏览中的 facet 计数归零：退出面板，回到首个有任务的区块
        Assert.Null(vm.SelectedFacet);
        Assert.Same(doing, vm.SelectedBlock);
    });

    [Fact]
    public void NewTask_InFacetPanel_PrefillsFacetAndAppearsInPanel() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = Block(vm, TaskState.Doing);
        AddTask(vm, doing, "任务一 +Apollo ");
        SelectProject(vm, "Apollo");

        vm.NewTaskCommand.Execute(null);

        var task = doing.Tasks.Last();   // §9：新任务落到 DOING 末尾
        Assert.Equal("Apollo", task.ProjectName);   // 归属预填为当前项目（结构化属性）
        Assert.Contains(task, vm.PanelItems.OfType<TaskViewModel>());
        Assert.True(task.IsExpanded);
    });

    [Fact]
    public void PanelOrder_FollowsSettleSort() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = Block(vm, TaskState.Doing);
        var x = AddTask(vm, doing, "X +Apollo ");
        var y = AddTask(vm, doing, "Y +Apollo ");
        SelectProject(vm, "Apollo");
        Assert.Equal(new[] { x, y }, vm.PanelItems.OfType<TaskViewModel>());

        vm.UpdateSelection(new[] { y });
        vm.SetPriorityForSelection('A');   // 排序键变化 → SettleSort → 面板增量对齐（移动错位项）

        Assert.Equal(new[] { y, x }, doing.Tasks);
        Assert.Equal(new[] { y, x }, vm.PanelItems.OfType<TaskViewModel>());
    });
}
