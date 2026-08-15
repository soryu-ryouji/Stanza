using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Stanza.App.Services;
using Stanza.Core;

namespace Stanza.App;

/// <summary>
/// 设置面板：语言切换（Loc，即时生效）+ 快捷键编辑（录制 / 添加 / 删除 / 重置 / 冲突转移）。
/// 浮层与退出遮罩同款：同一视觉树、模态收编键盘、Esc 或点空白关闭。
/// </summary>
public partial class MainWindow
{
    /// <summary>设置面板中列出的命令（SelectBlock 展开为四个参数实例）。</summary>
    private static readonly (AppCommand Command, string? Parameter)[] CommandCatalog =
    [
        (AppCommand.Save, null),
        (AppCommand.Open, null),
        (AppCommand.NewTask, null),
        (AppCommand.NewDocument, null),
        (AppCommand.SelectBlock, "1"),
        (AppCommand.SelectBlock, "2"),
        (AppCommand.SelectBlock, "3"),
        (AppCommand.SelectBlock, "4"),
    ];

    private (AppCommand Command, string? Parameter)? _recording;   // 正在为之录制新键位的命令
    private PendingConflict? _conflict;                            // 待确认的键位冲突

    private sealed record PendingConflict(
        ModifierKeys Modifiers, Key Key, AppCommand Owner, string? OwnerParameter);

