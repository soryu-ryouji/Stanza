namespace Stanza.App.Tests;

/// <summary>截止日输入缩写解析：纯逻辑（无 WPF/环境依赖，不需要 STA 宿主）。</summary>
public class DueDateInputTests
{
    [Fact]
    public void FullDate_ParsedAsIs_NoRolling()
    {
        Assert.True(DueDateInput.TryParse("2026-08-18", out var date));
        Assert.Equal(new DateOnly(2026, 8, 18), date);

        Assert.True(DueDateInput.TryParse("2026/8/8", out var slash));
        Assert.Equal(new DateOnly(2026, 8, 8), slash);
    }

    [Fact]
    public void Shorthand_AllForms_CompleteCurrentYear()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var future = today.AddDays(5);   // 用未来日期保证不触发滚动

        foreach (var form in new[]
        {
            $"{future.Month}-{future.Day}",
            $"{future.Month} {future.Day}",
            $"{future.Month:D2}-{future.Day:D2}",
            $"{future.Month:D2} {future.Day:D2}",
            $"{future.Month:D2}{future.Day:D2}",
        })
        {
            Assert.True(DueDateInput.TryParse(form, out var date), $"应识别: {form}");
            Assert.Equal(future, date);
        }
    }

    [Fact]
    public void Shorthand_InPast_RollsToNextYear()
    {
        var today = DateOnly.FromDateTime(DateTime.Today);
        var past = today.AddDays(-5);

        Assert.True(DueDateInput.TryParse($"{past.Month}-{past.Day}", out var date));
        Assert.Equal(past.AddYears(1), date);   // 已过：滚到明年同月日
    }

    [Fact]
    public void Invalid_Rejected()
    {
        Assert.False(DueDateInput.TryParse("13-1", out _));    // 月超界
        Assert.False(DueDateInput.TryParse("2-30", out _));    // 日不存在
        Assert.False(DueDateInput.TryParse("080931", out _));  // 超过 4 位
        Assert.False(DueDateInput.TryParse("809", out _));     // 3 位歧义不支持
        Assert.False(DueDateInput.TryParse("", out _));
        Assert.False(DueDateInput.TryParse("abc", out _));
    }
}
