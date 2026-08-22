using Stanza.App.ViewModels;
using Stanza.Core;

namespace Stanza.App.Tests;

// TaskViewModel 是纯文本逻辑（依赖仅 Stanza.Core 与 BCL），不需要 STA/WPF 环境与文件隔离
public class TaskViewModelTests
{
    private static TaskViewModel NewTask() => new();

    [Fact]
    public void FromModel_ToModel_RoundTripsFullTask()
    {
        var model = new StanzaTask
        {
            Priority = 'A',
            DueDate = new DateOnly(2026, 8, 18),
            Description = "完成登录模块",
            Project = "Apollo",
        };
        model.Tags.Add("紧急");
        model.Tags.Add("后端");
        model.Notes.Add("    2026-08-15 创建");
        model.Notes.Add("    plain note");

        var task = TaskViewModel.FromModel(model, TaskState.Doing);

        // GUI 编辑文本只含描述：优先级/项目/标签/截止日为结构化属性
        Assert.Equal("完成登录模块", task.HeaderText);
        Assert.Equal('A', task.Priority);
        Assert.Equal("Apollo", task.ProjectName);
        Assert.Equal(new[] { "紧急", "后端" }, task.Tags);
        Assert.True(task.HasCreated);
        Assert.Equal("plain note", task.NotesText);   // 备注去公共缩进

        var round = task.ToModel();
        Assert.Equal('A', round.Priority);
        Assert.Equal(new DateOnly(2026, 8, 18), round.DueDate);
        Assert.Equal("完成登录模块", round.Description);
        Assert.Equal("Apollo", round.Project);
        Assert.Equal(new[] { "紧急", "后端" }, round.Tags);
        Assert.Equal(new DateOnly(2026, 8, 15), round.CreatedAt);
        // 时间戳以续行形式写回（创建在前，备注随后），备注重新加 4 空格缩进
        Assert.Equal(new[] { "    2026-08-15 创建", "    plain note" }, round.Notes);
    }

    [Fact]
    public void TypedTokens_AreCapturedAndHiddenFromEditableText()
    {
        var task = NewTask();
        task.HeaderText = "(A) 2026-08-18 完成登录模块 +Apollo #紧急 ";

        // 输入完成的记号（优先级前缀/日期前缀/带尾随空白的项目标签）被实时捕获移除，
        // 编辑文本只剩描述（剥除只移除记号本身，残留空白由解析/提交阶段归并）
        Assert.Equal("完成登录模块", task.HeaderText.TrimEnd());
        Assert.DoesNotContain("(A)", task.HeaderText);
        Assert.DoesNotContain("2026-08-18", task.HeaderText);
        Assert.DoesNotContain("+Apollo", task.HeaderText);
        Assert.DoesNotContain("#紧急", task.HeaderText);
        Assert.Equal('A', task.Priority);
        Assert.Equal(new DateOnly(2026, 8, 18), task.Due);
        Assert.Equal("完成登录模块", task.Description);
        Assert.Equal("Apollo", task.ProjectName);
        Assert.Equal(new[] { "紧急" }, task.Tags);
    }

    [Fact]
    public void CommitHeader_CapturesLineEndTokens()
    {
        var task = NewTask();
        task.HeaderText = "完成登录模块 +Apollo #紧急";   // 行尾无尾随空格：实时捕获不触发

        task.CommitHeader();   // 收起时捕获行尾残留记号

        Assert.Equal("完成登录模块", task.HeaderText);
        var m = task.ToModel();
        Assert.Equal("Apollo", m.Project);
        Assert.Equal(new[] { "紧急" }, m.Tags);
    }

    [Fact]
    public void Timestamps_AreSeparatedFromNotesAndPreserved()
    {
        var model = new StanzaTask { Description = "任务" };
        model.Notes.Add("    2026-08-01 创建");
        model.Notes.Add("    2026-08-02 完成");
        model.Notes.Add("    2026-08-03 完成");
        model.Notes.Add("    普通备注");

        var task = TaskViewModel.FromModel(model, TaskState.Done);

        Assert.True(task.HasCreated);
        Assert.Contains("2026-08-01", task.CreatedDisplay);
        Assert.True(task.HasCompleted);
        Assert.Contains("2026-08-03", task.CompletedDisplay);   // 完成取最后一条
        Assert.Equal("普通备注", task.NotesText);                 // 时间戳行不进备注

        var round = task.ToModel();
        // 创建在前、完成历史随后、备注最后；时间戳行重新以规范缩进写出
        Assert.Equal(
            new[] { "    2026-08-01 创建", "    2026-08-02 完成", "    2026-08-03 完成", "    普通备注" },
            round.Notes);
    }

    [Fact]
    public void IsEmpty_ReflectsContent()
    {
        var task = NewTask();
        Assert.True(task.IsEmpty);

        task.NotesText = "   ";
        Assert.True(task.IsEmpty);   // 纯空白备注不算内容

        task.SetProject("P");
        Assert.False(task.IsEmpty);   // 归属信息计为内容（防止误弃草稿）
    }

    [Fact]
    public void DisplayQuadrant_OnlyForActiveTasks()
    {
        var task = NewTask();
        task.Priority = 'A';
        Assert.Equal('A', task.DisplayQuadrant);   // DOING 默认状态

        task.State = TaskState.Done;
        Assert.Null(task.DisplayQuadrant);   // 归档任务标题不按象限着色
    }

    [Fact]
    public void CommitHeader_CapturesLoneDateLine()
    {
        var task = NewTask();
        task.HeaderText = "2026-08-18";   // 整行日期、无尾随空格：实时捕获不触发

        task.CommitHeader();   // 收起时按截止日接管

        Assert.Equal(new DateOnly(2026, 8, 18), task.Due);
        Assert.Equal("", task.HeaderText);
        Assert.Equal(new DateOnly(2026, 8, 18), task.ToModel().DueDate);
    }

    [Fact]
    public void DueUrgency_GradesByDistanceToToday()
    {
        var task = NewTask();
        var today = DateOnly.FromDateTime(DateTime.Today);
        Assert.Equal(DueUrgency.None, task.Urgency);   // 无截止日

        task.HeaderText = $"{today:yyyy-MM-dd} 任务";
        Assert.Equal(DueUrgency.Today, task.Urgency);

        task.HeaderText = $"{today.AddDays(2):yyyy-MM-dd} 任务";
        Assert.Equal(DueUrgency.Soon, task.Urgency);   // 明天起 3 天内

        task.HeaderText = $"{today.AddDays(10):yyyy-MM-dd} 任务";
        Assert.Equal(DueUrgency.Far, task.Urgency);

        task.HeaderText = $"{today.AddDays(-1):yyyy-MM-dd} 任务";
        Assert.Equal(DueUrgency.Overdue, task.Urgency);

        task.State = TaskState.Done;   // 归档任务不分档（截止日无行动价值）
        Assert.Equal(DueUrgency.None, task.Urgency);
    }

    [Fact]
    public void ContentChanged_FiresOnContentMutation_NotOnViewState()
    {
        var task = NewTask();
        var fired = 0;
        task.ContentChanged += (_, _) => fired++;

        task.IsExpanded = true;   // 纯视图状态：不触发内容变化
        Assert.Equal(0, fired);

        task.HeaderText = "编辑主行";   // 任务变化 → 文档脏追踪链路
        Assert.Equal(1, fired);
    }
}
