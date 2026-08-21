using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Stanza.App.ViewModels;

namespace Stanza.App;

/// <summary>
/// 任务完成与撤销的过渡动画。完成：勾选 → 变灰 → 淡出 → 高度收起补位 → 统一提交流转；
/// 撤销：全量重建后按内容键 diff，「回来的任务」播完成动画的倒放。
/// 状态流转与撤销的数据逻辑在 MainViewModel，这里只做视觉编排。
/// </summary>
public partial class MainWindow
{
    // ==================== 勾选完成 ====================

    /// <summary>由 Themes/TaskTemplates 资源字典中的勾选框转发调用。</summary>
    // ==================== 完成动画：勾选 → 变灰 → 淡出 → 收起补位 ====================

    /// <summary>动画进行中（尚未提交）的任务：防止动画期间被重复完成。</summary>
    private readonly HashSet<TaskViewModel> _completingTasks = new();

    internal void HandleTaskCheck(CheckBox checkBox, RoutedEventArgs e)
    {
        if (checkBox.DataContext is not TaskViewModel task) return;
        e.Handled = true;
        // 已完成任务的勾选框呈已勾状态：点击 = 取消完成，直接恢复回 DOING（§9），不播动画
        if (task.IsDone)
        {
            VM.RestoreTask(task);
            return;
        }
        AnimateCompleteTasks(new[] { task });
    }

