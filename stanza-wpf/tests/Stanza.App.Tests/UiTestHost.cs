using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Stanza.App.ViewModels;

namespace Stanza.App.Tests;

/// <summary>
/// 进程内 UI 测试宿主：在 STA 线程创建真实 MainWindow（含完整视觉树与绑定），
/// 消息泵推进布局/容器生成/动画，合成键盘输入走完整分发管线（InputManager.PreProcessInput）。
/// 与真实点击的差异：不做 OS 级 hit test 与修饰键注入；窗口真实 Show（需要桌面会话，headless CI 不适用）。
/// 业务规则测试仍归 VM 层（MainViewModelTests 等），这里只覆盖「接线」：绑定、命令分发、模板转发、焦点。
/// </summary>
public static class UiTestHost
{
    private static bool _resourcesReady;

    /// <summary>补齐 App.xaml/OnStartup 在测试进程不执行的部分：合并主题与语言字典
    /// （XAML 的 StaticResource 画刷/样式依赖应用资源），注册文本编辑键的类级处理器。
    /// 代码里的相对 pack URI 以入口程序集（测试宿主）为基，必须用 ;component 绝对形式。幂等。</summary>
    public static void EnsureResources()
    {
        if (_resourcesReady) return;
        _resourcesReady = true;
        var merged = (Application.Current ?? new Application()).Resources.MergedDictionaries;
        merged.Add(new ResourceDictionary { Source = PackUri("Themes/Minimal.xaml") });
        merged.Add(new ResourceDictionary { Source = PackUri("Themes/Strings.zh.xaml") });
        // 与 App.OnStartup 一致：TextEditKeys 挂到所有 TextBox 的类级 PreviewKeyDown
        EventManager.RegisterClassHandler(typeof(TextBox), UIElement.PreviewKeyDownEvent,
            new KeyEventHandler((sender, args) => TextEditKeys.Handle((TextBox)sender, args)));
    }

    // 程序集名是 Stanza（csproj AssemblyName），命名空间 Stanza.App 仅同名巧合；pack URI 用程序集名
    private static Uri PackUri(string path)
        => new($"pack://application:,,,/Stanza;component/{path}", UriKind.Absolute);

    /// <summary>创建窗口（可选先打开文档），Show 后泵到 Loaded：绑定求值与容器生成完成。</summary>
    public static MainWindow CreateWindow(string? file = null)
    {
        EnsureResources();
        var window = new MainWindow();
        if (file != null) window.OpenFile(file);
        window.Show();
        PumpPriority(DispatcherPriority.Loaded);
        Pump(50);   // 容器生成与布局的余量
        return window;
    }

    /// <summary>把调度队列泵到指定优先级清空。</summary>
    public static void PumpPriority(DispatcherPriority priority = DispatcherPriority.Background)
    {
        var frame = new DispatcherFrame();
        Dispatcher.CurrentDispatcher.BeginInvoke(priority, new Action(() => frame.Continue = false));
        Dispatcher.PushFrame(frame);
    }

    /// <summary>泵消息指定时长（推进动画、DispatcherTimer 等时间驱动逻辑）。</summary>
    public static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(milliseconds),
        };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start();
        Dispatcher.PushFrame(frame);
    }

    /// <summary>轮询泵消息直到条件满足；超时抛带说明的异常（便于诊断是哪一步没走完）。</summary>
    public static void PumpUntil(Func<bool> condition, string description, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            if (sw.ElapsedMilliseconds > timeoutMs)
                throw new TimeoutException($"等待超时（{timeoutMs}ms）：{description}");
            Pump(50);
        }
    }

    /// <summary>合成键盘输入：走完整输入管线（InputManager.PreProcessInput → 应用/任务命令分发 → 路由）。
    /// 修饰键状态取自真实键盘设备（测试线程无法注入 Ctrl），故仅适用无修饰键的手势（Space 等）。</summary>
    public static void SendKey(MainWindow window, Key key)
    {
        var source = PresentationSource.FromVisual(window)
            ?? throw new InvalidOperationException("窗口未 Show，无 PresentationSource");
        InputManager.Current.ProcessInput(new KeyEventArgs(Keyboard.PrimaryDevice, source, 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        });
    }

    /// <summary>任务列表中指定任务的容器（列表非虚拟化，Loaded 后全量生成）。</summary>
    public static ListBoxItem? ContainerOf(MainWindow window, TaskViewModel task)
        => window.TaskList.ItemContainerGenerator.ContainerFromItem(task) as ListBoxItem;

    /// <summary>关闭窗口：替换为干净 VM 绕过退出确认遮罩（接线测试不验证关闭流程本身）。</summary>
    public static void CloseWindow(MainWindow window)
    {
        window.DataContext = new MainViewModel();
        window.Close();
        PumpPriority();
    }
}
