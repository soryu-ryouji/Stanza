# 设计文档：TaskViewModel 去 owner 化（方案一）

> 状态：已实现（构建 0 错误 0 警告；App 13 + Core 82 全部通过）
> 作者：重构执行者
> 审核对象：其他 AI / 评审人
> 关联：`docs/ARCHITECTURE.md`（当前架构基线）

## 1. 摘要

消除 `TaskViewModel` 对 `MainViewModel` 的**反向依赖**（子 → 父）：任务视图模型不再持有文档视图模型的引用，改为通过 `ContentChanged` 事件向订阅者（即 `MainViewModel`）通知内容变化。

预期收益：任务 VM 可脱离 WPF 运行时独立测试；依赖方向恢复为单向（父 → 子）；通知语义由「调用具体对象的方法」改为「声明变化、由订阅者响应」。

## 2. 背景与现状（代码事实）

### 2.1 依赖关系

```
MainViewModel ──持有──▶ TaskViewModel        （集合 Items 持有实例）
      ▲                     │
      └──────◀───引用───────┘                 （TaskViewModel._owner 字段）
```

`TaskViewModel` 持有 `MainViewModel` 引用，唯一用途是调用 `_owner.NotifyContentChanged()`。

### 2.2 调用点清单（当前行号，`src/Stanza.App/ViewModels/TaskViewModel.cs`）

| 行号 | 代码 |
| ---- | ---- |
| 13 | `private readonly MainViewModel _owner;`（字段） |
| 38 | `public TaskViewModel(MainViewModel owner) => _owner = owner;`（构造） |
| 109 | `_owner.NotifyContentChanged();`（`HeaderText` setter） |
| 192 | `_owner.NotifyContentChanged();`（`CommitHeader`） |
| 215 | `_owner.NotifyContentChanged();`（`Priority` setter） |
| 259 | `_owner.NotifyContentChanged();`（`SetCreated`） |
| 267 | `_owner.NotifyContentChanged();`（`AppendCompleted`） |
| 316 | `_owner.NotifyContentChanged();`（`NotesText` setter） |
| 329 | `_owner.NotifyContentChanged();`（`AfterMetaMutation`，标签/项目批量操作） |

7 处调用全部是同一行代码，无其他用途。

### 2.3 MainViewModel 侧创建点（`src/Stanza.App/ViewModels/MainViewModel.cs`）

| 行号 | 代码 | 场景 |
| ---- | ---- | ---- |
| 352 | `var task = new TaskViewModel(this) { State = block.State };` | `CreateTask`（新建任务） |
| 550 | `block.Items.Add(TaskViewModel.FromModel(this, t, state));` | `LoadDocument`（打开/撤销/新建文档时全量重建） |

### 2.4 测试现状（`tests/Stanza.App.Tests/TaskViewModelTests.cs`）

为创建任务 VM，测试被迫先创建 `MainViewModel`，连带触发：

- `Application.Current`（`Loc` 静态类依赖）→ 需要 STA 线程 + Application 单例
- `DispatcherTimer` × 2（自动保存/状态清除）→ 需要 STA 线程
- `ListCollectionView`（面板分组视图）→ 需要 Dispatcher
- `RecentFilesViewModel` 构造 → 读 `%APPDATA%` 真实文件（测试用 `APPDATA` 环境变量重定向隔离）
- `Keymap.Current` 静态初始化 → 读 `settings.json`

因此 `TaskViewModelTests` 全部 6 个测试必须使用 `StaTestHost`（STA 线程执行）+ `[Collection("AppData")]`（串行化，避免配置读写竞争）。

## 3. 问题分析

### 3.1 反向依赖

任务模型本身不知道也不应知道自己属于哪个文档：`FromModel` 按区块状态重建任务实例，跨文档切换、撤销时实例整体替换。但「报告内容变化」这一单向通知需求，却被实现为持有整个文档 VM 的引用——`TaskViewModel` 因而依赖 `MainViewModel` 的全部公开面（虽然只使用其中一个方法）。

### 3.2 测试成本失衡

任务 VM 的「编辑文本 ↔ 结构化属性」是纯文本逻辑（依赖仅 `StanzaParser`/`StanzaWriter`/`Regex`，均在 Core 或 BCL），但测试它需要启动半个 WPF 运行时。这提高了补测试的门槛（新增边界用例需经过 STA 基座），也让 6 个现有测试耦合于环境细节（串行化、APPDATA 隔离）。

### 3.3 语义：通知 vs 命令

`_owner.NotifyContentChanged()` 是命令式调用；语义上任务 VM 需要的只是「声明内容变化，让持有者决定如何响应」。事件是该语义的标准表达，且天然支持多订阅者——任务当前同时存在于两个容器（区块列表 `BlockViewModel.Items` 与面板列表 `_panelTasks`），未来新增视图（统计、导出预览等）时事件模型可直接挂订阅，引用模型做不到。

### 3.4 参照系：其他 VM 均不引用 MainViewModel

