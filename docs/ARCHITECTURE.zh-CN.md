# SourceGit 项目架构与代码分析（中文文档）

> 基于对仓库源码的完整分析整理，版本基线：`2026.19`（commit `c2dd8835`）。
> 文末附录记录了本次在本仓库中新增的两项 AI 功能改进（默认模型记忆、模型关键字筛选）。

---

## 目录

1. [项目概述](#1-项目概述)
2. [技术栈与构建体系](#2-技术栈与构建体系)
3. [目录结构总览](#3-目录结构总览)
4. [应用启动与生命周期](#4-应用启动与生命周期)
5. [主窗口架构：Launcher / Workspace / Repository](#5-主窗口架构launcher--workspace--repository)
6. [核心分层详解](#6-核心分层详解)
   - 6.1 [Models：领域模型与配置持久化](#61-models领域模型与配置持久化)
   - 6.2 [Commands：Git 命令封装层](#62-commands-git-命令封装层)
   - 6.3 [Native：平台后端](#63-native平台后端)
   - 6.4 [ViewModels / Views：MVVM、主题与本地化](#64-viewmodels--views-mvvm主题与本地化)
7. [关键功能模块实现](#7-关键功能模块实现)
8. [AI 子系统（重点）](#8-ai-子系统重点)
9. [配置文件与数据存储](#9-配置文件与数据存储)
10. [构建、发布与 CI](#10-构建发布与-ci)
11. [国际化与翻译机制](#11-国际化与翻译机制)
12. [附录：本次功能增强说明](#12-附录本次功能增强说明)

---

## 1. 项目概述

**SourceGit** 是一个开源、免费的跨平台 Git 图形化客户端（MIT 协议），支持 Windows / macOS / Linux。核心特性：

- 基于 **Avalonia 11** 的自绘 UI：亮/暗双主题、完全自定义主题覆盖（JSON）、可视化提交图（DAG）
- 覆盖 Git 日常与高级操作：Clone/Fetch/Pull/Push、Merge/Rebase/Reset/Revert/Cherry-pick、Amend/Reword/Squash、交互式 rebase、分支/远程/标签/储藏/子模块/worktree/Bisect/GitFlow、LFS 全套、补丁生成与应用、Blame、文件历史、归档等
- 内置 AI 助手（OpenAI 兼容协议 + Azure OpenAI），可生成约定式提交信息
- 程序自身可复用为 git 的 `core.editor`、rebase todo/message 编辑器与 SSH_ASKPASS
- 单实例运行（命名管道 IPC），支持从文件管理器直接打开仓库
- 16 种界面语言；集成外部 diff/merge 工具、终端、自定义动作（Custom Actions）

---

## 2. 技术栈与构建体系

| 项 | 说明 |
|---|---|
| 运行时 | .NET 10（`global.json` 指定 SDK 10.0.0，`rollForward: latestMajor`） |
| UI 框架 | Avalonia 11.3.20（Fluent 主题），`AvaloniaUseCompiledBindingsByDefault=true`（全量编译绑定） |
| MVVM | CommunityToolkit.Mvvm 8.4.2（`ObservableObject` / `ObservableValidator`） |
| AI SDK | OpenAI 2.10.0 + Azure.AI.OpenAI 2.9.0-beta.1 |
| 编辑器 | AvaloniaEdit —— 以 **源码子模块** 方式内嵌于 `depends/AvaloniaEdit`（而非 NuGet 包） |
| 图像解码 | Pfim / StbImageSharp / BitMiracle.LibTiff.NET（用于图片 diff、LFS 图片） |
| 版本号 | 根目录 `VERSION` 文件，格式如 `2026.19`，csproj 读取后作为程序集版本 |
| 发布形态 | Release 下 `PublishAot=true` + `PublishTrimmed` + `TrimMode=link`（Native AOT），可用 `-p:DisableAOT=true` 关闭 |

解决方案文件为 `SourceGit.slnx`（新版 XML 格式解决方案），包含两个项目：

- `src/SourceGit.csproj` —— 主程序
- `depends/AvaloniaEdit/src/AvaloniaEdit.TextMate/AvaloniaEdit.TextMate.csproj` —— 内嵌编辑器（ProjectReference 引用）

手动构建命令：

```bash
dotnet publish -c Release -r <rid> src/SourceGit.csproj
# rid 支持：win-x64 / win-arm64 / linux-x64 / linux-arm64 / osx-x64 / osx-arm64
```

---

## 3. 目录结构总览

```
sourcegit/
├── SourceGit.slnx              # 解决方案（XML 格式）
├── global.json                 # .NET SDK 版本约束
├── VERSION                     # 版本号文件
├── TRANSLATION.md              # 各语言翻译完成度（CI 生成）
├── translate_helper.py         # 翻译辅助脚本（比对 locale 键）
├── src/                        # 主程序源码
│   ├── AI/                     # AI 集成层（5 个文件）
│   ├── Commands/               # Git 命令封装层（88 个文件）
│   ├── Converters/             # Avalonia 值转换器（10 个）
│   ├── Models/                 # 领域模型 + 配置持久化（79 个）
│   ├── Native/                 # 平台后端（OS 门面 + Windows/macOS/Linux 实现）
│   ├── Resources/              # 主题/样式/图标/字体/语法/多语言
│   ├── ViewModels/             # MVVM 的 VM 层（133 个）
│   ├── Views/                  # 视图层（178 个 .cs + 144 个 .axaml）
│   ├── App.axaml(.cs)          # 入口（Main 就在这里，无独立 Program.cs）
│   ├── App.Commands.cs         # 全局命令（快捷键绑定）
│   ├── App.JsonCodeGen.cs      # System.Text.Json 源生成上下文（AOT 友好）
│   └── SourceGit.csproj
├── depends/AvaloniaEdit/       # AvaloniaEdit 源码子模块
├── build/
│   ├── scripts/                # 打包脚本（win/linux/mac）+ localization-check.js
│   └── resources/              # .desktop、deb/rpm spec、App.plist 等打包资源
├── tools/setsid-macos/         # macOS 用的 setsid 小工具（C 源码，随 mac 包编译）
└── .github/workflows/          # build.yml、localization-check.yml 等 CI
```

各目录规模：`Commands` 88 个命令类、`Models` 79 个、`ViewModels` 133 个、`Views` 144 对 axaml+code-behind 另有 34 个纯代码自定义控件。代码风格特点：

- MVVM 命名约定：`Views/Foo.axaml` + `Views/Foo.axaml.cs`（视图），`ViewModels/Foo.cs`（视图模型）
- 大量**纯代码自绘控件**（如提交图 `Views/CommitGraph.cs`、diff 小地图、图表 `Views/Chart.cs`、头像 `Views/Avatar.cs`）
- 无单元测试项目；质量主要靠 CI 编译 + 社区反馈

---

## 4. 应用启动与生命周期

入口位于 `src/App.axaml.cs` 的 `App.Main()`（没有独立 Program.cs）。流程：

1. `Native.OS.SetupBasicDirectories()`：确定配置/缓存目录（支持便携模式，见第 9 节），注册全局异常日志。
2. **特殊启动模式检测**（通过 main args）——这些模式让本程序可以被 git 直接调用：
   - `--rebase-todo-editor <file>`：作为 `git rebase -i` 的 sequence.editor，读取仓库内 `sourcegit.interactive_rebase` 作业文件，生成/编辑 todo 后退出（无 UI）
   - `--rebase-message-editor <file>`：作为 rebase 的 COMMIT_EDITMSG 编辑器
   - `--core-editor <file>`：独立提交信息编辑器窗口（`Views/CommitMessageEditor`），配合注入的 `core.editor` 配置
   - 环境变量 `SOURCEGIT_LAUNCH_AS_ASKPASS=TRUE`：作为 `SSH_ASKPASS` 弹出密码窗口（`Views/Askpass`）
3. 正常模式 `BuildAvaloniaApp().StartWithClassicDesktopLifetime(args)`：设置默认字体、内嵌字体集合、`Native.OS.SetupApp`。
4. `OnFrameworkInitializationCompleted` 再按 desktop.Args 分流为独立窗口模式：
   - `--history <path>`：独立文件/目录历史窗口
   - `--blame <file>`：独立 Blame 窗口
5. **单实例 IPC**：`Models/IpcChannel.cs` 基于命名管道 + 锁文件。第二个实例把仓库路径发给首实例后退出，实现"文件关联打开仓库"。
6. 初始化 `Native.OS.SetupExternalTools()`、`Models.AvatarManager`（GitHub 风格头像异步下载）、`ViewModels.Launcher`（主窗口），可选启动时更新检查（数据源是 GitHub Pages 上的 `version.json`，非 GitHub API；可用编译开关 `DISABLE_UPDATE_DETECTION` 禁用）。

每个 git 子进程由 `Commands/Command.cs` 统一注入环境：`SSH_ASKPASS=<自身exe>`、`SOURCEGIT_LAUNCH_AS_ASKPASS=TRUE`、`GIT_SSH_COMMAND`，以及可选 `-c core.editor="<自身exe>" --core-editor`。**这就是"程序自身作为 git 编辑器/askpass"机制的实现方式。**

---

## 5. 主窗口架构：Launcher / Workspace / Repository

```
Launcher (主窗口 VM, src/ViewModels/Launcher.cs)
├── AvaloniaList<LauncherPage> Pages        # 标签页
│   ├── RepositoryNode Node                 # 左侧仓库树节点
│   ├── object Data                         # Welcome(欢迎页单例) 或 Repository(仓库 VM)
│   ├── DirtyState                          # 标题脏标记
│   ├── Popup                               # 当前模态弹窗（见下）
│   └── Notifications                       # 页面顶部通知条
├── ActiveWorkspace                         # 多工作区（src/ViewModels/Workspace.cs）
└── CommandPalette                          # 当前打开的命令面板（object 多态）
```

### Repository（仓库 VM，`src/ViewModels/Repository.cs`，约 2000 行）

- 实现 `Models.IRepository`；三大视图索引：0=Histories（历史）、1=WorkingCopy（工作副本）、2=StashesPage
- 持有分支/远程/标签/子模块/worktree 集合与过滤树、`Models.Watcher`（FileSystemWatcher，工作副本与 .git 分开监听，100ms 去抖触发 `RefreshAll()`）、5 秒轮询的自动 Fetch 定时器（可配置）
- 写操作执行期间通过 `LockWatcher()` 挂起文件监听，避免刷新风暴

### Popup 机制（弹窗/对话框）

`src/ViewModels/Popup.cs`：所有模态操作（Checkout、Merge、Push…）的 VM 继承 `Popup : ObservableValidator`（带 DataAnnotations 校验、`Check()/Sure()/Terminate()` 等）。弹窗渲染在 `Launcher.axaml` 的**应用内悬浮层**而非 OS 对话框；只有 `Confirm`、`SelfUpdate` 等少数用 `ShowDialog`。VM→视图映射在 `Views/PopupDataTemplates.cs`。

### 命令面板（Command Palette）

`ViewModels/ICommandPalette.cs` 是基类，`Open()/Close()` 把自身挂到 `Launcher.CommandPalette`；13+ 个实现（仓库/检出/合并/文件历史/打开文件…），由 `Ctrl+P` 等快捷键触发，视图模板在 `Views/CommandPaletteDataTemplates.cs`。

---

## 6. 核心分层详解

### 6.1 Models：领域模型与配置持久化

核心模型（`src/Models/`）：

| 类 | 说明 |
|---|---|
| `Commit.cs` | SHA、作者/提交者、时间戳、Subject、Parents、Decorators（分支/Tag 装饰）及解析；图渲染用几何字段 |
| `Branch.cs` / `Tag.cs` / `Remote.cs` / `Stash.cs` / `Submodule.cs` / `Worktree.cs` | 引用类模型 |
| `Change.cs` | 工作区变更（状态：Added/Modified/Deleted/TypeChanged/Renamed/Copied/Untracked/Conflicted） |
| `CommitGraph.cs` | **提交图拓扑与几何计算**：`Generate(commits, ...)` 产出 Paths/Links/Dots（Avalonia 点位），`SetPens/SetDefaultPens` 支持主题自定义连线颜色 |
| `Watcher.cs` | 文件监听与去抖 |
| `IpcChannel.cs` | 单实例命名管道 |
| `Locales.cs` | 支持的语言列表 |
| `ShellOrTerminal.cs` / `ExternalMerger.cs` / `ExternalTool.cs` | 各平台支持清单 |
| `TemplateEngine.cs` | 提交模板引擎 |
| `RepositorySettings.cs` / `RepositoryUIStates.cs` | 见下 |

**配置持久化三件套**（均为 System.Text.Json，且通过 `App.JsonCodeGen.cs` 的 `JsonCodeGen : JsonSerializerContext` 源生成，兼容 AOT）：

| 文件 | 位置 | 内容 |
|---|---|---|
| `preference.json` | 全局配置目录 | `ViewModels.Preferences`：语言、主题、字体、AI 服务列表（`OpenAIServices`）、仓库树、工作区等。保存时先写 `preference_tmp.json` 再原子替换 |
| `sourcegit.settings` | `<git公共目录>/` | `Models.RepositorySettings`：每仓库的 DefaultRemote、PreferredMergeMode、PreferredOpenAIService、CommitTemplates、CustomActions 等。带 MD5 哈希脏检查，内容不变不写盘 |
| `sourcegit.uistates` | `<git目录>/` | `Models.RepositoryUIStates`：每仓库 UI 状态（历史过滤器、折叠状态、最近提交消息等），也检测进行中状态（MERGE_HEAD/CHERRY_PICK_HEAD 等） |

### 6.2 Commands：Git 命令封装层

基类 `src/Commands/Command.cs` 职责：

- `CreateGitStartInfo`：统一拼 `--no-pager -c core.quotepath=off -c credential.helper=...`，按需注入编辑器/SSH_ASKPASS/GIT_SSH_COMMAND；Linux 强制 `LANG=C LC_ALL=C` 保证输出可解析
- `ExecAsync()`：`Process` 启动 git，流式读 stdout/stderr，支持取消（配合 setsid 终止进程树）；非零退出码 → `Models.Notification.Send`（页面顶部通知条）
- `ReadToEnd()/ReadToEndAsync()`：一次性查询

命名约定：**`Query*` = 只读查询**（如 `QueryCommits`、`QueryLocalChanges`、`QueryBranches`），**动词类 = 写操作**（`Checkout`、`Commit`、`Merge`、`Fetch`/`Push`/`Pull`），通常 `ExecAsync()` 返回 bool 或 `GetResultAsync()` 返回模型。

- 输出解析内嵌于各命令类（无独立 parsers 目录）。典型：`QueryCommits.cs` 用自定义 log format（`%H%x00%P%x00%D...`，NUL 分隔）再逐段解析
- `Commands/Commit.cs`：消息写入临时文件，`commit --file=<tmp> [--amend ...]`
- AI 专用：`Commands/GetFileChangeForAI.cs` —— 按变更状态（新增/修改/重命名等）拼装文件 diff 文本供模型阅读

### 6.3 Native：平台后端

- `Native/OS.cs`：静态门面 `OS`，静态构造按 OS 选择 `IBackend` 实现。公开 GitExecutable、GitVersion、CredentialHelper、Shell/Terminal、外部合并工具配置、`BasicDirectories{ConfigDir,CacheDir}`、`UseSystemWindowFrame`（Linux）、`TerminateProcess` 等
- `Windows.cs`：注册表/PATH 找 git、PowerShell 发现、Win32 P/Invoke（无边框窗口修复、深色标题栏）
- `Linux.cs`：XDG 目录、setsid 支持、终端探测（GNOME Terminal/Konsole/…）
- `MacOS.cs`：`PATH` 自定义文件（`ConfigDir/PATH`）、`tools/setsid-macos` 编译产物、OpenTerminal

**配置目录规则**：Windows/macOS 为 `%APPDATA%/SourceGit` 风格目录；Linux 走 XDG（`$XDG_CONFIG_HOME/SourceGit`，含 `~/.sourcegit` 迁移逻辑）；三平台都支持**便携模式**——exe 旁存在 `data/` 目录即使用之。

### 6.4 ViewModels / Views：MVVM、主题与本地化

- **MVVM**：VM 继承 `ObservableObject`（弹窗类继承 `ObservableValidator`），多为手写属性 + `SetProperty`；编译绑定默认开启
- **主题**：`Resources/Themes.axaml` 用 `ThemeDictionaries` 定义 Light/Dark 两套 `Color.*` 再映射为 `Brush.*`；`Resources/Styles.axaml`（1500+ 行）承载全局控件样式。`App.SetTheme(theme, overridesFile)` 可加载外部 JSON 主题覆盖（`Models.ThemeOverrides`：基础色/图连线色/线宽），运行时换肤；字体同样可被覆盖（`Fonts.Default` / `Fonts.Monospace` 资源）
- **本地化**：`Resources/Locales/<lang>.axaml` 是 `x:String` 键值 ResourceDictionary（键名 `Text.*`，支持 `{0}` 占位）；`App.SetLocale` 动态切换 MergedDictionaries，代码取词用静态 `App.Text(key, args)`，axaml 用 `{DynamicResource Text.*}`。共 16 种语言
- **窗口体系**：主窗口 `Views/Launcher`；窗口基类 `Views/ChromelessWindow.cs`（自绘标题栏，Linux 可退回系统边框）；普通模态走 LauncherPage 的 Popup 悬浮层

---

## 7. 关键功能模块实现

| 模块 | 实现要点 |
|---|---|
| 提交图 | `Models/CommitGraph.cs` 计算拓扑，`Views/CommitGraph.cs` 自绘（OnRender 画 Paths/Links/Dots）；`Views/Histories.axaml` 用 DataGrid 第一列嵌自绘控件；支持 bisect/高亮模式、双列布局、相对时间 |
| Diff | `ViewModels/DiffContext.cs` / `TextDiffContext.cs`（Unified/SideBySide、词内高亮）；`Views/TextDiffView.axaml`（1500+ 行 + 自绘 minimap）；图片 diff（Pfim/StbImage/LibTiff 解码）、二进制 Hex 查看、LFS 图片对比 |
| 工作副本/提交 | `ViewModels/WorkingCopy.cs`：Unstaged/Staged 两个 ChangeCollection；提交流程（空提交确认 → 锁 watcher → 记录最近消息 → `Commands.Commit` → 可选弹 Push）；消息框 `Views/CommitMessageToolBox.axaml` 含 50 字符参考线、约定式提交类型补全（`ConventionalCommitMessageBuilder`）、提交模板、最近消息 |
| 交互式 rebase | 自绘 todo 编辑器；程序自身作为 `sequence.editor` / COMMIT_EDITMSG 编辑器被 git 回调 |
| Bisect / GitFlow / LFS / 子模块 / Worktree | 各有独立命令类与弹窗；LFS 覆盖 track/pull/push/prune/locks |
| 自定义动作 | 全局（Preferences）与仓库级（sourcegit.settings）两级；可配置输入控件、作用域（仓库/提交/分支/标签/远程/文件） |
| 统计 | `Views/Chart.cs` 自绘图表 |
| SSH 密钥 | `Views/SSHKeyGenerator` + `Models/SSHKeyPair` |

---

## 8. AI 子系统（重点）

代码位置：`src/AI/` + `src/ViewModels/AIAssistant.cs` + `src/Views/AIAssistant.axaml(.cs)` + 两处入口视图。

### 8.1 组成

| 文件 | 职责 |
|---|---|
| `AI/Service.cs` | 一个"平台接口"（服务）配置：Name/Server/ApiKey/ReadApiKeyFromEnv/Model/AutoFetchAvailableModels/ReasoningEffortLevel/AdditionalPrompt/ExtraHeaders。负责创建 `OpenAIClient`（URL 含 `openai.azure.com/` 时走 `AzureOpenAIClient`）与 `ChatClient`；`FetchAvailableModels()` 拉取模型列表 |
| `AI/Agent.cs` | 一次"生成提交信息"的对话循环：构造约定式提交提示词（含分支、变更文件清单），支持 tool-calls 循环、`reasoning_content` 透传、token 用量展示；`ChatFinishReason` 分支处理（Stop/Length/ToolCalls/ContentFilter） |
| `AI/ChatTools.cs` | 定义 function-tool `GetDetailChangesInFile`（模型可主动请求某文件的详细 diff）；`Commands/GetFileChangeForAI.cs` 提供数据 |
| `AI/Options.cs` | Reasoning Effort 等级常量（unspecified/none/minimal/low/medium/high） |
| `AI/ExtraHeadersPolicy.cs` | 将用户配置的附加 HTTP 头注入每次请求（DelegatingHandler 风格 pipeline policy） |
| `ViewModels/AIAssistant.cs` | AI 助手窗口 VM：序列化变更清单、驱动 Agent、流式更新文本、提取最终响应、`Use()` 写回提交消息 |
| `Views/AIAssistant.axaml` | 助手窗口：Markdown 语法的响应视图（AvaloniaEdit + TextMate）、模型选择、Use/RE-GENERATE 按钮 |

### 8.2 数据流

```
用户点击"使用AI助手生成提交信息"（CommitMessageToolBox / 右键已暂存文件）
  → repo.GetPreferredOpenAIServices()          // Repository.cs：仓库偏好服务优先，否则返回全部
  → （多服务且未指定偏好时弹菜单选择服务）
  → DoOpenAIAssistant(repo, service, changes)  // 记住所选服务（本次改进，见附录）
  → new ViewModels.AIAssistant(repo, service, changes)
  → AIAssistant 窗口 OnOpened → vm.GenAsync()
      → AI.Agent.GenerateCommitMessageAsync(...)
          → ChatClient.CompleteChatAsync（可多轮 tool-calls 读取文件 diff）
          → onUpdate 流式回调 → 窗口实时显示
  → 用户点"应用" → vm.Use(response) → repo.SetCommitMessage(...)
```

### 8.3 服务与模型的存储语义

- 服务列表存于全局 `preference.json` 的 `OpenAIServices` 数组（`AI.Service` 可 JSON 序列化；`AvailableModels` 标 `[JsonIgnore]` 不落盘）
- 每仓库可选"首选服务"：`sourcegit.settings` 的 `PreferredOpenAIService`（UI 在仓库设置里，`---` 表示未指定）
- `Service.Model` 即该服务的**默认模型**，持久化保存；`AutoFetchAvailableModels=true` 时启动与偏好关闭时会后台拉取模型列表（`Preferences.UpdateAvailableAIModels()`）

---

## 9. 配置文件与数据存储

```
<配置目录>/                          # 见 6.3 的目录规则
├── preference.json                  # 全局偏好（含 OpenAIServices）
├── bookmarks.json(如启用)            # 书签
├── themes/                          # 外部主题覆盖
├── avatars/                         # 头像缓存
└── logs/                            # 异常日志
<仓库>/.git/
├── sourcegit.settings               # 仓库级设置（含 PreferredOpenAIService）
├── sourcegit.uistates               # 仓库级 UI 状态
└── sourcegit.interactive_rebase     # 交互式 rebase 作业描述（临时）
```

关键实现细节：

- 所有 JSON 序列化走 `App.JsonCodeGen.cs` 源生成上下文（AOT 必需）
- `Preferences.Save()`：写临时文件 → `File.Move` 原子替换
- `RepositorySettings.Save()`：MD5 对比内容哈希，无变化不写盘

---

## 10. 构建、发布与 CI

- `build/scripts/package.win.ps1`：删除 pdb → Compress-Archive
- `build/scripts/package.linux.sh`：构建 deb / rpm / AppImage（处理多版本 ICU）
- `build/scripts/package.osx-app.sh`：打 .app bundle，并用 clang 编译 `tools/setsid-macos/setsid.c` 打入包中（用于终止 git 进程树）
- `build/scripts/localization-check.js` + CI `localization-check.yml`：校验各语言缺失键，生成 `TRANSLATION.md`
- `build.yml`：多平台多 RID 矩阵构建发布
- Native AOT：csproj 中 Release 配置 `PublishAot=true`；`DISABLE_UPDATE_DETECTION` 编译开关可去掉更新检查

---

## 11. 国际化与翻译机制

- 键命名 `Text.*`，en_US 为基准；其余 15 种语言：de_DE / el_GR / fr_FR / he_IL / id_ID / it_IT / ja_JP / ko_KR / pt_BR / ru_RU / ta_IN / uk_UA / zh_CN / zh_TW / es_ES
- 取词入口：`App.Text(key, args)`（内部 `FindResource("Text." + key)`），axaml 中 `{DynamicResource Text.xxx}`
- `translate_helper.py` 辅助比对缺失键；CI 输出 `TRANSLATION.md` 徽章
- **注意**：只有当前语言字典被合并加载（`App.SetLocale`），因此新增键需要至少加入 `en_US.axaml`（否则其他语言取不到词），推荐同时补 zh_CN/zh_TW 等

---

## 12. 附录：本次功能增强说明

> 本节记录在本仓库中实现的两个功能改进（针对需求②与③），均已编译验证通过（Debug 与 Release/AOT 均 0 警告 0 错误）。

### 12.1 需求回顾

1. **偏好设置 → AI 中配置多个平台接口时，希望能设置默认模型，或点击"使用AI助手生成提交信息"时默认使用上一次的选择，不用每次都选。**
2. **AI 助手中模型下拉框在模型几十个时无法快速定位，需要支持关键字筛选。**

### 12.2 方案总览（按"组合拳"设计）

| 痛点 | 改进 |
|---|---|
| 多服务时每次点击都弹菜单选平台 | **全局默认平台**：偏好设置 AI 页顶部新增"默认平台"选择器，选定后所有仓库（含新仓库）直接使用该平台，零点击。另有**逐仓库记忆**：未指定全局默认时，某仓库选择过一次平台即被记住（写回 `sourcegit.settings` 的 `PreferredOpenAIService`）。解析优先级：仓库级偏好 > 全局默认 > 弹菜单 |
| 默认模型无法显式指定（自动拉取时旧 UI 的模型输入框被禁用） | 偏好设置 AI 页的"模型"字段改为**可筛选下拉框**：自动拉取开启时可直接搜索并选定默认模型；关闭时可自由输入。所选模型随 `preference.json` 持久化 |
| 上次使用的模型重启后丢失 | AI 助手窗口中切换的模型在窗口关闭时自动保存（`Preferences.Save()`），下次打开默认选中上次的模型 |
| 模型列表几十项难以定位 | 新增 `FilterableComboBox` 控件：输入即过滤（不区分大小写的包含匹配）、回车确认、↑/↓ 在过滤结果中循环选择、Esc 取消 |

### 12.3 具体改动清单

**新增文件**

- `src/Views/FilterableComboBox.cs` —— 通用"可筛选下拉框"控件
  - 继承 Avalonia `ComboBox` 并启用 `IsEditable`（复用 Fluent 原生外观与 `PART_EditableTextBox` 模板部件）
  - 监听内部文本框输入，对 `ItemsSource` 应用不区分大小写的包含过滤（`SetCurrentValue` 方式替换视图集合，不破坏外部绑定，模型列表异步刷新后自动重新过滤）
  - 提交语义（新 DirectProperty `SelectedModel` 双向绑定到 VM）：精确匹配列表项的文本按"选择"提交；与任何模型都不构成包含关系的文本视为自定义 ID（如私有部署），失焦也提交；部分是某模型子串的输入视为未完成过滤，失焦一律还原；回车显式提交当前文本；Esc 取消还原；纯输入中的中间态（含 null）绝不写回模型
  - 键盘：↑/↓ 在过滤结果中循环并把焦点拉回文本框；Enter 提交（列表项或手输的自定义模型 ID）；Esc 取消并还原
  - `SelectionCommitted` 事件供视图层在"用户确认切换模型"时触发重新生成

**修改文件**

| 文件 | 改动 |
|---|---|
| `src/Views/AIAssistant.axaml` | 模型选择由普通 `ComboBox` 换为 `FilterableComboBox`（`SelectionCommitted="OnModelCommitted"`） |
| `src/Views/AIAssistant.axaml.cs` | ① 打开时若模型为空则自动取列表第一个，避免首次空跑；② `OnModelCommitted`：用户确认切换模型后自动重新生成；③ `OnClosing` 时 `Preferences.Instance.Save()` 持久化"上次使用的模型" |
| `src/ViewModels/AIAssistant.cs` | `CurrentModel` setter 过滤 null（筛选过程中的临时取消选择不得清空已保存模型）；订阅 `Service.PropertyChanged` 在模型列表异步到位后刷新 UI |
| `src/AI/Service.cs` | ① `Model` setter 过滤 null；② `FetchAvailableModels()` 改为**仅在模型为空时**自动取第一个（不再覆盖用户显式设定的模型）；③ 模型列表更新后经 `Dispatcher.UIThread.Post` 发通知（修复原先后台线程直接改 UI 绑定属性的隐患） |
| `src/ViewModels/Preferences.cs` | 新增全局 `DefaultOpenAIService`（持久化到 preference.json）与 `OpenAIServiceNames`（`---` + 各服务名，供"默认平台"下拉使用）；监听服务列表增删与名称改名，自动校验默认平台有效性（失效回落 `---`） |
| `src/ViewModels/Repository.cs` | `GetPreferredOpenAIServices()` 解析优先级改为：仓库级偏好 → 全局默认平台 → 返回全部（弹菜单兜底） |
| `src/Views/Preferences.axaml` | ① AI 页顶部新增"默认平台"行（下拉 + 说明 tooltip）；② AI 页"模型"字段由（自动拉取时被禁用的）文本框改为 `FilterableComboBox`，自动拉取开启时也可搜索选择 |
| `src/Views/CommitMessageToolBox.axaml.cs` | `DoOpenAIAssistant`：将所用服务写回 `repo.Settings.PreferredOpenAIService` 并保存（未设全局默认时逐仓库记忆，下次免选） |
| `src/Views/WorkingCopy.axaml.cs` | 同上（右键菜单入口），顺带修正原参数名拼写 `serivce` → `service` |
| `src/Resources/Locales/{en_US,zh_CN,zh_TW}.axaml` | 新增 `Text.FilterableComboBox.FilterHint`（"输入关键字筛选…"）与 `Text.Preferences.AI.DefaultService(.Tip)`（"默认平台"及说明） |

### 12.4 行为变化说明（升级后）

- 在 偏好设置 → AI → **默认平台** 选定平台后，所有仓库（含新添加的仓库）点"使用AI助手生成提交信息"直接进入该平台，**一次都不用再选**
- 未设全局默认时，首次在某仓库使用后该平台即成为该仓库的默认（写回仓库级 `PreferredOpenAIService`）；想换可在 仓库设置 → 首选 OpenAI 服务 中修改。仓库级设置优先于全局默认
- AI 助手里选择过的模型自动成为该平台接口的默认模型并持久化；偏好设置里也可显式指定
- 自动拉取开启时，若某模型 ID 被服务方下线，将保留用户设定并在生成时报错（不再静默切换为列表第一个），需手动重新选择

### 12.5 自查与修复记录

交付前对全部改动做了一轮代码审查，发现并修复以下问题：

| # | 严重度 | 问题 | 修复 |
|---|---|---|---|
| 1 | 高 | FilterableComboBox：打字打开下拉时基类把焦点移给列表项 → 编辑框失焦触发提交 → 首个字符被还原 | 下拉开着期间跳过失焦提交（提交统一在 `DropDownClosed` 处理） |
| 2 | 高 | ↑/↓ 导航时基类把选中项全名回填编辑框，被当成新过滤词重新过滤，列表塌缩为 1 项 | 程序化导航期间挂 `_syncingText` 旗标抑制文本变更处理 |
| 3 | 高 | `DefaultOpenAIService` 绑定普通 ComboBox，`OpenAIServiceNames` 每次重建导致选中项引用失配 → TwoWay 回写 null **静默擦除全局默认平台** | 名单改为稳定 `AvaloniaList` 原地增删（引用不变）；setter 过滤 null |
| 4 | 中 | AIAssistant VM 向长生命周期 `AI.Service` 挂 PropertyChanged 委托，窗口关闭不摘除（每次打开泄漏一个订阅） | 命名方法订阅，窗口 `OnClosing` 调 `vm.Release()` 摘除 |
| 5 | 中 | 使用过的平台写回仓库偏好后，日后修改全局默认平台时旧仓库仍钉死在旧服务 | 仅当**无全局默认**且仓库未记忆时才写回；全局默认永远生效 |
| 6 | 中 | 模型列表异步到位会重置 `SelectedItem`，用户先前的选择在提交时被降级为"手输文本"路径而可能丢弃 | 提交时按当前文本在活动列表反查匹配项，命中即按"列表选择"路径提交 |
| 7 | 低 | 水印文案在构造函数取 `App.Text` 快照，运行时切语言不更新 | 改为 XAML 中 `PlaceholderText="{DynamicResource Text.FilterableComboBox.FilterHint}"` |
| 8 | 低 | （防御）`Service.Model`/`CurrentModel` setter 接受 null 会擦除已存模型；`GetPreferredOpenAIServices` 对重名服务取首个 | 已在实现中做 null 防护与去重 |

第二轮审查追加发现：

| # | 严重度 | 问题 | 修复 |
|---|---|---|---|
| 9 | 高 | **空格键误关下拉**：TextBox 对 Space 不标记 handled，按键冒泡进基类 `IsDropDownOpen && (Enter‖Space)` 分支 → 选中焦点项并关闭下拉（过滤词含空格时必现，如 "gpt 4o"） | FilterableComboBox 重写 `OnKeyDown`：下拉开着且焦点在编辑框时吞掉 Space（不传给基类），空格正常进入文本 |
| 10 | 高 | **半截过滤词被误保存为模型**：`CommitTextOnLostFocus=true` 时在偏好页输入 `gpt` 再点击别处，会把 `gpt` 当作模型 ID 提交并持久化 | 移除 `CommitTextOnLostFocus` 属性与"失焦提交自由文本"语义；失焦/关闭时只有**精确匹配列表项**的文本才提交，其余一律还原。手输自定义模型 ID 需按回车显式提交 |
| 11 | 中 | **模型拉取竞态覆盖用户选择**：`FetchAvailableModels` 的兜底（取第一个模型）经 `Dispatcher.Post` 延迟执行，若用户在拉取期间已自行选好模型，兜底会把选择覆盖掉 | 兜底执行前再次校验 `Model` 为空才填 |
| 12 | 低 | 键位复核：Enter/Esc 由 Tunnel 处理器先于 TextBox/Bubble 截获；F4/Alt+↓ 由基类开合下拉后焦点自动回编辑框；`PART_EditableTextBox` 默认 `AcceptsReturn=false` 不会误换行——均确认无问题 | 无需改动 |

第三轮审查追加发现（聚焦绑定通知链与事件时序）：

| # | 严重度 | 问题 | 修复 |
|---|---|---|---|
| 13 | 中 | **异步兜底选模型后下拉显示不同步**：`AIAssistant.CurrentModel` 是转发属性，但 VM 不转发 `Service.Model` 变更。首次使用时后台拉取完成的兜底赋值不会推给下拉，显示与实际生效模型不一致 | VM 的 `OnServicePropertyChanged` 同时转发 `Model`→`CurrentModel`（已验证无通知死循环：回推时 `SetAndRaise` 值相等短路） |
| 14 | 中 | **逐字输入完整模型 ID 时焦点被抢、打字中断**：基类 `ComboBox.TextChanged` 每次文本变化做精确匹配自动选中，命中即 `TryFocusSelectedItem()` 把键盘焦点移给列表项容器 | 打字路径的 `ApplyFilter` 后检测 `IsKeyboardFocusWithin`，被抢则拉回编辑框并定位光标到末尾 |
| 15 | 低 | **关窗竞态重启生成**：窗口 `OnClosing` 取消生成后，Popup 卸载又触发 `DropDownClosed → 提交 → SelectionCommitted → GenAsync`，用全新 CTS 顶掉刚取消的，白耗一次 API 调用 | 增加 `_closing` 旗标，`OnClosing` 首行置位，`OnModelCommitted` 入口短路 |
| 16 | 中 | **自定义模型 ID 粘贴后点别处丢失**（#10 的矫枉过正）：私有部署/自定义 ID 失焦还原导致"粘贴→切走"输入丢失，回归旧文本框习惯 | 引入 `HasNoMatch` 判据：与列表中任何模型都不构成包含关系的文本只可能是刻意输入的自定义 ID，失焦也提交；是某模型子串的输入才还原。该判据同时覆盖"自动拉取关闭+空列表"的纯文本框场景 |
| 17 | — | （证伪记录）审查中曾疑"切换服务时新旧 `AvailableModels` 同引用致 `_fullItems` 过期"——核实每个 `Service` 实例各自 new 独立 List，绑定换源必然触发变更，**不成立**，未做改动 | 无 |
