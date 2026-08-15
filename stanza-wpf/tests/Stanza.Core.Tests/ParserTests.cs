using Stanza.Core;

namespace Stanza.Core.Tests;

/// <summary>RFC §10.3 边界情况与测试用例。</summary>
public class ParserTests
{
    [Fact]
    public void Case1_BlankLineBeforeIndentedLine_BelongsToNotes()
    {
        var doc = StanzaParser.Parse("# DOING\n\ntask one\n\n    note line\n");
        var task = Assert.Single(doc.FindBlock(TaskState.Doing)!.Tasks);
        // 空白行计入备注（备注内空行），续行原样保留缩进
        Assert.Equal(new[] { "", "    note line" }, task.Notes);
    }

    [Fact]
    public void Case2_BlankLineBeforeUnindentedLine_IsOnlySeparator()
    {
        var doc = StanzaParser.Parse("# DOING\n\ntask one\n\ntask two\n");
        var block = doc.FindBlock(TaskState.Doing)!;
        Assert.Equal(2, block.Tasks.Count);
        Assert.Equal("task one", block.Tasks[0].Description);
        Assert.Empty(block.Tasks[0].Notes);
        Assert.Equal("task two", block.Tasks[1].Description);
    }

    [Fact]
    public void Case3_AdjacentUnindentedLines_AreTwoTasks()
    {
        var doc = StanzaParser.Parse("# DOING\n\ntask one\ntask two\n");
        Assert.Equal(2, doc.FindBlock(TaskState.Doing)!.Tasks.Count);
    }

    [Fact]
    public void Case4_CPlusPlus_IsNotProject()
    {
        var doc = StanzaParser.Parse("# DOING\n\n修复 C++ 编译错误 +Dev\n");
        var task = Assert.Single(doc.FindBlock(TaskState.Doing)!.Tasks);
        Assert.Equal("Dev", task.Project);
        Assert.Equal("修复 C++ 编译错误", task.Description);
    }

    [Fact]
    public void Case5_HashFollowedByDigit_IsNotTag()
    {
        var doc = StanzaParser.Parse("# DOING\n\n联系客服走 #1 号窗口\n");
        var task = Assert.Single(doc.FindBlock(TaskState.Doing)!.Tasks);
        Assert.Empty(task.Tags);
        Assert.Equal("联系客服走 #1 号窗口", task.Description);
    }

    [Fact]
    public void Case6_TagAtLineStart_IsValid()
    {
        var doc = StanzaParser.Parse("# DOING\n\n#v2 版本回归测试\n");
        var task = Assert.Single(doc.FindBlock(TaskState.Doing)!.Tasks);
        Assert.Equal(new[] { "v2" }, task.Tags);
        Assert.Equal("版本回归测试", task.Description);
    }

    [Fact]
    public void Case7_OrphanContinuationLines_AreIgnoredWithWarning()
    {
        var doc = StanzaParser.Parse("    orphan at file start\n# DOING\n\n    orphan after header\ntask\n");
        var block = doc.FindBlock(TaskState.Doing)!;
        var task = Assert.Single(block.Tasks);
        Assert.Equal("task", task.Description);
        Assert.Empty(task.Notes);
        Assert.Equal(2, doc.Warnings.Count);
    }

    [Fact]
    public void Case8_TaskBeforeFirstBlock_IsIgnoredWithWarning()
    {
        var doc = StanzaParser.Parse("stray task\n# DOING\n\nreal task\n");
        var task = Assert.Single(doc.FindBlock(TaskState.Doing)!.Tasks);
        Assert.Equal("real task", task.Description);
        Assert.Single(doc.Warnings);
    }

    [Theory]
    [InlineData("# doing")]
    [InlineData("# Doing")]
    [InlineData("# dOiNg")]
    public void Case9_BlockTitle_IsCaseInsensitive(string title)
    {
        var doc = StanzaParser.Parse(title + "\n\ntask\n");
        Assert.NotNull(doc.FindBlock(TaskState.Doing));
    }

    [Theory]
    [InlineData("# DONIG")]
    [InlineData("# DOING 杂记")]
    public void Case10_MalformedBlockTitle_IsOrdinaryTask(string line)
    {
        var doc = StanzaParser.Parse("# WAIT\n\n" + line + "\n");
        Assert.Null(doc.FindBlock(TaskState.Doing));
        var task = Assert.Single(doc.FindBlock(TaskState.Wait)!.Tasks);
        Assert.Equal(line, task.Description);
    }

