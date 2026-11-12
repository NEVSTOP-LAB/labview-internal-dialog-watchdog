# 设计说明

本文记录 LabVIEW Dialog Watchdog 的设计思路、关键决策与实现细节。使用与配置见 [README](../README.md)，
真实验证记录见 [verification.md](verification.md)。

## 1. 目标与非目标

**目标**

- 检测 LabVIEW 进程被模态对话框阻塞的情况（最常见：`Internal Warning` / internal error）。
- 在无人值守时自动确认，让 labview-mcp 等自动化调用继续。
- 只处理自动化场景：用户正在手动操作 LabVIEW 时不打扰。
- 单 exe、托盘常驻、低资源占用；不引入运行时依赖。

**非目标**

- 不修复 LabVIEW 内部状态，不重启 LabVIEW，不处理崩溃转储/Windows 错误报告。
- 不处理其他会话/其他虚拟桌面的窗口。
- 不绕过 Windows UIPI/完整性级别。
- 不替代 labview-mcp 的超时、重连或服务恢复逻辑。

## 2. 总体架构

```text
Program.Main
  ├─ --scan / --watch-seconds          CLI 与自动化测试入口
  └─ TrayContext                       托盘宿主（无主窗口）
       ├─ DialogWatchdogEngine         轮询、状态机、点击决策
       │    ├─ WindowScanner           枚举窗口、识别对话框、找确认按钮
       │    ├─ DialogClicker           分层点击策略
       │    ├─ UserActivityGuard       前台历史 + 系统输入空闲判定
       │    └─ Log                      追加日志 + 1 MB 轮转
       └─ NotifyIcon / ContextMenu     状态、暂停、日志、配置、自启、退出
```

数据流（一次 Tick）：

```text
采样前台 PID（写入历史）
  → EnumWindows 找目标进程窗口
  → 分类：标准 #32770 / 被禁用 owner 的对话框 / 配置类
  → 收文本、判 internal warning、找确认按钮
  → 新对话框：判定是否用户正在操作 → 记录或跳过
  → 值守窗口结束 → 点击（原生按钮 / UIA / Enter）
  → 下一 Tick 验证窗口消失 → 记日志/气泡
```

## 3. 关键决策

### 3.1 为什么不用进程枚举找 LabVIEW

在受限上下文（如 DSH 沙箱启动的进程）里，`Process.GetProcessesByName`、Toolhelp32、WMI 都可能只看到
极少进程，`Process.GetProcesses()` 甚至只返回 13 个；但 `EnumWindows` 仍能看到全部窗口。

因此实现为：

1. `EnumWindows` 枚举顶层窗口 → `GetWindowThreadProcessId` 得到 PID；
2. `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageNameW` 得到 exe 名；
3. 与 `targetProcessNames`（默认 `LabVIEW`）比较。

进程名取不到时，保守回退到窗口类以 `LV` 开头或标题包含 `LabVIEW`，只用于兜底。

### 3.2 如何认定“阻塞对话框”

只看目标进程的**可见、屏幕上**的顶层窗口，并排除主框架、工具面板、托盘/提示类窗口：

- 标准 Win32 对话框：窗口类 `#32770`。由于它不是天然模态，默认还要求满足其一：
  owner 被禁用、文本命中错误语义、或只有一个可见按钮；普通多按钮 OK 框不处理。
- 非标准对话框：owner 非空、带 `WS_DLGFRAME`、owner 被禁用（模态），且窗口在虚拟屏幕内。
  LabVIEW 自绘 `One Button Dialog` 属于这一类（类名 `LVDChild`、标题为空、没有子按钮）。
- 忽略类：`tooltips_class32`、`#32768`、`ComboLBox`、`SysShadow`、IME、OLE/DDE 等。
- 标题命中 `excludeDialogTitlePatterns`（默认保存/覆盖/删除/重命名/退出/停止，中英）直接跳过。
- 屏幕判断使用 `SM_XVIRTUALSCREEN` 等虚拟屏幕范围，避免把 `-32000` 位置的隐藏面板当对话框，
  同时兼容左置/多显示器。

### 3.3 确认按钮识别