`BlockViewModel`、`FacetItemViewModel`、`RecentFilesViewModel` 都不持有 `MainViewModel` 引用（依赖经构造参数回调或集合注入）。`TaskViewModel` 是唯一的例外，方案一将其对齐到同一模式。

## 4. 方案设计

### 4.1 目标

1. 消除 `TaskViewModel → MainViewModel` 引用，恢复单向依赖（父 → 子）。
2. 任务 VM 可独立于 `MainViewModel` 实例化与测试。
3. 行为零变化：7 处通知调用点的触发时机、通知语义与现状完全一致。

### 4.2 非目标（明确不做）

- 不引入消息总线 / `INotifyPropertyChanged` 路由 / DI 容器——单窗口应用不需要间接层。
- 不改 `MainViewModel.NotifyScopeChanged()` 的 12 属性广播——手写 MVVM 的固有成本，抽象收益不成立。
- 不抽象 `DispatcherTimer` / `ListCollectionView`——它们对文档级 VM 是正确工具。
- 不做 `FacetAggregator` 提取、`SelectionState` 状态机提取——无当前需求驱动，另立方案。

### 4.3 改动清单

#### A. `TaskViewModel.cs`

```csharp
// 删除字段与构造参数（不新增显式构造函数，编译器自动生成无参构造）
- private readonly MainViewModel _owner;
- public TaskViewModel(MainViewModel owner) => _owner = owner;

// 工厂签名去掉 owner（调用点仅 MainViewModel.LoadDocument 与测试）；
// FromModel 内部第 42 行 new TaskViewModel(owner) 同步改为 new TaskViewModel()
- public static TaskViewModel FromModel(MainViewModel owner, StanzaTask model, TaskState state)
+ public static TaskViewModel FromModel(StanzaTask model, TaskState state)

// 新增事件与私有通知方法
+ public event EventHandler? ContentChanged;
+ private void NotifyContentChanged() => ContentChanged?.Invoke(this, EventArgs.Empty);
```

7 处 `_owner.NotifyContentChanged();` 替换为 `NotifyContentChanged();`（机械替换，触发时机不变）。
类注释「任何修改都通知 MainViewModel 触发自动保存」同步改为「经 ContentChanged 事件通知持有方」。

#### B. `MainViewModel.cs`（2 处创建点 + 挂接辅助 + 处理器）

挂接收敛为私有辅助方法 `Track`：两处创建点都走它，把「新增创建点必须记得挂接」从约定变成结构。

```csharp
// 挂接辅助与统一处理器
+ private TaskViewModel Track(TaskViewModel task)
+ {
+     task.ContentChanged += OnTaskContentChanged;
+     return task;
+ }
+ private void OnTaskContentChanged(object? sender, EventArgs e) => NotifyContentChanged();

// CreateTask（行 352）
- var task = new TaskViewModel(this) { State = block.State };
+ var task = Track(new TaskViewModel { State = block.State });

// LoadDocument（行 550）
- block.Items.Add(TaskViewModel.FromModel(this, t, state));
+ block.Items.Add(Track(TaskViewModel.FromModel(t, state)));
```

**挂接时序约束**：挂接必须紧跟构造。`CreateTask` 在构造后立即调用 `SetCreated`（行 354），其内部走通知路径——挂接若晚于 `SetCreated`，该次通知丢失（虽有 `CreateTask` 末尾的显式 `NotifyContentChanged()` 兜底，行为不变，但语义应保持一致）。`Track` 把挂接固定在构造表达式处，天然满足该约束。

#### C. 测试（`TaskViewModelTests.cs`）

- 移除 `[Collection("AppData")]`、`StaTestHost.StaFactBase` 继承与 `OnUi(...)` 包装（不再涉及 WPF 环境；不碰 Application/文件系统，与仍需 STA 的 `MainViewModelTests` 并行无竞争）
- 测试辅助方法一行化：`private static TaskViewModel NewTask() => new();`
- owner 相关调用共 3 处：`FromModel(owner, model, state)` 2 处（行 31、95）去掉 owner 参数；`new TaskViewModel(vm)` 1 处（`NewTask` 辅助方法内）随一行化消除
- 新增 1 个事件链路测试：订阅 `ContentChanged` → 修改 `HeaderText` → 断言事件触发（显式钉住「任务变化 → 文档脏追踪」链路，目前仅被 `SaveOpen_RoundTripsDocument` 间接覆盖）

### 4.4 事件命名与可见性

- 公开事件名 `ContentChanged`，与 `NotifyContentChanged`（MainViewModel 的公开方法名）区分——前者是任务级通知，后者是文档级标脏入口。
- 不采用 `IReadOnlyList`/结构化事件参数：通知不需要携带变更内容，订阅方统一响应「有变化」。

## 5. 优点

