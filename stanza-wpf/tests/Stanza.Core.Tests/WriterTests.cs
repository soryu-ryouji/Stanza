using Stanza.Core;

namespace Stanza.Core.Tests;

public class WriterTests
{
    // RFC §8 完整示例
    private const string RfcExample = """
        # DOING

        (A) 2026-08-07 完成登录模块的单元测试 +Apollo #紧急
            先跑通现有测试用例
            再补充边界情况

            测试数据在共享盘的 testdata 目录

        (B) 2026-08-07 预约周五下午的牙医 +生活
            记得带医保卡

        整理《重构》读书笔记 +学习
            重点看第 3、6 章
            摘抄代码坏味道清单

        # WAIT

        2026-08-05 等设计组回复新版图标 +Apollo
            上周已提需求，预计本周五交付

        # DONE

        2026-08-05 修复列表页滚动卡顿的问题 +Apollo
            2026-08-06 已合入主干，随 2.3 版本发布

        2026-08-03 更新部署文档 +运维
            已同步到团队 Wiki

        # DELETE

        调研第三方推送服务 +Apollo
            报价超出预算，改用自建方案
        """;

    [Fact]
    public void RoundTrip_RfcExample_ModelIsStable()
    {
        var doc1 = StanzaParser.Parse(RfcExample);
        var text1 = StanzaWriter.Write(doc1);
        var doc2 = StanzaParser.Parse(text1);
        var text2 = StanzaWriter.Write(doc2);

        // 第二次写出与第一次完全一致，说明模型往返稳定
        Assert.Equal(text1, text2);
    }

    [Fact]
    public void Write_UsesLfOnlyAndUppercaseHeaders()
    {
        var doc = StanzaParser.Parse("# doing\n\ntask\n");
        var text = StanzaWriter.Write(doc);
        Assert.DoesNotContain("\r", text);
        Assert.StartsWith("# DOING\n", text);
        Assert.EndsWith("\n", text);
    }

    [Fact]
    public void Write_PreservesNoteIndentation()
    {
        var doc = StanzaParser.Parse("# DOING\n\ntask\n        deep indent\n");
        var text = StanzaWriter.Write(doc);
        Assert.Contains("\n        deep indent\n", text);
    }

    [Fact]
    public void Write_ProjectAfterDescription_RoundTrips()
    {
        var doc = StanzaParser.Parse("# DOING\n\ntask body +Proj #tag1 #tag2\n");
        var text = StanzaWriter.Write(doc);
        Assert.Contains("task body +Proj #tag1 #tag2", text);
    }

    [Fact]
    public void Write_ExtraPlusNameInDescription_ProjectGoesFirst()
    {
        // 描述中残留 +名称 时，项目必须放到描述之前，否则重解析会误取第一个
        var doc = StanzaParser.Parse("# DOING\n\ntask +One +Two\n");
        var text = StanzaWriter.Write(doc);
        Assert.Contains("+One task +Two", text);

        var doc2 = StanzaParser.Parse(text);
        Assert.Equal("One", doc2.FindBlock(TaskState.Doing)!.Tasks[0].Project);
        Assert.Equal("task +Two", doc2.FindBlock(TaskState.Doing)!.Tasks[0].Description);
    }

    [Fact]
    public void TaskHeader_ComposeAndParse_RoundTrips()
    {
        // 编辑器主行内联编辑依赖的组合/解析互逆
        var header = "(B) 2026-08-07 完成登录模块 +Apollo #紧急";
        var parsed = StanzaParser.ParseTaskHeader(header);
        Assert.Equal(header, StanzaWriter.ComposeTaskHeader(parsed));
    }

    [Fact]
    public void Write_BlocksInCanonicalOrder()
    {
        var doc = StanzaParser.Parse("# DELETE\n\ndel task\n\n# DOING\n\ndoing task\n");
        var text = StanzaWriter.Write(doc);
        Assert.True(text.IndexOf("# DOING", StringComparison.Ordinal)
                  < text.IndexOf("# DELETE", StringComparison.Ordinal));
    }

    [Fact]
    public void Write_EmptyMainLineTask_IsSkipped()
    {
        var doc = new StanzaDocument();
        var block = doc.GetOrAddBlock(TaskState.Doing);
        block.Tasks.Add(new StanzaTask());   // 完全空的任务
        block.Tasks.Add(new StanzaTask { Description = "real" });
        var text = StanzaWriter.Write(doc);
        var task = Assert.Single(StanzaParser.Parse(text).FindBlock(TaskState.Doing)!.Tasks);
        Assert.Equal("real", task.Description);
    }

    [Fact]
    public void Write_PreservesBlankLinesInsideNotes()
    {
        var doc = StanzaParser.Parse("# DOING\n\ntask\n    note a\n\n    note b\n");
        var text = StanzaWriter.Write(doc);
        var task = StanzaParser.Parse(text).FindBlock(TaskState.Doing)!.Tasks[0];
        Assert.Equal(new[] { "    note a", "", "    note b" }, task.Notes);
    }
}