- 枚举对话框所有子窗口，优先类名 `Button`、含 `button` 或控件 ID 为 `IDOK` 的控件。
- 按钮文本做**精确匹配**（去 `&`、去尾部省略号），避免“关闭而不保存/Close without saving”命中“关闭/Close”。
- `IDOK` 作为语义兜底，但文本命中危险词（保存/不保存/覆盖/删除/重命名/Save/Delete/Overwrite/Rename）时不点。
- 命中 `excludeButtonTexts`（取消/停止/放弃/No 等）永不点击。
- 找不到可点按钮时：
  - 自绘 `LVDChild` 且显式开启 `enableKeyboardFallback`：按 Enter；
  - 标准对话框且带错误语义：允许默认按钮兜底；
  - 其他情况不处理（宁可漏点，不误点）。

### 3.4 点击策略（分层、越后越保守）

1. `PostMessage(BM_CLICK)`：异步，不阻塞守护线程。
2. 仍不消失：UI Automation `InvokePattern`（覆盖 WinForms 等自绘按钮），再退到
   `SendMessageTimeout(BM_CLICK, 1.5s)`。
3. 仍不消失：向对话框发**已匹配按钮的控件 ID** 的 `WM_COMMAND`，必要时再回车。
4. 自绘无按钮窗口：`SetForegroundWindow` → 发 Enter；仍不消失发 `WM_COMMAND(IDOK)+Enter`。
5. `WM_CLOSE`：默认关闭，只有显式开启 `enableWindowCloseFallback` 才作为最后兜底。

> 真实事故：早期实现把 `WM_CLOSE` 当默认第 3 次兜底，强关 `One Button Dialog` 后 LabVIEW 的 AI gRPC
> 服务卡死、重启未恢复。因此现在默认绝不强关，并把它做成显式 opt-in。

每次点击都检查发送结果；失败单独计数并写 `W` 日志，而不是假装已点击。

### 3.5 值守窗口（clickDelayMs）

对话框出现后先等待 `clickDelayMs`（默认 5000ms）再点击：

- 有人值守：通常在这 5 秒内自己点掉，窗口消失后守护只保留“等待”日志，不会点击；
- 无人值守：窗口结束后才自动点击（自绘框按 Enter）。

等待发生在第一次点击之前，不消耗 `maxClickAttempts`。配置为 0 可立即点击，10000 即 10 秒。
真实测试：无人值守 5.0 秒后 Enter，MCP 调用 `runMs=5266`；模拟有人值守 92ms 内手动按掉，
守护 0 次自动点击，调用 `runMs=285`。

### 3.6 用户操作闸门（userActivityGuard）

没有可靠的“当前调用来自 MCP”信号，因此用启发式：

- 每个 Tick 采样一次前台窗口 PID，写入约 8 秒历史（`userActivityWindowMs` 默认 3 秒）；
- 新对话框出现时，只看**对话框出现之前**的历史：若目标进程曾在前台，且
  `GetLastInputInfo` 显示最近 `userActivityIdleMs`（默认 2 秒）内有输入，判定用户正在手动操作；
- 判定成立则整个对话框生命周期都不点击，日志记录“跳过自动点击”，托盘累计“用户跳过”。

只看出现前的历史，是因为对话框自己会抢前台；采样放在扫描之后，记录的是“下一轮看到对话框之前”的前台窗口。
这是启发式，边界（手动时 MCP 也触发会跳过、手动时不在前台可能被点）已写入 README；需要绝对控制时用托盘暂停。

### 3.7 状态机与并发

每个窗口句柄一个状态：连续看到次数、尝试次数、下次尝试时间、验证时间、是否用户处理、是否已放弃。

- `minSeenCount`（默认 2）避免瞬时窗口误触。
- 新状态按 PID + 窗口类 + 标题 + 文本记录身份；句柄被复用成另一个对话框时重置状态。
- 点击后 400ms 起验证窗口是否消失；成功即写日志、发事件、移除状态。
- 失败达到 `maxClickAttempts` 后放弃并告警（同权限仍失败提示可能是 UIPI/完整性）。
- `dryRun` 只记录、不累计尝试和失败；同一对话框只记一次。
- 扫描与点击在**线程池**执行（`System.Threading.Timer`），UI 更新经隐藏 `Control` 调度；
  `SendMessageTimeout`/UIA 不会冻结托盘菜单或退出。

### 3.8 完整性、会话与部署

- 进程完整性由可执行文件所在位置的强制标签决定：工作区（Low）里的 exe 是 Low，看不到/点不动 Medium 的 LabVIEW。
  实测：工作区 exe 只看到 13 个进程；复制到 `%LOCALAPPDATA%\Programs` 后为 Medium、看到 244+。