    // ==================== 打开 / 关闭 ====================

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        LangZh.IsChecked = Loc.Current == "zh";
        LangEn.IsChecked = Loc.Current == "en";
        RebuildKeymapRows();
        SettingsOverlay.Visibility = Visibility.Visible;
        // 焦点必须落在浮层内，PreviewKeyDown（隧道）才会经过浮层
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => LangZh.Focus()));
    }

    private void HideSettings()
    {
        StopRecording();
        SettingsOverlay.Visibility = Visibility.Collapsed;
        Keyboard.ClearFocus();
    }

    private void Settings_DimMouseDown(object sender, MouseButtonEventArgs e) => HideSettings();

    /// <summary>挂在设置浮层上（隧道）。录制中：按键全部吃掉作为候选手势；否则模态过滤。</summary>
    private void Settings_KeyDown(object sender, KeyEventArgs e)
    {
        if (_recording is { } target)
        {
            CaptureGesture(target, e);
            return;
        }
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            HideSettings();
            return;
        }
        // 模态：焦点导航/激活键之外不穿透到主界面（含应用快捷键）；
        // Enter 落在非按钮上时拦下，避免冒泡到主窗口触发展开/收起
        if (e.Key is not (Key.Tab or Key.Enter or Key.Space))
            e.Handled = true;
        else if (e.Key == Key.Enter && Keyboard.FocusedElement is not ButtonBase)
            e.Handled = true;
    }

    // ==================== 语言 ====================

    private void Lang_Checked(object sender, RoutedEventArgs e)
    {
        var language = ReferenceEquals(sender, LangEn) ? "en" : "zh";
        if (language == Loc.Current) return;   // 打开面板回填选中态也会触发 Checked
        Loc.SetLanguage(language);
        SettingsStore.Save(new AppSettings { Language = language });
        RebuildKeymapRows();   // 命令名随语言变化
    }

    // ==================== 快捷键行 ====================

    private static string CommandName(AppCommand command, string? parameter)
        => command == AppCommand.SelectBlock
            ? Loc.Format("Cmd_SelectBlock", BlockToken(parameter))
            : Loc.Get("Cmd_" + command);

    private static string BlockToken(string? parameter)
        => int.TryParse(parameter, out var i) && i >= 1 && i <= TaskStateNames.CanonicalOrder.Length
            ? Loc.StateName(TaskStateNames.CanonicalOrder[i - 1])
            : "?";

    private void RebuildKeymapRows()
    {
        KeymapRows.Children.Clear();
        foreach (var (command, parameter) in CommandCatalog)
        {
            var row = new Grid { Margin = new Thickness(0, 3, 0, 3) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            row.Children.Add(new TextBlock
            {
                Text = CommandName(command, parameter),
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
            });

            var chips = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(chips, 1);
            var entries = Keymap.Current.EntriesFor(command, parameter);
            foreach (var entry in entries)
                chips.Children.Add(MakeGestureChip(command, parameter, entry));
            if (entries.Count == 0)
            {
                chips.Children.Add(new TextBlock
                {
                    Text = Loc.Get("Settings_NoneGesture"),
                    Foreground = (Brush)FindResource("FaintBrush"),
                    FontSize = 11.5,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 0, 0),
                });
            }
            var addButton = new Button
            {
                Content = "+",
                Style = (Style)FindResource("GhostButton"),
                FontSize = 11,
                Padding = new Thickness(7, 0, 7, 0),
                Margin = new Thickness(2, 0, 0, 0),
            };
            addButton.Click += (_, _) => StartRecording((command, parameter));
            chips.Children.Add(addButton);
            row.Children.Add(chips);

            if (Keymap.Current.HasOverride(command, parameter))
            {
                var reset = new Button
                {
                    Content = Loc.Get("Settings_Reset"),
                    Style = (Style)FindResource("GhostButton"),
                    FontSize = 11,
                    Padding = new Thickness(6, 1, 6, 1),
                };
                Grid.SetColumn(reset, 2);
                reset.Click += (_, _) =>
                {
                    Keymap.Current.SetOverride(command, parameter, null);
                    RebuildKeymapRows();
                };
                row.Children.Add(reset);
            }

            KeymapRows.Children.Add(row);
        }
    }

    /// <summary>键位 chip：手势文本 + 移除按钮。</summary>
    private UIElement MakeGestureChip(AppCommand command, string? parameter, KeymapEntry entry)
    {
        var remove = new Button
        {
            Content = "×",
            Style = (Style)FindResource("GhostButton"),
            FontSize = 10,
            Padding = new Thickness(2, 0, 2, 0),
            Margin = new Thickness(3, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        remove.Click += (_, _) =>
        {
            var rest = Keymap.Current.EntriesFor(command, parameter)
                .Where(x => !(x.Modifiers == entry.Modifiers && x.Key == entry.Key))
                .Select(x => (x.Modifiers, x.Key))
                .ToList();
            SetGestures(command, parameter, rest);
            RebuildKeymapRows();
        };
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        panel.Children.Add(new TextBlock
        {
            Text = Gesture.Format(entry.Modifiers, entry.Key),
            FontSize = 11.5,
            VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(remove);
        return new Border
        {
            Background = (Brush)FindResource("AccentSoftBrush"),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 4, 1),
            Margin = new Thickness(0, 1, 4, 1),
            Child = panel,
        };
    }

    // ==================== 录制 ====================

    private void StartRecording((AppCommand Command, string? Parameter) target)
    {
        _recording = target;
        _conflict = null;
        ConflictBar.Visibility = Visibility.Collapsed;
        RecordingHint.Text = Loc.Get("Settings_Recording");
        RecordingHint.Foreground = (Brush)FindResource("GrayBrush");
        RecordingHint.Visibility = Visibility.Visible;
    }

    private void StopRecording()
    {
        _recording = null;
        _conflict = null;
        RecordingHint.Visibility = Visibility.Collapsed;
        ConflictBar.Visibility = Visibility.Collapsed;
    }

    private void CaptureGesture((AppCommand Command, string? Parameter) target, KeyEventArgs e)
    {
        e.Handled = true;   // 录制中所有按键都不穿透（含 Enter/Space/Tab 与应用快捷键）
        var key = e.Key == Key.System ? e.SystemKey : e.Key;   // Alt 组合的主键在 SystemKey 上
        var modifiers = Keyboard.Modifiers;

        if (key == Key.Escape && modifiers == ModifierKeys.None)
        {
            StopRecording();   // Esc 取消录制
            return;
        }
        if (Gesture.IsModifierKey(key)) return;   // 纯修饰键：继续等主键
        if (!Gesture.IsAllowedShortcut(modifiers, key))
        {
            RecordingHint.Text = Loc.Get("Settings_NeedModifier");
            RecordingHint.Foreground = (Brush)FindResource("DangerBrush");
            return;
        }

        // 冲突：同一手势已属于其他命令 → 提示，确认后从对方移除（键位全局唯一）
        var existing = Keymap.Current.Resolve(key, modifiers);
        if (existing != null
            && (existing.Command != target.Command || existing.Parameter?.ToString() != target.Parameter))
        {
            _conflict = new PendingConflict(modifiers, key, existing.Command, existing.Parameter?.ToString());
            ConflictText.Text = Loc.Format("Settings_Conflict",
                Gesture.Format(modifiers, key), CommandName(existing.Command, existing.Parameter?.ToString()));
            ConflictBar.Visibility = Visibility.Visible;
            return;
        }

        AddGesture(target, modifiers, key);
    }

    private void ConflictConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (_conflict is not { } conflict || _recording is not { } target) return;
        var rest = Keymap.Current.EntriesFor(conflict.Owner, conflict.OwnerParameter)
            .Where(x => !(x.Modifiers == conflict.Modifiers && x.Key == conflict.Key))
            .Select(x => (x.Modifiers, x.Key))
            .ToList();
        SetGestures(conflict.Owner, conflict.OwnerParameter, rest);
        AddGesture(target, conflict.Modifiers, conflict.Key);
    }

    private void ConflictCancel_Click(object sender, RoutedEventArgs e) => StopRecording();

    private void AddGesture((AppCommand Command, string? Parameter) target, ModifierKeys modifiers, Key key)
    {
        var gestures = Keymap.Current.EntriesFor(target.Command, target.Parameter)
            .Select(x => (x.Modifiers, x.Key))
            .ToList();
        if (!gestures.Contains((modifiers, key)))   // 重复录入同一手势：视为完成
            gestures.Add((modifiers, key));
        SetGestures(target.Command, target.Parameter, gestures);
        StopRecording();
        RebuildKeymapRows();
    }

    /// <summary>写某命令的键位集合；与默认一致时不落覆盖（保持用户文件为纯增量）。</summary>
    private static void SetGestures(AppCommand command, string? parameter, List<(ModifierKeys, Key)> gestures)
    {
        var defaults = Keymap.Defaults
            .Where(x => x.Command == command && x.Parameter?.ToString() == parameter)
            .Select(x => (x.Modifiers, x.Key))
            .ToList();
        var sameAsDefaults = defaults.Count == gestures.Count && defaults.All(gestures.Contains);
        Keymap.Current.SetOverride(command, parameter, sameAsDefaults ? null : gestures);
    }

    // ==================== 动态快捷键提示 ====================

    /// <summary>含快捷键的 tooltip / 空态提示：按当前键位与语言合成。构造、语言切换、键位变更时调用。</summary>
    private void RefreshShortcutHints()
    {
        OpenButton.ToolTip = WithGesture("Tip_OpenFile", AppCommand.Open);
        NewDocButton.ToolTip = WithGesture("Tip_NewFile", AppCommand.NewDocument);
        RecentNewButton.ToolTip = WithGesture("Tip_NewFile", AppCommand.NewDocument);
        AddTaskButton.ToolTip = WithGesture("Tip_AddTask", AppCommand.NewTask);
        EmptyHint.Text = WithGestureInline("Empty_NoTasks", AppCommand.NewTask);
        WelcomeOpenHint.Text = WithGestureInline("Welcome_OpenHint", AppCommand.Open);
        WelcomeNewHint.Text = WithGestureInline("Welcome_NewHint", AppCommand.NewDocument);
    }

    private static string WithGesture(string tipKey, AppCommand command)
    {
        var gestures = Keymap.Current.Describe(command, null);
        return gestures == null
            ? Loc.Get(tipKey)
            : Loc.Get(tipKey) + string.Format(Loc.Get("Gesture_Suffix"), gestures);
    }

    private static string WithGestureInline(string textKey, AppCommand command)
        => Loc.Format(textKey, Keymap.Current.Describe(command, null) ?? Loc.Get("Settings_NoneGesture"));
}
