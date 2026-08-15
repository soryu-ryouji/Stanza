# Stanza for VS Code

**Stanza** 纯文本任务管理格式的 VS Code 语言支持插件（规范全文见同仓库 `stanza-rfc/Stanza-RFC-1.1.0.md`）。Stanza 受 TODO.txt 启发，通过状态区块（DOING / WAIT / DONE / DELETE）与段落式任务组织待办事项，单文件、纯文本、无锁定。

## 功能

### 语法高亮

高亮规则与 Stanza RFC 1.1.0 严格对齐：

| 元素 | 示例 | 说明 |
| ---- | ---- | ---- |
| 区块标题 | `# DOING` `# WAIT` `# DONE` `# DELETE` | 状态名大小写不敏感，容忍行尾空白 |
| 优先级 | `(A)` – `(Z)` | 仅在行首识别，右括号后恰一个空格 |
| 截止日期 | `2026-08-07` | 行首或紧跟优先级之后 |
| 项目 | `+Apollo` | `+` 前必须是行首或空白，`C++`、`A+B` 不误判 |
| 标签 | `#紧急` `#my_tag` | 首字符必须是字母（`#1` 不识别），支持中文、数字、`_`、`-` |
| 备注 | 缩进续行 | 整体弱化显示，内部不解析其他元数据 |
| 时间戳行 | `2026-08-05 创建` | 续行中整行匹配“日期 + 关键字”；英文别名 `created`/`completed` 大小写不敏感（RFC §7.4） |

与解析器一致的行为细节：

- `# DONIG`、`# DOING 杂记` 等不能整行匹配区块标题语法的行，按普通任务行处理（RFC §6.1）。
- 优先级与日期只有出现在行首（日期可紧跟优先级）才被识别，行中间的 `(A) 2026-08-07` 不高亮（RFC §7.2）。
- 空白行无高亮；缩进的非空白行一律视为备注续行（RFC §7.1、§7.3）。
- 时间戳行必须整行匹配才高亮：`2026-08-05 完成初稿` 这类含其他文字的行不高亮（RFC §7.4.1）。

### 代码片段（Snippets）

在 `.stanza` 文件中输入前缀后按 `Tab`（或 `Ctrl+Space` 触发补全）：

| 前缀 | 展开内容 |
| ---- | ---- |
| `doing` / `wait` / `done` / `delete` | 对应状态区块标题 |
| `task` | 带优先级、今天日期、项目、标签的任务主行 |
| `todo` | 普通任务主行（描述 + 项目） |
| `note` | 缩进备注续行 |
| `created` | 创建时间戳行（今天的日期） |
| `completed` | 完成时间戳行（今天的日期） |
| `date` | 今天的日期（`YYYY-MM-DD`） |

### 折叠

启用了基于缩进的折叠（offSide）：任务主行下方的备注续行可以折叠收起。

## 安装

### 方式一：VSIX 安装包（推荐）