- 因此部署必须放正常程序目录（`install.ps1` 默认如此）；LabVIEW 以管理员运行时守护也要管理员，否则 UIPI 拒绝消息。
- 只处理当前会话、当前虚拟桌面；DWM cloaked 的其他虚拟桌面窗口未单独判断（文档列为限制）。
- 单实例用 `Local\...` 互斥量；重复启动静默退出，避免开机自启叠出模态提示框。
- 日志按 1 MB 轮转保留一个历史文件；日志目录不可写时回退 `%TEMP%`，再失败才提示。

### 3.9 配置

- 默认读 exe 同目录 `LabVIEWDialogWatchdog.json`，`--config` 可覆盖。
- 严格 JSON；加载时容忍整行 `//` 注释。解析失败回退默认值并写 `W` 日志。
- 文件需 UTF-8；`DefaultJson()` 生成的内容可直接被本程序读取。
- 安装脚本不主动覆盖已有配置；开机自启只在显式 `-AutoStart` 时写入/`-RemoveAutoStart` 时删除。

## 4. 安全默认值

| 项 | 默认 | 原因 |
|---|---|---|
| `enableKeyboardFallback` | false | 无按钮自绘框按 Enter 可能走默认按钮 |
| `enableWindowCloseFallback` | false | 强关可能破坏调用方/服务状态 |
| `clickDelayMs` | 5000 | 留出有人值守的处理时间 |
| `userActivityGuard` | true | 用户操作时不打扰 |
| `minSeenCount` | 2 | 避免瞬时窗口误触 |
| `excludeDialogTitlePatterns` | 保存/覆盖/删除/重命名/退出/停止 | 避免误确认危险提示 |
| `maxClickAttempts` | 3 | 失败不无限重试 |

部署实例为了处理 `One Button Dialog`，显式开启 `enableKeyboardFallback`，其余保持安全默认。

## 5. 测试策略

- **单元测试**：按钮精确匹配/危险词、`internal warning` 识别、配置解析与默认值、值守窗口默认值、
  用户闸门判定、前台历史窗口。
- **端到端夹具**（不需要 LabVIEW）：标准 `MessageBox`、internal error 文案、带 owner 的 WinForms 框、
  原生 `LVDChild` 无按钮框（Enter）、危险标题“保存更改”不处理、默认配置不处理无按钮框、dry-run、
  非目标进程、含空格路径。共 31 项检查。
- **CI**：`build` 构建 + 单元测试 + 扫描 JSON 断言；`e2e` job 跑完整夹具。
- **真实 LabVIEW**：12 + 6 个真实 `#32770` 内部告警自动关闭；真实 `One Button Dialog` 阻塞 MCP 调用后
  由 Enter 解除并返回；5 秒值守窗口的无人/有人两条分支。详见 [verification.md](verification.md)。

测试脚本用 `Start-Process` + 重定向 + `WaitForExit`，因为 GUI 子系统 exe 的 stdout 在部分环境不可靠；
CLI 因此提供 `--out` 写 UTF-8 结果文件。

## 6. 已知限制

- 只处理可见、当前桌面、同会话的窗口；不处理崩溃/WER/UAC/其他会话。
- 必须与 LabVIEW 同或更高完整性；Low 完整性目录里的副本失效。
- 自绘无按钮框只能靠 Enter/默认按钮，双按钮框会走默认按钮；需要显式开启。
- `WM_CLOSE` 默认关闭；开启也可能让调用方拿到“被取消”。
- 用户操作闸门是启发式，不能保证 100% 区分手动与自动化。
- 非中英文按钮可能漏检，可通过配置补充。
- 轮询延迟约一个扫描周期（默认 500ms）；值守窗口以此为基础再多等 5 秒。

## 7. 文件对应

| 文件 | 作用 |
|---|---|
| `src/Program.cs` | 入口、CLI、单实例、日志初始化 |
| `src/TrayContext.cs` | 托盘、菜单、线程池轮询、UI 调度 |
| `src/DialogWatchdogEngine.cs` | 状态机、值守窗口、用户闸门、验证 |
| `src/WindowScanner.cs` | 窗口枚举、对话框分类、按钮匹配 |
| `src/DialogClicker.cs` | 分层点击策略 |
| `src/UserActivityGuard.cs` | 系统输入空闲、前台窗口历史判定 |
| `src/Settings.cs` | 配置模型、默认值、严格 JSON 加载 |
| `src/Native.cs` | Win32 P/Invoke |
| `tests/` | 单元测试、夹具、端到端脚本 |
| `docs/verification.md` | 真实验证记录 |
