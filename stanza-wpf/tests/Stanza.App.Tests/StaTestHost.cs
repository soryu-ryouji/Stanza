using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;

namespace Stanza.App.Tests;

/// <summary>
/// 串行化集合：共享 %APPDATA% 隔离目录的测试类不得并行（配置读写存在竞争）。
/// </summary>
[CollectionDefinition("AppData", DisableParallelization = true)]
public class AppDataCollection;

/// <summary>
/// WPF 测试宿主：测试体在 STA 线程执行（WPF 对象要求 STA），
/// Application 每个 AppDomain 只创建一次（Loc 等静态代码依赖 Application.Current）。
/// DispatcherTimer 等在测试线程创建后不泵消息，自动保存不会干扰测试。
/// </summary>
public static class StaTestHost
{
    /// <summary>在 STA 线程执行测试体（每个测试调用一次，线程按需新建）。</summary>
    public static T Run<T>(Func<T> action)
    {
        T? result = default;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = action(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error != null) throw new InvalidOperationException("STA 线程执行失败", error);
        return result!;
    }

    public static void Run(Action action) => Run<object?>(() => { action(); return null; });

    /// <summary>测试基类：屏蔽 STA 细节。</summary>
    public abstract class StaFactBase
    {
        protected static T OnUi<T>(Func<T> f) => Run(f);
        protected static void OnUi(Action a) => Run(a);
    }
}

/// <summary>程序集加载时：隔离 %APPDATA%（配置存储路径重定向到临时目录，不读写真实用户配置），
/// 并创建 Application（仅一次）。</summary>
internal static class AppDomainInit
{
    [ModuleInitializer]
    internal static void Init()
    {
        var dir = Path.Combine(Path.GetTempPath(), "stanza-test-appdata");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        Environment.SetEnvironmentVariable("APPDATA", dir);
        StaTestHost.Run(() => _ = new Application());
    }
}
