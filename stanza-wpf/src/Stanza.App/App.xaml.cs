using System.IO;
using System.Windows;

namespace Stanza.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var window = new MainWindow();
        window.Show();

        // 支持命令行传入文件路径（双击关联、发送到等场景）；否则恢复上次打开的文件
        var file = e.Args.FirstOrDefault(File.Exists);
        if (file != null) window.OpenFile(file);
        else window.OpenStartupFile();
    }
}