| 维度 | 说明 |
| ---- | ---- |
| 可测试性 | 任务 VM 测试脱离 STA/Application/文件 IO 环境；新增边界用例无门槛 |
| 依赖方向 | TaskViewModel 零 VM 依赖，对齐 BlockViewModel/FacetItemViewModel 模式 |
| 多订阅者 | 任务同时存在于区块与面板两个容器；未来新视图直接挂订阅 |
| 语义 | 「声明变化」取代「调用具体对象的方法」；MainViewModel 通过订阅表达「任务内容变化 = 文档内容变化」 |
| 改动面 | 1 个文件的行为代码 + 2 处创建点 + 测试；无公开 API 语义变化（`FromModel` 签名变化仅 2 个调用点） |

## 6. 风险与缓解

| 风险 | 分析 | 缓解 |
| ---- | ---- | ---- |
| 事件泄漏 | MainViewModel 订阅任务事件，任务被 MainViewModel 的集合持有——循环引用，托管堆可回收；无非托管资源 | 无需处理；如需防御可在 `DetachTask` 时退订（当前不必要） |
| 撤销/重建后的订阅 | `Undo`/`LoadDocument` 全量重建任务实例，每次重建都经 `Track` 重新订阅新实例；旧实例随集合替换脱钩即不可达，委托指向 MainViewModel 不阻碍其回收 | 无 |
| 新增创建点漏挂事件 | 事件挂接依赖创建点自觉，漏挂则脏追踪静默失效（运行时才暴露） | `Track` 辅助方法收敛全部实例化路径，挂接成为结构而非约定 |
| 自动保存链路回归 | 编辑 → 事件 → `NotifyContentChanged` → IsDirty + 状态栏 + 1.2s 计时器 | 新增事件链路测试 + 现有 `SaveOpen_RoundTripsDocument` 回归 |
| `FromModel` 签名变化 | 编译期错误暴露所有调用点（MainViewModel 1 处 + 测试 3 处），无运行时风险 | 编译 + 测试 |
| 行为差异 | 7 处调用点仅替换调用对象，触发时机不变 | 全量测试（App 12 + Core 82） |

## 7. 验证计划

1. `dotnet build Stanza.slnx`：0 错误 0 警告。
2. `dotnet test`：App 层 12 个（含新增事件链路测试）+ Core 82 个用例（45 个 `[Fact]` + 37 个 `[Theory]` 数据行，共 56 个方法）全部通过。
3. 重点回归：撤销（Undo → LoadDocument 重建 → 重新订阅）、自动保存（编辑 → 事件 → 标脏 → 计时器）、新建任务（CreateTask → 订阅）。

## 8. 审核检查清单（请审核方逐项确认）

**事实核对**
- [x] 7 处 `_owner.NotifyContentChanged()` 调用点是否准确、有无遗漏？——准确（行号全部吻合，grep 无遗漏）
- [x] `TaskViewModel` 对 `MainViewModel` 是否真的只有 `NotifyContentChanged` 一个使用面？——是
- [x] 2 处创建点（CreateTask/LoadDocument）是否覆盖全部实例化路径？——是（测试 3 处已列出，XAML 无实例化）

**设计决策**
- [x] 事件 vs 保留引用：事件是「通知」语义的正确表达，当前规模下非过度设计
- [x] 公开事件 `ContentChanged` 的命名与可见性是否合适？——合适；无需变更类型参数（无消费方）
- [x] 非目标清单（4.2）是否有应纳入本次范围的遗漏？——无

**风险**
- [x] 循环引用无泄漏的判断是否成立？——成立（GC 按可达性，重建后旧实例即不可达）
- [x] 撤销重建路径的订阅生命周期分析是否完整？——完整
- [x] 是否存在本方案未覆盖的隐式依赖（如反射、序列化、XAML 绑定对构造签名的假设）？——无（XAML 仅 `DataTemplate DataType` 引用，不实例化）

## 9. 评审结论与修正记录

评审通过，方案按原设计执行。评审发现的问题与对应修正：

| 评审发现 | 修正 |
| ---- | ---- |
| §7「Core 82 个」口径不明（方法数实为 56） | 已注明：82 = 45 Fact + 37 Theory 数据行 |
| §4.3 C「FromModel 3 处调用」不准确 | 实为 2 处 `FromModel` + 1 处 `new`（`NewTask` 辅助方法内），已修正 |
| §4.3 A 未列 `FromModel` 内部第 42 行实例化 | 已补充 |
| 未说明 `CreateTask` 中 `SetCreated` 依赖通知路径的挂接时序 | 已在 §4.3 B 补充时序约束 |
| 事件挂接依赖创建点自觉，存在漏挂风险 | §4.3 B 改用 `Track` 辅助方法收敛挂接，风险表新增一行 |
| `public TaskViewModel() { }` 冗余（编译器自动生成） | 已改为不新增显式构造 |

评审中讨论的备选方案：构造注入 `Action onContentChanged`（编译期强制通知渠道、与项目回调注入约定一致）。结论：与事件近乎等效，不换案——回调在项目中的语义是「借用视图能力」（`PickOpenFile`/`UndoRequested`），用于通知反而混淆；事件与 `MainViewModel.TaskCreated` 已有先例同构。漏挂风险由 `Track` 辅助方法消除。
