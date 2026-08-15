using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Stanza.App.ViewModels;

namespace Stanza.App;

public partial class MainWindow : Window
{
    private MainViewModel VM => (MainViewModel)DataContext;

    public MainWindow()
    {
        InitializeComponent();

        var vm = new MainViewModel
        {
            PickOpenFile = PickOpenFile,
            PickSaveFile = PickSaveFile,
        };
        vm.TaskCreated += OnTaskCreated;
        DataContext = vm;

        // 区块切换后把焦点交给任务列表中的条目容器：防止焦点悬在 ListBox 上显示主题虚线框，
        // 同时保持方向键导航可用
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.SelectedBlock)) return;
            DisarmClear();   // 切换区块时取消清空的待确认状态
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
            {
                var target = VM.SelectedTask;
                if (target == null || VM.SelectedBlock?.Items.Contains(target) != true)
                    target = VM.SelectedBlock?.Tasks.FirstOrDefault();
                var container = target == null
                    ? null
                    : TaskList.ItemContainerGenerator.ContainerFromItem(target) as UIElement;
                (container ?? (UIElement)TaskList).Focus();
            }));
        };

        InitializeRecentPopup();
        InitializeDragInput();
    }

    /// <summary>供命令行参数 / 拖放调用。</summary>
    public void OpenFile(string path) => VM.OpenFile(path);

    /// <summary>启动时恢复上次打开的文件。</summary>
    public void OpenStartupFile() => VM.OpenStartupFile();

    // ==================== 窗口 ====================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        // 无系统标题栏，自行实现拖动；最大化时先还原并把标题栏对齐到鼠标位置，保持拖动连贯
        if (WindowState == WindowState.Maximized)
        {
            var ratio = e.GetPosition(this).X / ActualWidth;
            var screen = PointToScreen(e.GetPosition(this));
            WindowState = WindowState.Normal;
            Left = screen.X - Width * ratio;
            Top = screen.Y - 19;   // 拖拽区高 38，让鼠标落在其中部
        }

        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Min_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Max_Click(object sender, RoutedEventArgs e) => ToggleMaximize();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!VM.IsDirty) return;
        var r = MessageBox.Show(this, "有未保存的更改。", "Stanza",
            MessageBoxButton.YesNoCancel, MessageBoxImage.None);
        if (r == MessageBoxResult.Yes)
        {
            VM.Save();
            if (VM.IsDirty) e.Cancel = true;   // 保存被取消（如未选择路径），不关闭
        }
        else if (r == MessageBoxResult.Cancel)
        {
            e.Cancel = true;
        }
    }

    // ==================== 文件对话框 ====================

    private string? PickOpenFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开 Stanza 文件",
            Filter = "Stanza 文件 (*.stanza;*.txt)|*.stanza;*.txt|所有文件 (*.*)|*.*",
        };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }

    private string? PickSaveFile()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "保存 Stanza 文件",
            Filter = "Stanza 文件 (*.stanza)|*.stanza|文本文件 (*.txt)|*.txt",
            FileName = "TODO.stanza",
            DefaultExt = ".stanza",
        };
        return dlg.ShowDialog(this) == true ? dlg.FileName : null;
    }
}
