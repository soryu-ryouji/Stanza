using System.Windows;
using Stanza.Core;

namespace Stanza.App.Services;

/// <summary>
/// 运行时本地化。每语言一个字符串资源字典（Themes/Strings.{lang}.xaml），merge 进应用资源；
/// XAML 用 {DynamicResource} 引用，切换语言时替换字典即全量刷新。
/// 代码侧字符串用 Get/Format 读取；需要即时刷新的文本订阅 Changed。
/// </summary>
public static class Loc
{
    public const string DefaultLanguage = "zh";

    private static ResourceDictionary? _active;

    /// <summary>当前语言（"zh" / "en"）。</summary>
    public static string Current { get; private set; } = DefaultLanguage;

    /// <summary>语言字典已替换（启动时的首次合并也会触发）。</summary>
    public static event EventHandler? Changed;

    /// <summary>按 key 取当前语言的字符串；缺失时返回 key 本身（便于发现漏配）。</summary>
    public static string Get(string key)
        => Application.Current.TryFindResource(key) as string ?? key;

    public static string Format(string key, object arg0)
        => string.Format(Get(key), arg0);

    public static string Format(string key, object arg0, object arg1)
        => string.Format(Get(key), arg0, arg1);

    /// <summary>状态区块的显示名（侧栏 / 大标题 / 面板分组头）。英文为 RFC 令牌原样（DOING 等）。</summary>
    public static string StateName(TaskState state) => Get("State_" + TaskStateNames.ToHeader(state));

    /// <summary>切换语言：替换合并资源字典（XAML 即时生效），再通知代码侧刷新。</summary>
    public static void SetLanguage(string language)
    {
        if (_active != null && language == Current) return;
        var dict = new ResourceDictionary
        {
            Source = new Uri($"Themes/Strings.{language}.xaml", UriKind.Relative),
        };
        var merged = Application.Current.Resources.MergedDictionaries;
        if (_active != null) merged.Remove(_active);
        merged.Add(dict);
        _active = dict;
        Current = language;
        Changed?.Invoke(null, EventArgs.Empty);
    }
}
