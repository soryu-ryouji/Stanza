# AGENTS.md

本文件为 AI 编码助手提供项目上下文。人类可读的说明见 [README.md](README.md)。

## 项目概述

Stanza 是 [Stanza 纯文本任务管理格式](https://github.com/Ryougi-Shiki0/Stanza)（RFC 1.0.0）的 Windows 桌面编辑器。WPF + MVVM，零第三方依赖，目标框架 .NET 10。

- 解决方案文件为 `Stanza.slnx`（XML 格式，需要较新的 .NET SDK / VS 支持）
- 整个仓库约 2900 行代码，体量小，改动前先通读相关文件

## 常用命令

```bash
dotnet build                                   # 构建
dotnet run --project src/Stanza.App            # 启动
dotnet run --project src/Stanza.App -- TODO.stanza   # 启动并打开文件
dotnet test                                    # 全部测试（xunit）
dotnet test --filter "FullyQualifiedName~ParserTests"  # 只跑解析器测试
```

发布用 `publish.ps1`（生成单文件 exe 到 `publish/<runtime>/`，默认先跑测试）。发布前需关闭正在运行的 Stanza.exe，否则文件占用导致失败。

## 项目结构与职责边界

```
src/Stanza.Core        解析器 / 写出器 / 文档模型 / §9 流转与排序规则。禁止依赖任何 UI 或 WPF 程序集
src/Stanza.App         WPF 应用（MVVM）。所有 UI、交互、Windows 互操作
tests/Stanza.Core.Tests  只测 Stanza.Core：RFC §10.3 全部用例 + 解析→写出往返一致性 + §9 流转与排序规则
```

- `Stanza.Core` 是纯函数式静态 API：`StanzaParser.Parse(string)` → `StanzaDocument` → `StanzaWriter.Write(doc)`。模型类（`StanzaDocument` / `StanzaBlock` / `StanzaTask`）是普通可变 POCO。§9 流转规范化（`TaskTransitions`）与活跃排序（`ActiveTaskOrdering`）的规则唯一来源也在 Core，App 层只编排 ViewModel 集合，不得另行实现
- `MainWindow` 按职责拆为 partial class：`MainWindow.xaml.cs`（主体）、`MainWindow.Drag.cs`（拖拽排序/跨区块移动）、`MainWindow.Panels.cs`（面板/侧栏）、`MainWindow.Windowing.cs`（窗口 chrome、拖动、最大化）。新增窗口交互代码时放进对应的 partial 文件
- ViewModel 层使用自实现的 `ViewModelBase` / `RelayCommand`，不引入 CommunityToolkit.Mvvm 等外部包
- Win32 互操作集中在 `Services/NativeMethods.cs`

## 必须遵守的行为不变量

改动解析、写出或任务状态流转逻辑时，以下行为不能破坏（有测试覆盖，改完必须 `dotnet test`）：

1. **解析严格遵循 RFC 1.0.0**：四状态区块、主行元数据（`(A)` 优先级、`YYYY-MM-DD` 截止、`+项目`、`#标签`）、缩进续行、备注内空行。代码注释中用 `§x.y` 引用 RFC 条款，新增规则时保持这个习惯
2. **保存为规范化输出**：UTF-8 无 BOM、LF 换行、区块标题大写、按 DOING / WAIT / DONE / DELETE 顺序、任务间空行分隔
3. **解析→写出往返一致**：`Write(Parse(text))` 的结果是规范形式，再解析语义不变
4. **「完成」语义（RFC §9）**：移到 DONE 顶部、移除优先级、在备注末尾追加完成日期。「废弃」移到 DELETE 顶部，「恢复」移回 DOING 末尾
5. **DOING / WAIT 自动排序**：按（优先级 → 截止日期）稳定排序；拖拽只调整同优先级内的相对顺序
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