    /// <summary>以「勾选 → 变灰 → 淡出 → 收起」动画完成一组任务：先补上勾选视觉（点击勾选框的路径
    /// 已由 Click 置位 IsChecked），整卡降至半透明（白底上即灰化），再渐隐，最后高度收起、
    /// 下方任务匀速补位。全部结束后统一提交流转（§9），选中/焦点落位到空缺处。</summary>
    private void AnimateCompleteTasks(IReadOnlyList<TaskViewModel> tasks)
    {
        var pending = tasks.Where(t => _completingTasks.Add(t)).ToList();
        if (pending.Count == 0) return;
        var focusIndex = FirstSelectedIndex();   // 提交后的落位（空缺处）
        var remaining = pending.Count;
        foreach (var task in pending)
        {
            var item = ContainerOf(task);
            if (item == null)
            {
                // 容器不可见（滚动外/刚切换视图）：不参与动画，直接计入提交
                if (--remaining == 0) CommitCompleteTasks(pending, focusIndex);
                continue;
            }

            // 勾选：Space/命令路径未经过勾选框点击，这里补齐视觉；IsEnabled 防止动画期间重复点击
            var box = VisualTreeEx.FindVisualChildren<CheckBox>(item).FirstOrDefault();
            if (box != null) { box.IsChecked = true; box.IsEnabled = false; }

            item.ClipToBounds = true;
            item.MaxHeight = item.ActualHeight;   // 锁定当前高度，淡出结束后由此收起

            // 变灰 → 淡出：先降至 0.45 停顿出「灰化」观感，再渐隐至消失
            item.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
            {
                KeyFrames =
                {
                    new LinearDoubleKeyFrame(1.0, TimeSpan.Zero),
                    new LinearDoubleKeyFrame(0.45, TimeSpan.FromMilliseconds(180)),
                    new LinearDoubleKeyFrame(0.0, TimeSpan.FromMilliseconds(430)),
                },
            });

            var shrink = new DoubleAnimation(item.ActualHeight, 0, TimeSpan.FromMilliseconds(260))
            {
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseIn },
                BeginTime = TimeSpan.FromMilliseconds(430),
            };
            shrink.Completed += (_, _) =>
            {
                if (--remaining == 0) CommitCompleteTasks(pending, focusIndex);
            };
            item.BeginAnimation(FrameworkElement.MaxHeightProperty, shrink);
        }
    }

    /// <summary>动画结束后的统一提交：流转至 DONE（§9），落位到空缺处。
    /// 动画期间被其他操作移走的任务直接跳过。</summary>
    private void CommitCompleteTasks(List<TaskViewModel> tasks, int focusIndex)
    {
        foreach (var t in tasks) _completingTasks.Remove(t);
        var alive = tasks.Where(t => VM.Blocks.Any(b => b.Items.Contains(t))).ToList();
        if (alive.Count > 0) VM.CompleteTasks(alive);
        FocusTaskAtIndex(focusIndex);
    }

    // ==================== 撤销回归动画：让位展开 → 灰态浮现 → 颜色恢复 → 取消勾选 ====================

    /// <summary>带回归动画的撤销：全量重建后按内容键做多重集 diff，「回来的任务」播
    /// 完成动画的倒放（高度展开让位、灰态渐显、颜色恢复、勾选框取消勾选）。</summary>
    private void UndoWithAnimation()
    {
        var before = TaskKeyCounts();
        // 选中按排序位置记忆（而非任务引用）：任务流走后位置由下一个占据，
        // 撤销把任务送回原位时，选中落在同一位置——自然回到被操作的任务
        var selectedIndex = VM.SelectedTask is { } st
            ? TaskList.Items.OfType<TaskViewModel>().ToList().IndexOf(st)
            : -1;
        var scroll = VisualTreeEx.FindVisualChildren<ScrollViewer>(TaskList).FirstOrDefault();
        var offset = scroll?.VerticalOffset ?? 0;
        VM.Undo();   // 同步重建文档（TaskViewModel 全部为新实例，只能按内容键比较）
        var restored = new List<TaskViewModel>();
        foreach (var t in VM.Blocks.SelectMany(b => b.Tasks))
        {
            var key = TaskKey(t);
            if (before.TryGetValue(key, out var n) && n > 0) before[key] = n - 1;
            else restored.Add(t);
        }
        // 快照重建不携带视图状态：选中位置与滚动位置按原值恢复
        if (selectedIndex >= 0)
        {
            var tasks = TaskList.Items.OfType<TaskViewModel>().ToList();
            VM.SelectedTask = tasks.Count == 0
                ? null
                : tasks[Math.Clamp(selectedIndex, 0, tasks.Count - 1)];
        }
        if (restored.Count == 0 && scroll == null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            scroll?.ScrollToVerticalOffset(offset);
            foreach (var task in restored) AnimateRestoreTask(task);
        }));
    }

    /// <summary>任务内容键（状态 + 主行 + 备注）：撤销全量重建后识别「同一任务」的多重集键。</summary>
    private static string TaskKey(TaskViewModel t)
        => $"{t.State}\n{t.HeaderText}\n{t.NotesText}";

    private Dictionary<string, int> TaskKeyCounts()
    {
        var counts = new Dictionary<string, int>();
        foreach (var t in VM.Blocks.SelectMany(b => b.Tasks))
        {
            var key = TaskKey(t);
            counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
        }
        return counts;
    }

    /// <summary>单个任务的回归动画：高度从 0 展开让位、灰态随展开浮现、颜色快速恢复。
    /// 勾选框全程保持未勾选（回归即「回到进行中」的直接呈现，不做「先勾后取消」的两段式）。</summary>
    private void AnimateRestoreTask(TaskViewModel task)
    {
        var item = ContainerOf(task);
        if (item == null) return;

        var height = item.ActualHeight;
        item.ClipToBounds = true;
        item.Opacity = 0;
        item.MaxHeight = 0;

        // 让位展开：其他任务随布局匀速让开；灰态同步浮现
        var grow = new DoubleAnimation(0, height, TimeSpan.FromMilliseconds(160))
        {
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut },
        };
        grow.Completed += (_, _) =>
        {
            // 先移除动画时钟（HoldEnd 会让终值压住属性），再清动画前写入的本地值 0，
            // 否则卡片被钉在折叠高度（时钟残留）或塌成 0（本地值残留）
            item.BeginAnimation(FrameworkElement.MaxHeightProperty, null);
            item.ClearValue(FrameworkElement.MaxHeightProperty);
        };
        item.BeginAnimation(FrameworkElement.MaxHeightProperty, grow);

        // 颜色恢复：灰（0.45）随展开出现，再快速渐变回正常
        item.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimationUsingKeyFrames
        {
            KeyFrames =
            {
                new LinearDoubleKeyFrame(0.0, TimeSpan.Zero),
                new LinearDoubleKeyFrame(0.45, TimeSpan.FromMilliseconds(160)),
                new LinearDoubleKeyFrame(1.0, TimeSpan.FromMilliseconds(300)),
            },
        });
    }
}
