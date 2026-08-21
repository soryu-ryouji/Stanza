using System.IO;
using Stanza.App.ViewModels;
using Stanza.Core;

namespace Stanza.App.Tests;

[Collection("AppData")]
public class MainViewModelTests : StaTestHost.StaFactBase
{
    private static MainViewModel NewDoc()
    {
        var vm = new MainViewModel();
        vm.NewDocument();
        return vm;
    }

    [Fact]
    public void NewDocument_CreatesFourBlocksInOrder() => OnUi(() =>
    {
        var vm = NewDoc();

        Assert.Equal(4, vm.Blocks.Count);
        Assert.Equal(
            new[] { TaskState.Doing, TaskState.Wait, TaskState.Done, TaskState.Delete },
            vm.Blocks.Select(b => b.State));
        Assert.Same(vm.Blocks[0], vm.SelectedBlock);
        Assert.True(vm.HasDocument);
        Assert.False(vm.IsDirty);
    });

    [Fact]
    public void CreateTask_AppendsToBlockAndExpands() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = vm.Blocks[0];

        var task = vm.CreateTask(doing, int.MaxValue);

        Assert.Single(doing.Tasks);
        Assert.Contains(task, doing.Items);
        Assert.True(task.IsExpanded);
        Assert.Same(task, vm.SelectedTask);
        Assert.True(task.HasCreated);   // §7.4：创建时写入创建时间戳
        Assert.True(vm.IsDirty);
    });

    [Fact]
    public void CompleteTask_MovesToDoneTopAndNormalizes() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = vm.Blocks[0];
        var task = vm.CreateTask(doing, int.MaxValue);
        task.HeaderText = "(A) 2026-08-18 完成登录模块 +Apollo #紧急 ";

        vm.CompleteTask(task);

        var done = vm.Blocks.First(b => b.State == TaskState.Done);
        Assert.Same(task, done.Tasks.First());     // §9：DONE 插到顶部
        Assert.DoesNotContain(task, doing.Tasks);
        Assert.Equal(TaskState.Done, task.State);
        Assert.Null(task.Priority);                // §9：进 DONE 清优先级
        Assert.True(task.HasCompleted);            // §9：追加完成时间戳
        Assert.Equal("Apollo", task.ProjectName);  // 结构化属性保留
    });

    [Fact]
    public void Undo_RestoresPreviousState() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = vm.Blocks[0];
        var task = vm.CreateTask(doing, int.MaxValue);
        task.HeaderText = "有内容的任务";   // 空草稿无持久化价值（序列化过滤），撤销需以有内容任务为对象
        Assert.Equal(1, doing.TaskCount);

        vm.Undo();

        // Undo 重建区块集合：旧 BlockViewModel 实例被丢弃，需从新集合重新取
        Assert.Equal(0, vm.Blocks.First(b => b.State == TaskState.Doing).TaskCount);
        Assert.True(vm.HasDocument);
    });

    [Fact]
    public void FacetAggregation_ZeroCountRetained_ThenRemoved() => OnUi(() =>
    {
        var vm = NewDoc();
        var doing = vm.Blocks[0];
        var task = vm.CreateTask(doing, int.MaxValue);
        vm.UpdateSelection(new[] { task });
        vm.SetProjectForSelection("Apollo");
        vm.ToggleTag("紧急");

        var project = Assert.Single(vm.Projects);
        Assert.Equal("Apollo", project.Name);
        Assert.Equal(1, project.Count);
        var tag = Assert.Single(vm.Tags);
        Assert.Equal("紧急", tag.Name);
        Assert.Equal(1, tag.Count);

        vm.CompleteTask(task);   // 归档：计数归零，条目保留（显示 0）

        Assert.Single(vm.Projects);
        Assert.Equal(0, vm.Projects[0].Count);
        Assert.Single(vm.Tags);
        Assert.Equal(0, vm.Tags[0].Count);

        vm.DeleteSelectionCommand.Execute(null);   // 永久删除：文档中消失，条目移除

        Assert.Empty(vm.Projects);
        Assert.Empty(vm.Tags);
    });

    [Fact]
    public void SaveOpen_RoundTripsDocument() => OnUi(() =>
    {
        var dir = Path.Combine(Path.GetTempPath(), "stanza-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "todo.stanza");
        File.WriteAllText(path, "# DOING\n\n任务一 +Apollo #紧急\n    备注\n\n");

        var vm = new MainViewModel();
        vm.OpenFile(path);
        Assert.True(vm.HasDocument);
        Assert.False(vm.IsDirty);
        var task = Assert.Single(vm.Blocks[0].Tasks);
        Assert.Equal("任务一", task.Description);
        Assert.Equal("Apollo", task.ProjectName);
        Assert.Equal("备注", task.NotesText);

        task.HeaderText = "2026-08-18 改过的任务";
        vm.Save();
        Assert.False(vm.IsDirty);

        // 结构化属性与备注随保存写回文件
        var text = File.ReadAllText(path);
        Assert.Contains("2026-08-18 改过的任务", text);
        Assert.Contains("+Apollo", text);
        Assert.Contains("#紧急", text);
        Assert.Contains("    备注", text);
    });

    [Fact]
    public void TaskPropertyEdit_MarksDirty() => OnUi(() =>
    {
        // 任务内容变化 → ContentChanged 事件 → NotifyContentChanged 标脏（重构后的事件链路回归）
        var dir = Path.Combine(Path.GetTempPath(), "stanza-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "todo.stanza");
        File.WriteAllText(path, "# DOING\n\n任务一\n\n");

        var vm = new MainViewModel();
        vm.OpenFile(path);
        var task = Assert.Single(vm.Blocks[0].Tasks);
        Assert.False(vm.IsDirty);

        task.Priority = 'A';   // 结构化属性编辑
        Assert.True(vm.IsDirty);

        vm.Save();
        Assert.False(vm.IsDirty);

        task.NotesText = "备注";   // 备注编辑
        Assert.True(vm.IsDirty);
    });
}
