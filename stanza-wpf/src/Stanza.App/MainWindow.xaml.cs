using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
            if (e.PropertyName == nameof(MainViewModel.SelectedFacet))
            {
                DisarmClear();   // 进入/离开面板时取消清空的待确认状态
                SyncFacetSelection();
                return;
            }
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

    // ==================== 退出确认（应用内遮罩层） ====================

    private bool _allowClose;

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_allowClose || !VM.IsDirty) return;
        e.Cancel = true;   // 先拦下关闭，由遮罩层的三个选择决定后续
        ShowExitOverlay();
    }

    private void ShowExitOverlay()
    {
        ExitOverlay.Visibility = Visibility.Visible;
        ExitOverlay.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var scale = new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        ExitCardScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        ExitCardScale.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.96, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => ExitSaveButton.Focus()));
    }

    private void HideExitOverlay()
    {
        ExitOverlay.BeginAnimation(UIElement.OpacityProperty, null);
        ExitOverlay.Visibility = Visibility.Collapsed;
        // 与点击空白一致：不主动归还焦点，避免 ListBox 聚焦首项导致视图跳动
        Keyboard.ClearFocus();
    }

    private void ExitSave_Click(object sender, RoutedEventArgs e)
    {
        VM.Save();
        if (!VM.IsDirty)
        {
            _allowClose = true;
            Close();
        }
        else
        {
            // 保存流程未完成（如新建文档在路径选择框点了取消）：回到应用
            HideExitOverlay();
        }
    }

    private void ExitDiscard_Click(object sender, RoutedEventArgs e)
    {
        _allowClose = true;
        Close();
    }

    private void ExitCancel_Click(object sender, RoutedEventArgs e) => HideExitOverlay();

    private void ExitOverlay_DimMouseDown(object sender, MouseButtonEventArgs e) => HideExitOverlay();

    private void ExitOverlay_KeyDown(object sender, KeyEventArgs e)
    {
        // Enter 默认保存（焦点在按钮上时交给按钮自身，避免重复触发）；Esc 取消
        if (e.Key == Key.Enter && Keyboard.FocusedElement is not ButtonBase)
        {
            e.Handled = true;
            ExitSave_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideExitOverlay();
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
