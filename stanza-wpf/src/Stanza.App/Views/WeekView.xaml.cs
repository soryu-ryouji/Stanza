using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using Stanza.App.Services;

namespace Stanza.App.Views;

/// <summary>
/// 自绘周历控件（参考 Things 3 的粗略视图）：不显示年月，只列周列头；
/// 从今天所在周起列出四星期（28 格），末格内嵌 › 翻页、翻页后首格为 ‹ 回翻。
/// 翻页步长 3 周（21 天）：既保持窗口起点对齐周首，被翻页格替代的日期也必然在相邻页出现。
/// 外部经 <see cref="SelectedDate"/>（初始化定位/选中）与 <see cref="DatePicked"/>（点选即应用）通信。
/// 周首日与周列头名称随系统文化。
/// </summary>
public partial class WeekView : UserControl
{
    /// <summary>日历格：日期 + 显示文本（数字 / 月界「9月1」/ 翻页图标）+
    /// 是否过去（禁用）+ 是否今天 + 是否选中 + 翻页方向（0 非翻页格，+1 下一页，-1 上一页）。</summary>
    public sealed record DateCell(DateOnly Date, string Display, bool IsPast, bool IsToday, bool IsSelected, int Pager);

    private const int GridDays = 28;    // 4 行 × 7 列
    private const int PageStep = 21;    // 翻页步长 3 周：7 的倍数（周首对齐）且被替代日期在邻页可见

    public static readonly DependencyProperty SelectedDateProperty =
        DependencyProperty.Register(
            nameof(SelectedDate), typeof(DateOnly?), typeof(WeekView),
            new PropertyMetadata(null, OnSelectedDateChanged));

    /// <summary>当前选中日期（打开时由外部初始化定位；点选经 DatePicked 上报，不在此回写）。</summary>
    public DateOnly? SelectedDate
    {
        get => (DateOnly?)GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
    }

    /// <summary>点选日期格：应用语义由订阅方决定（设值并关闭）。翻页格不触发。</summary>
    public event Action<DateOnly>? DatePicked;

    public ObservableCollection<DateCell> Days { get; } = new();

    public ObservableCollection<string> WeekdayNames { get; } = new();

    /// <summary>窗口起点（始终对齐周首日）。首页 = 今天所在周的首日。</summary>
    private DateOnly _windowStart;

    public WeekView()
    {
        InitializeComponent();
        RefreshWeekdayNames();
        Loc.Changed += (_, _) => RefreshWeekdayNames();   // 语言切换即时刷新
        _windowStart = WeekStart(Today());
        RebuildDays();
    }

    /// <summary>周列头跟随界面语言（Loc），不随系统文化——中文界面在英文系统上也应显示中文。
    /// 周日为一周之首（与 Things 3 参考图一致，两种语言相同）。</summary>
    private void RefreshWeekdayNames()
    {
        WeekdayNames.Clear();
        if (Loc.Current == "zh")
        {
            foreach (var n in new[] { "日", "一", "二", "三", "四", "五", "六" }) WeekdayNames.Add(n);
            return;
        }
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedDayNames;
        foreach (var i in Enumerable.Range(0, 7)) WeekdayNames.Add(names[i]);   // Sunday 起
    }

    private static DateOnly Today() => DateOnly.FromDateTime(DateTime.Today);

    /// <summary>日期所在周的首日（周日）。</summary>
    private static DateOnly WeekStart(DateOnly date) => date.AddDays(-(int)date.DayOfWeek);

    private static void OnSelectedDateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var view = (WeekView)d;
        // 选中日落在当前窗口外时，定位到包含它的窗口（起点对齐其周首日）
        if (e.NewValue is DateOnly date
            && (date < view._windowStart || date >= view._windowStart.AddDays(GridDays)))
            view._windowStart = WeekStart(date);
        view.RebuildDays();
    }

    /// <summary>向后翻一页（3 周）。</summary>
    public void NextPage()
    {
        _windowStart = _windowStart.AddDays(PageStep);
        RebuildDays();
    }

    /// <summary>向前回翻一页；不早于今天所在周。</summary>
    public void PrevPage()
    {
        _windowStart = _windowStart.AddDays(-PageStep);
        var home = WeekStart(Today());
        if (_windowStart < home) _windowStart = home;
        RebuildDays();
    }

    private void Day_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not DateCell cell) return;
        if (cell.Pager > 0) { NextPage(); return; }
        if (cell.Pager < 0) { PrevPage(); return; }
        DatePicked?.Invoke(cell.Date);
    }

    /// <summary>重建 28 格：窗口起点起连续四星期；末格为 ›（翻页），非首页首格为 ‹（回翻）。
    /// 被翻页格替代的日期必然在相邻页出现（步长 21：› 格的日期在下一页第 7 格，‹ 格的日期在上一页第 22 格）。</summary>
    private void RebuildDays()
    {
        var today = Today();
        var isFirstPage = _windowStart <= WeekStart(today);
        Days.Clear();
        for (var i = 0; i < GridDays; i++)
        {
            var date = _windowStart.AddDays(i);
            var pager = i == GridDays - 1 ? 1 : (i == 0 && !isFirstPage ? -1 : 0);
            Days.Add(new DateCell(
                date,
                DisplayOf(date, pager),
                IsPast: pager == 0 && date < today,
                IsToday: pager == 0 && date == today,
                IsSelected: pager == 0 && date == SelectedDate,
                Pager: pager));
        }
    }

    /// <summary>格文本：翻页格为图标；其余为日数字（不显示年月，月初也不加月界标记）。</summary>
    private static string DisplayOf(DateOnly date, int pager)
    {
        if (pager > 0) return "\uE76C";   // ›
        if (pager < 0) return "\uE76B";   // ‹
        return date.Day.ToString();
    }
}
