using System.Globalization;

namespace Stanza.App;

/// <summary>
/// 截止日输入的宽松解析（GUI 输入辅助，不进 Core——Core 只管格式规则）。
/// 完整日期（含年份）原样解析；月日缩写（8-9 / 8 9 / 08-09 / 08 09 / 0809）补当年，
/// 已过的缩写滚到明年（截止是未来语义，与周历禁用过去日期一致；跨年由此自动正确）。
/// 纯数字只接受 4 位 MMdd——3 位有歧义（123 是 1/23 还是 12/3），不支持。
/// </summary>
internal static class DueDateInput
{
    private static readonly string[] FullFormats = { "yyyy-MM-dd", "yyyy-M-d", "yyyy/MM/dd", "yyyy/M/d" };

    public static bool TryParse(string text, out DateOnly date)
    {
        text = text.Trim();
        // 完整日期（含年份）：原样，不滚动
        if (DateOnly.TryParseExact(text, FullFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            return true;

        // 月日缩写：补当年；已过滚到明年
        if (TryParseShorthand(text, out var month, out var day))
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            if (DateOnly.TryParseExact($"{today.Year}-{month}-{day}", "yyyy-M-d",
                    CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
            {
                if (date < today) date = date.AddYears(1);
                return true;
            }
        }
        date = default;
        return false;
    }

    /// <summary>拆分月日缩写：分隔形式（M-d / M/d / M d，单位数均可）与纯数字 4 位（MMdd）。
    /// 只检查粗范围（月 1-12、日 1-31）；日的合法性（如 2-30）由调用方的完整构造兜底。</summary>
    private static bool TryParseShorthand(string text, out int month, out int day)
    {
        month = day = 0;
        // 纯数字 4 位：MMdd（0809 → 8 月 9 日）
        if (text.Length == 4 && text.All(char.IsDigit))
        {
            month = int.Parse(text[..2], CultureInfo.InvariantCulture);
            day = int.Parse(text[2..], CultureInfo.InvariantCulture);
            return month is >= 1 and <= 12 && day is >= 1 and <= 31;
        }
        // 分隔形式：M-d / M/d / M d
        var parts = text.Split(new[] { '-', '/', ' ' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out month)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out day))
            return month is >= 1 and <= 12 && day is >= 1 and <= 31;
        month = day = 0;
        return false;
    }
}
