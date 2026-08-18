# AGENTS.md

本文件为 AI 编码助手提供项目上下文。人类可读的说明见 [README.md](README.md)。

## 项目概述

Stanza 是 [Stanza 纯文本任务管理格式](https://github.com/Ryougi-Shiki0/Stanza)（RFC 1.5.0）的 Windows 桌面编辑器。WPF + MVVM，零第三方依赖，目标框架 .NET 10。

- 解决方案文件为 `Stanza.slnx`（XML 格式，需要较新的 .NET SDK / VS 支持）
- 仓库体量小（C# 约 5000 行，不含 XAML），改动前先通读相关文件

## 常用命令

```bash
dotnet build                                   # 构建
dotnet run --project src/Stanza.App            # 启动
dotnet run --project src/Stanza.App -- TODO.stanza   # 启动并打开文件
dotnet test                                    # 全部测试（xunit）
dotnet test --filter "FullyQualifiedName~ParserTests"  # 只跑解析器测试
powershell -NoProfile -ExecutionPolicy Bypass -File tools/verify-input.ps1   # 键盘输入 UI 自动化回归（hjkl/方向键导航、中文 IME、Space 完成与撤销、Ctrl+R 快速打开）
powershell -NoProfile -ExecutionPolicy Bypass -File tools/capture-screenshots.ps1   # 自动截取应用截图到仓库根目录 .assets/（演示数据为 tools/demo.stanza）
```

发布用 `tools/publish.ps1`（生成单文件 exe 到 `publish/<runtime>/`，默认先跑测试；`-CopyTo <dir>` 可额外把 exe 复制到指定目录）。发布前需关闭正在运行的 Stanza.exe，否则文件占用导致失败。

## 项目结构与职责边界

```
src/Stanza.Core        解析器 / 写出器 / 文档模型 / §9 流转与排序规则。禁止依赖任何 UI 或 WPF 程序集
src/Stanza.App         WPF 应用（MVVM）。所有 UI、交互、Windows 互操作
tests/Stanza.Core.Tests  只测 Stanza.Core：RFC §10.3 全部用例 + 解析→写出往返一致性 + §9 流转与排序规则
```

- `Stanza.Core` 是纯函数式静态 API：`StanzaParser.Parse(string)` → `StanzaDocument` → `StanzaWriter.Write(doc)`。模型类（`StanzaDocument` / `StanzaBlock` / `StanzaTask`）是普通可变 POCO。§9 流转规范化（`TaskTransitions`）与活跃排序（`ActiveTaskOrdering`）的规则唯一来源也在 Core，App 层只编排 ViewModel 集合，不得另行实现
- `MainWindow` 按职责拆为 partial class：`MainWindow.xaml.cs`（主体、退出确认、文件对话框）、`MainWindow.Drag.cs`（拖拽排序/跨区块移动、按键分发）、`MainWindow.Panels.cs`（侧栏与弹层交互、项目/标签列表快速跳转预览（无选中任务时 P/T 进入）、勾选动画、文件拖入）、`MainWindow.Settings.cs`（设置面板：语言与快捷键）、`MainWindow.Windowing.cs`（无边框窗口的系统集成）。新增窗口交互代码时放进对应的 partial 文件
- 快捷键的唯一来源是 `Keymap.cs`：`AppCommand` 枚举（用户键位文件的稳定标识）+ 默认键位 + 用户覆盖合并（%APPDATA%/Stanza/keymap.json，VS Code 语义：出现的命令整体替换、空列表解绑）。默认键位分 Windows/macOS 两套（`DefaultsFor(MacOsMode)`：应用级命令修饰键 Ctrl↔Alt 整体互换，例外：OpenRecent 两模式同为 Ctrl+R——VS Code macOS 惯例；模式持久化在 settings.json，设置面板切换后 `Keymap.Current.Reload()` 生效）；命令分两类：`IsTaskScoped` 判定的任务作用域命令（仅任务列表焦点上下文分发，允许裸键）与应用级命令（全局分发，必须带修饰键）；新增命令默认按应用级（安全方向）。`Esc`/`Enter` 上下文多义不进表；选择器面板内的键硬编码在各面板的 PreviewKeyDown（标签/项目：`FacetPicker_KeyDown`；状态与优先级共用通用选择面板：`ChoicePicker_KeyDown`，行由 `ChoiceItem` 描述符驱动）
- 分发路径（`MainWindow.Drag.cs` 的 `OnPreProcessInput`）：应用命令查表后经 `VM.CommandFor` 执行；任务命令经 `TryExecuteTaskCommand` 执行——每个命令的焦点作用域谓词写死在分发处，用户改键只改触发手势，不改上下文语义（编辑框内输入、浮层内按键始终优先）
- 文本框编辑键（`TextEditKeys.cs`）：平台风格编辑手势（Windows 在 Alt、macOS 在 Ctrl；macOS 模式另有 Alt+C/X/V/A/Z 扮演 Command 且禁用原生 Ctrl 文本键）经类级 PreviewKeyDown 处理器挂到所有 TextBox，在 `OnPreProcessInput` 中先于应用命令让行（如 Windows 模式编辑框内 Alt+N 是下移一行，与 Ctrl+Z 文本级撤销同一先例）。备注框的 Enter 换行 / Ctrl+Enter 提交与列表续接（`NotesListEditing.cs`）硬编码在 `HandleTaskNotesKey`。Shift+方向键扩展选中：焦点在任务条目容器上时走 WPF 原生扩展，焦点落空时（`BridgeShiftArrow`）先把焦点放回选中边缘容器再走原生路由；Shift+jk 字母键无原生路由可借力，全程自处理（`TryShiftSelectTasks`，锚点+活动端区间）
- ViewModel 层使用自实现的 `ViewModelBase` / `RelayCommand`，不引入 CommunityToolkit.Mvvm 等外部包
- Win32 互操作集中在 `Services/NativeMethods.cs`

## 图标与滚动条

- 图标统一用字体字形：资源 `IconFont`（Segoe Fluent Icons / Segoe MDL2 Assets，WinUI FontIcon 同款方案），颜色经 `Foreground` 绑定联动。新图标优先查 MDL2 字形（窗口控制用 `&#xE921;`/`&#xE922;`/`&#xE8BB;` 系列），不要自绘 Path；仅无对应字形的组合图形（新建文档、备注标记）保留自绘
- 滚动条：隐式 `ScrollBar` 样式（细长圆角 thumb）覆盖所有滚动容器；任务列表用 `SlimScrollBar` 变体（`ScrollBarAutoHide` 行为驱动：滚动时淡入、闲置淡出）

## 必须遵守的行为不变量

改动解析、写出或任务状态流转逻辑时，以下行为不能破坏（有测试覆盖，改完必须 `dotnet test`）：

1. **解析严格遵循 RFC 1.5.0**：四状态区块、主行元数据（`(A)`–`(D)` 四象限优先级、`YYYY-MM-DD` 截止、`+项目`、`#标签`）、缩进续行、备注内空行、时间戳属性块（§7.4.3）。GUI 的编辑文本只含日期与描述：优先级、项目、标签是 ViewModel 上的结构化属性（右键菜单/选择器设置，键入的完整记号被自动捕获），写出时由 `ToModel` 重组。代码注释中用 `§x.y` 引用 RFC 条款，新增规则时保持这个习惯
2. **保存为规范化输出**：UTF-8 无 BOM、LF 换行、区块标题大写、按 DOING / WAIT / DONE / DELETE 顺序、任务间空行分隔
3. **解析→写出往返一致**：`Write(Parse(text))` 的结果是规范形式，再解析语义不变
4. **「完成」语义（RFC §9）**：移到 DONE 顶部、移除优先级、在属性块末尾追加完成日期（时间戳行集中在主行之后，与备注分离）。「废弃」移到 DELETE 顶部，「恢复」移回 DOING 末尾
5. **DOING / WAIT 自动排序**：按（优先级象限 → 截止日期）稳定排序；拖拽只调整同优先级内的相对顺序
6. **备注缩进**：编辑器内顶格显示，写出时统一 4 空格缩进；解析时原样保留续行内容（含缩进）

## 代码风格

- C#：`Nullable` 与 `ImplicitUsings` 均启用；file-scoped namespace；`sealed` 用于不打算继承的类
- **注释与 XML 文档注释用中文**，代码标识符用英文；`<summary>` 注释里引用 RFC 条款号（如 `RFC §7`）
- 正则统一 `RegexOptions.Compiled | RegexOptions.CultureInvariant`，解析器与写出器中相同的模式（如 `+项目`）保持字面一致
- 解析器容忍输入差异（BOM、CRLF、大小写、行尾空白），写出器输出唯一规范形式——「宽进严出」
- 不新增第三方 NuGet 依赖（App 层目前为零依赖，Core 层也为零）

## 测试约定

- xunit，`[Fact]` 为主；测试方法命名：`Case{N}_{场景}_{预期}`（对应 RFC §10.3 用例）或 `{方法}_{场景}_{预期}`
- 修改解析器边界行为时，优先补充/更新 RFC 用例对应的测试，再改实现
- `WriterTests.cs` 包含往返一致性测试，写出格式变更必须同步更新

## 常见陷阱

- 主行为空的任务无法被格式表示（会成为空白行被解析器跳过），写出时直接丢弃——不要试图为它发明转义
- `+` 项目与 `#` 标签的前导字符必须是行首或空白（否则 `C++`、`C#` 会被误解析），改正则时注意 lookbehind `(?<!\S)`
- 空白行的归属（任务分隔符 vs 备注内空行）取决于其后一行是否缩进，这是解析器最容易改错的部分，对应 `Case1` / `Case2` 测试
- `ItemsControl.ItemsSource` 重建后容器（行按钮）在布局阶段才异步生成：重建后同步遍历视觉树只能拿到旧容器。需要随行的状态（如选择器高亮）应放进项数据由绑定带出，代码遍历只用于不重建列表的瞬时迁移
- WPF 绑定对引用相等的新旧值短路不更新：集合类属性（如 `TaskViewModel.Tags`）变更时必须给出新实例，不能原地修改后复用同一引用
- WPF 不自动迁移已隐藏元素上的焦点：浮层 Collapsed 后焦点残留在不可见控件上，后续按键分发会落空。关闭浮层时检查焦点是否在其中，是则显式停回任务列表（`CloseFacetPicker` 的模式）
- WPF TextBox 的 `SelectedText` 赋值会把新插入的文本留为选区：程序化插入后需显式 `Select(末尾, 0)` 折叠回光标（`NotesListEditing` 的列表续接）；删除（赋空串）无此问题
- 中文 IME 会吞交互控件上的字母键：`StanzaControlBase` 基座样式统一禁用 IME（文本框显式恢复），新增可交互控件样式必须 BasedOn 基座，否则 hjkl 导航在中文布局下失效