    [Fact]
    public void Case10_HashWithoutSpace_IsTaskWithTag()
    {
        // "#DOING" 不是区块标题；作为任务行，行首的 #DOING 是合法标签（首字符为字母）
        var doc = StanzaParser.Parse("# WAIT\n\n#DOING\n");
        Assert.Null(doc.FindBlock(TaskState.Doing));
        var task = Assert.Single(doc.FindBlock(TaskState.Wait)!.Tasks);
        Assert.Equal(new[] { "DOING" }, task.Tags);
        Assert.Equal("", task.Description);
    }

    [Fact]
    public void Case11_PriorityInDoneBlock_IsParsedSyntactically()
    {
        var doc = StanzaParser.Parse("# DONE\n\n(A) 2026-08-07 finished task\n");
        var task = Assert.Single(doc.FindBlock(TaskState.Done)!.Tasks);
        // 语法上解析出优先级（展示与排序由应用层忽略）
        Assert.Equal('A', task.Priority);
        Assert.Equal(new DateOnly(2026, 8, 7), task.DueDate);
        Assert.Equal("finished task", task.Description);
    }

    [Fact]
    public void Case12_DuplicateBlocks_AreMerged()
    {
        var doc = StanzaParser.Parse("# DOING\n\ntask a\n\n# WAIT\n\ntask w\n\n# DOING\n\ntask b\n");
        var block = doc.FindBlock(TaskState.Doing)!;
        Assert.Equal(2, block.Tasks.Count);
        Assert.Equal("task a", block.Tasks[0].Description);
        Assert.Equal("task b", block.Tasks[1].Description);
    }

    [Fact]
    public void Case13_CrlfAndBom_ParseNormally()
    {
        var doc = StanzaParser.Parse("﻿# DOING\r\n\r\ntask one\r\n    note\r\n");
        var task = Assert.Single(doc.FindBlock(TaskState.Doing)!.Tasks);
        Assert.Equal("task one", task.Description);
        Assert.Equal(new[] { "    note" }, task.Notes);
    }

    [Fact]
    public void Case15_EmptyBlock_IsPreserved()
    {
        var doc = StanzaParser.Parse("# DOING\n\ntask\n\n# DELETE\n");
        var delete = doc.FindBlock(TaskState.Delete);
        Assert.NotNull(delete);
        Assert.Empty(delete.Tasks);
    }

    [Fact]
    public void PriorityAndDate_AreParsedInOrder()
    {
        var doc = StanzaParser.Parse("# DOING\n\n(B) 2026-08-07 完成登录模块 +Apollo #紧急 #review\n");
        var task = Assert.Single(doc.FindBlock(TaskState.Doing)!.Tasks);
        Assert.Equal('B', task.Priority);
        Assert.Equal(new DateOnly(2026, 8, 7), task.DueDate);
        Assert.Equal("完成登录模块", task.Description);
        Assert.Equal("Apollo", task.Project);
        Assert.Equal(new[] { "紧急", "review" }, task.Tags);
    }

    [Fact]
    public void InvalidDate_DoesNotOccupyDateSlot()
    {
        var doc = StanzaParser.Parse("# DOING\n\n2026-13-99 not a real date\n");
        var task = Assert.Single(doc.FindBlock(TaskState.Doing)!.Tasks);
        Assert.Null(task.DueDate);
        Assert.Equal("2026-13-99 not a real date", task.Description);
    }

    [Fact]
    public void PriorityWithoutTrailingSpace_IsNotPriority()
    {
        var doc = StanzaParser.Parse("# DOING\n\n(A)no space here\n");
        var task = Assert.Single(doc.FindBlock(TaskState.Doing)!.Tasks);
        Assert.Null(task.Priority);
        Assert.Equal("(A)no space here", task.Description);
    }

    [Fact]
    public void Case22_Priority_ParsesQuadrant()
    {
        var doc = StanzaParser.Parse("# DOING\n\n(A) 提交报告\n");
        var task = Assert.Single(doc.FindBlock(TaskState.Doing)!.Tasks);
        Assert.Equal('A', task.Priority);
        Assert.Equal("提交报告", task.Description);
    }

    [Fact]
    public void TrySplitPriority_SplitsPrefixFromEditableText()
    {
        // GUI 编辑文本剥除优先级前缀所依赖的拆分（§7.2.1）
        Assert.True(StanzaParser.TrySplitPriority("(A) 2026-08-07 任务", out var p, out var rest));
        Assert.Equal('A', p);
        Assert.Equal("2026-08-07 任务", rest);

        Assert.False(StanzaParser.TrySplitPriority("(E) 任务", out _, out var kept));
        Assert.Equal("(E) 任务", kept);
    }

    [Theory]
    [InlineData("(E) 任务")]       // 象限字母超出 A–D
    [InlineData("(A3) 任务")]     // 附加级别数字（1.5.0 起级别语法移除）
    [InlineData("(A-1) 任务")]    // 附加连字符数字
    public void Case23_InvalidQuadrantPriority_IsNotPriority(string line)
    {
        var doc = StanzaParser.Parse("# DOING\n\n" + line + "\n");
        var task = Assert.Single(doc.FindBlock(TaskState.Doing)!.Tasks);
        Assert.Null(task.Priority);
        Assert.Equal(line, task.Description);
    }