从 [GitHub Releases](https://github.com/soryu-ryouji/StanzaVSCode/releases) 下载最新的 `.vsix` 安装包（也可自行打包，见下文），任选一种方式安装：

- **图形界面**：VS Code 中打开扩展面板（`Ctrl+Shift+X`）→ 右上角 `···` → `Install from VSIX...` → 选择该文件；
- **命令行**：`code --install-extension vscode-stanza-1.2.0.vsix`。

安装后打开任意 `.stanza` 文件即可生效。

### 方式二：手动复制（开发调试用）

1. 将本仓库克隆到 VS Code 扩展目录下：
   - **Windows**：`git clone https://github.com/soryu-ryouji/StanzaVSCode.git "%USERPROFILE%\.vscode\extensions\vscode-stanza"`
   - **macOS / Linux**：`git clone https://github.com/soryu-ryouji/StanzaVSCode.git ~/.vscode/extensions/vscode-stanza`
2. 重启 VS Code，打开任意 `.stanza` 文件。

## 打包与分发（维护者）

### 重新打包 VSIX

`package.json` 中已配好 `package` 脚本，首次安装开发依赖后，一条命令出包：

```bash
npm install       # 仅首次，安装 @vscode/vsce 到 devDependencies
npm run package   # 生成 vscode-stanza-<版本号>.vsix
```

包名与版本号取自 `package.json` 的 `name` 和 `version`，发新版前先递增 `version`。分发时直接把 `.vsix` 文件发给用户，或上传到 GitHub Releases 供下载（`.vsix` 已被 `.gitignore` 排除，不入库）。

### 发布到 VS Code Marketplace

发布后用户可直接在扩展面板搜索安装，并获得自动更新：

1. 注册 [Azure DevOps](https://dev.azure.com/) 账号，创建 Personal Access Token（范围选择 **Marketplace → Manage**）；
2. 创建发布者 ID：`npx @vscode/vsce create-publisher <publisher-id>`；
3. 将 `package.json` 中的 `publisher` 改为该 ID；
4. 登录并发布：

```bash
npx vsce login <publisher-id>
npx vsce publish        # 自动按版本号发布
```

### 发布到 Open VSX（可选）

VSCodium、Cursor 等编辑器使用 [Open VSX](https://open-vsx.org/) 市场：注册账号获取 token 后执行 `npx ovsx publish`。

## 文件关联

`.stanza` 扩展名自动识别。其他文件名可在 VS Code 设置中关联：

```json
"files.associations": {
  "*.todo": "stanza",
  "tasks.txt": "stanza"
}
```

## 自定义高亮颜色

各元素使用标准 TextMate scope，可在 `settings.json` 中覆盖颜色：

```json
"editor.tokenColorCustomizations": {
  "textMateRules": [
    {
      "scope": "entity.name.tag.stanza",
      "settings": { "foreground": "#E06C75" }
    },
    {
      "scope": "entity.name.type.project.stanza",
      "settings": { "foreground": "#61AFEF" }
    }
  ]
}
```

完整 scope 列表：

| 元素 | Scope |
| ---- | ---- |
| 区块标题（整行） | `markup.heading.state-block.stanza` |
| 状态名 | `entity.name.section.state-name.stanza` |
| 优先级 | `storage.modifier.priority.stanza` |
| 截止日期 | `constant.numeric.date.stanza` |
| 项目 | `entity.name.type.project.stanza` |
| 标签 | `entity.name.tag.stanza` |
| 备注续行 | `comment.block.notes.stanza` |
| 时间戳行（整行） | `meta.timestamp-line.stanza`（同时带备注续行 scope，保持弱化显示） |
| 时间戳关键字 | `keyword.other.timestamp.stanza` |

## 示例

`examples/sample.stanza` 覆盖了规范中的主要边界情况（大小写标题、拼写错误的标题、`C++` 误判、`#1` 非标签、备注内空行、时间戳行及其误判等），可直接在 VS Code 中打开验证高亮效果。

```stanza
# DOING

(A) 2026-08-07 完成登录模块的单元测试 +Apollo #紧急
    2026-08-05 创建
    先跑通现有测试用例
    再补充边界情况

    测试数据在共享盘的 testdata 目录

(B) 2026-08-07 预约周五下午的牙医 +生活
    2026-08-06 创建
    记得带医保卡

# WAIT

2026-08-05 等设计组回复新版图标 +Apollo
    2026-08-04 创建
    上周已提需求，预计本周五交付

# DONE

2026-08-05 修复列表页滚动卡顿的问题 +Apollo
    2026-08-04 创建
    2026-08-06 已合入主干，随 2.3 版本发布
    2026-08-06 完成

# DELETE

调研第三方推送服务 +Apollo
    2026-08-01 创建
    报价超出预算，改用自建方案
```

## 相关链接

- Stanza RFC 1.1.0 规范全文：同仓库 `stanza-rfc/Stanza-RFC-1.1.0.md`
- [TODO.txt](http://todotxt.org/)（Stanza 的灵感来源）

## License

MIT
