using System.IO;
using System.Windows;
using Stanza.App.Services;

namespace Stanza.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 先应用语言（合并对应字符串字典），再建窗口——XAML 的 DynamicResource 在建窗时求值
        Loc.SetLanguage(SettingsStore.Load().Language);

        var window = new MainWindow();

        // 支持命令行传入文件路径（双击关联、发送到等场景）；否则恢复上次打开的文件。
        // 加载在 Show 之前完成：窗口首帧即为任务界面，避免无文档页短暂闪现
        var file = e.Args.FirstOrDefault(File.Exists);
        if (file != null) window.OpenFile(file);
        else window.OpenStartupFile();

        window.Show();
    }
}