    [Fact]
    public void MultiplePlusNames_OnlyFirstIsProject()
    {
        var doc = StanzaParser.Parse("# DOING\n\ntask +One +Two\n");
        var task = Assert.Single(doc.FindBlock(TaskState.Doing)!.Tasks);
        Assert.Equal("One", task.Project);
        Assert.Equal("task +Two", task.Description);
    }

    // ---- 时间戳行（§7.4 / §10.3 用例 16-20） ----

    [Fact]
    public void TimestampLines_ZhAndEn_AreExtracted()
    {
        var doc = StanzaParser.Parse("# DONE\n\ntask\n    2026-08-04 创建\n    备注\n    2026-08-06 完成\n");
        var task = Assert.Single(doc.FindBlock(TaskState.Done)!.Tasks);
        Assert.Equal(new DateOnly(2026, 8, 4), task.CreatedAt);
        Assert.Equal(new DateOnly(2026, 8, 6), task.CompletedAt);
        // 备注原样保留，保证写出无损（§7.3）
        Assert.Equal(new[] { "    2026-08-04 创建", "    备注", "    2026-08-06 完成" }, task.Notes);
    }

    [Theory]
    [InlineData("    2026-08-04 created")]
    [InlineData("    2026-08-04 Created")]
    [InlineData("    2026-08-04 CREATED")]
    public void Case18_EnglishKeyword_IsCaseInsensitive(string line)
    {
        var doc = StanzaParser.Parse("# DOING\n\ntask\n" + line + "\n");
        var task = Assert.Single(doc.FindBlock(TaskState.Doing)!.Tasks);
        Assert.Equal(new DateOnly(2026, 8, 4), task.CreatedAt);
    }

    [Theory]
    [InlineData("    2026-08-05 完成初稿")]      // 关键字后有其他文字
    [InlineData("    创建于 2026-08-05")]        // 关键字在日期之前
    [InlineData("    2026-08-05 完成 ！")]       // 含其他字符
    [InlineData("    2026-13-01 创建")]          // 非法日期
    public void Case16_TimestampLine_RequiresFullLineMatch(string line)
    {
        var doc = StanzaParser.Parse("# DOING\n\ntask\n" + line + "\n");
        var task = Assert.Single(doc.FindBlock(TaskState.Doing)!.Tasks);
        Assert.Null(task.CreatedAt);
        Assert.Null(task.CompletedAt);
        Assert.Equal(new[] { line }, task.Notes);   // 仍按普通备注保留
    }

    [Fact]
    public void Case17_MultipleCompletions_LastWins()
    {
        var doc = StanzaParser.Parse("# DONE\n\ntask\n    2026-08-05 完成\n    2026-08-07 完成\n");
        var task = Assert.Single(doc.FindBlock(TaskState.Done)!.Tasks);
        Assert.Equal(new DateOnly(2026, 8, 7), task.CompletedAt);
    }

    [Fact]
    public void Case19_MultipleCreations_FirstWins()
    {
        var doc = StanzaParser.Parse("# DOING\n\ntask\n    2026-08-04 创建\n    2026-08-05 创建\n");
        var task = Assert.Single(doc.FindBlock(TaskState.Doing)!.Tasks);
        Assert.Equal(new DateOnly(2026, 8, 4), task.CreatedAt);
    }

    [Fact]
    public void Case20_Timestamps_SurviveWriteAndParse()
    {
        const string source = "# DONE\n\ntask\n    2026-08-04 创建\n    2026-08-06 完成\n";
        var reparsed = StanzaParser.Parse(StanzaWriter.Write(StanzaParser.Parse(source)));
        var task = Assert.Single(reparsed.FindBlock(TaskState.Done)!.Tasks);
        Assert.Equal(new DateOnly(2026, 8, 4), task.CreatedAt);
        Assert.Equal(new DateOnly(2026, 8, 6), task.CompletedAt);
    }

    [Fact]
    public void IsTimestampLine_ToleratesSurroundingWhitespace()
    {
        Assert.True(StanzaParser.IsTimestampLine("2026-08-05 创建"));     // 顶格（编辑器备注文本）
        Assert.True(StanzaParser.IsTimestampLine("  2026-08-05 完成  ")); // 前导/尾随空白
        Assert.False(StanzaParser.IsTimestampLine("2026-08-05"));        // 裸日期不是时间戳行
        Assert.False(StanzaParser.IsTimestampLine("随便一行备注"));
    }
}
