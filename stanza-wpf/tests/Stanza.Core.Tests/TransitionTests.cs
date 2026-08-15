using Stanza.Core;

namespace Stanza.Core.Tests;

/// <summary>RFC §9 任务流转与活跃排序规则（不变量 #4 / #5）。</summary>
public class TransitionTests
{
    private static readonly DateOnly Today = new(2026, 2, 10);

    // ---- 规范化（§9） ----

    [Fact]
    public void NormalizeForState_EnteringDone_RemovesPriorityAndAppendsCompletionDate()
    {
        var task = new StanzaTask { Priority = 'A', DueDate = new DateOnly(2026, 3, 1) };
        task.Notes.Add("    已有备注");

        TaskTransitions.NormalizeForState(task, TaskState.Doing, TaskState.Done, Today);

        Assert.Null(task.Priority);
        Assert.Equal(new DateOnly(2026, 3, 1), task.DueDate);   // 截止日期保留
        // 完成日期追加在备注末尾，规范续行形式（4 空格缩进）
        Assert.Equal(new[] { "    已有备注", "    2026-02-10 完成" }, task.Notes);
    }

    [Fact]
    public void NormalizeForState_EnteringDelete_RemovesPriorityWithoutCompletionDate()
    {
        var task = new StanzaTask { Priority = 'B' };

        TaskTransitions.NormalizeForState(task, TaskState.Doing, TaskState.Delete, Today);

        Assert.Null(task.Priority);
        Assert.Empty(task.Notes);
    }

    [Theory]
    [InlineData(TaskState.Doing)]
    [InlineData(TaskState.Wait)]
    public void NormalizeForState_EnteringActiveState_KeepsMetadata(TaskState target)
    {
        var task = new StanzaTask { Priority = 'C', DueDate = new DateOnly(2026, 3, 1) };
        task.Notes.Add("    2026-02-01 完成");

        TaskTransitions.NormalizeForState(task, TaskState.Done, target, Today);

        Assert.Equal('C', task.Priority);
        Assert.Equal(new DateOnly(2026, 3, 1), task.DueDate);
        Assert.Equal(new[] { "    2026-02-01 完成" }, task.Notes);   // 完成记录保留
    }

    [Fact]
    public void NormalizeForState_SameState_DoesNothing()
    {
        var task = new StanzaTask();
        task.Notes.Add("    2026-02-01 完成");

        TaskTransitions.NormalizeForState(task, TaskState.Done, TaskState.Done, Today);

        Assert.Single(task.Notes);   // DONE 内移动不重复追加完成日期
    }

    [Fact]
    public void CompletionLine_FormatsCanonicalDate()
        => Assert.Equal("2026-02-10 完成", TaskTransitions.CompletionLine(Today));

    // ---- 插入位置（§9） ----

    [Theory]
    [InlineData(TaskState.Done)]
    [InlineData(TaskState.Delete)]
    public void InsertsAtTop_ArchiveStates_True(TaskState state)
        => Assert.True(TaskTransitions.InsertsAtTop(state));

    [Theory]
    [InlineData(TaskState.Doing)]
    [InlineData(TaskState.Wait)]
    public void InsertsAtTop_ActiveStates_False(TaskState state)
    {
        Assert.False(TaskTransitions.InsertsAtTop(state));
        Assert.True(TaskTransitions.IsActiveState(state));
    }

    [Theory]
    [InlineData(TaskState.Done)]
    [InlineData(TaskState.Delete)]
    public void IsActiveState_ArchiveStates_False(TaskState state)
        => Assert.False(TaskTransitions.IsActiveState(state));

    // ---- 活跃排序（§9 排序约定） ----

    [Fact]
    public void ActiveTaskOrdering_PriorityFirst_NoPriorityLast()
    {
        var tasks = new[]
        {
            new StanzaTask { Description = "无优先级" },
            new StanzaTask { Description = "B", Priority = 'B' },
            new StanzaTask { Description = "A", Priority = 'A' },
        };

        var sorted = tasks.OrderBy(t => t, Comparer<StanzaTask>.Create(ActiveTaskOrdering.Compare)).ToList();

        Assert.Equal(new[] { "A", "B", "无优先级" }, sorted.Select(t => t.Description));
    }

    [Fact]
    public void ActiveTaskOrdering_SamePriority_SortsByDueDate_NoDateLast()
    {
        var tasks = new[]
        {
            new StanzaTask { Description = "无日期", Priority = 'A' },
            new StanzaTask { Description = "晚", Priority = 'A', DueDate = new DateOnly(2026, 3, 1) },
            new StanzaTask { Description = "早", Priority = 'A', DueDate = new DateOnly(2026, 2, 1) },
        };

        var sorted = tasks.OrderBy(t => t, Comparer<StanzaTask>.Create(ActiveTaskOrdering.Compare)).ToList();

        Assert.Equal(new[] { "早", "晚", "无日期" }, sorted.Select(t => t.Description));
    }

    [Fact]
    public void ActiveTaskOrdering_EqualKeys_OrderByStaysStable()
    {
        // 拖拽排序依赖稳定性：同优先级同日期的任务相对顺序不变
        var tasks = new[]
        {
            new StanzaTask { Description = "一", Priority = 'A' },
            new StanzaTask { Description = "二", Priority = 'A' },
            new StanzaTask { Description = "三", Priority = 'A' },
        };

        var sorted = tasks.OrderBy(t => t, Comparer<StanzaTask>.Create(ActiveTaskOrdering.Compare)).ToList();

        Assert.Equal(new[] { "一", "二", "三" }, sorted.Select(t => t.Description));
    }

    // ---- 端到端：规范化后经写出/解析仍成立（不变量 #3 + #4） ----

    [Fact]
    public void NormalizeForState_ViaHeaderRecompose_MatchesEditorRoundTrip()
    {
        // 编辑器路径（MainViewModel.NormalizeForTarget）：主行解析 → 规范化 → 重组为规范顺序
        var m = StanzaParser.ParseTaskHeader("(A) 2026-03-01 写月报 +工作 #急");
        TaskTransitions.NormalizeForState(m, TaskState.Doing, TaskState.Done, Today);

        Assert.Equal("2026-03-01 写月报 +工作 #急", StanzaWriter.ComposeTaskHeader(m));
        Assert.Equal("    2026-02-10 完成", Assert.Single(m.Notes));
    }

    [Fact]
    public void NormalizeForState_RoundTrip_CompletionSurvivesWriteAndParse()
    {
        var doc = StanzaParser.Parse("# DOING\n\n(A) 2026-03-01 写月报 +工作\n");
        var doing = doc.FindBlock(TaskState.Doing)!;
        var task = doing.Tasks[0];

        // 完成：移到 DONE 顶部并规范化（§9）
        doing.Tasks.Remove(task);
        TaskTransitions.NormalizeForState(task, TaskState.Doing, TaskState.Done, Today);
        doc.GetOrAddBlock(TaskState.Done).Tasks.Insert(0, task);

        var reparsed = StanzaParser.Parse(StanzaWriter.Write(doc));

        var done = reparsed.FindBlock(TaskState.Done)!;
        var moved = done.Tasks[0];
        Assert.Null(moved.Priority);
        Assert.Equal("写月报", moved.Description);
        Assert.Equal("    2026-02-10 完成", moved.Notes[^1]);
        Assert.Empty(reparsed.FindBlock(TaskState.Doing)!.Tasks);
    }
}
