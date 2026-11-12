# LabVIEW Dialog Watchdog

Windows 托盘守护进程：检测 LabVIEW 被模态对话框卡住的情况，自动点击确认按钮（或按 Enter），让 labview-mcp 等自动化调用继续执行。

针对的场景：labview-mcp 调用 LabVIEW 后，LabVIEW 弹出错误框或 `Internal Warning`，整个进程被模态循环阻塞，MCP 调用一直等不到返回、后续也连不上 LabVIEW；需要有人在桌面上点一下“确定”。

- 常驻系统托盘，无主窗口
- 单文件 exe（约 40 KB），只依赖 Windows 自带的 .NET Framework 4.8
- 默认每 500 ms 扫描一次，静止时 CPU 接近 0
- 只处理指定进程（默认 `LabVIEW`）的可见模态对话框，默认只点“确定/OK/继续/关闭”
- 默认安全策略：危险标题（保存/覆盖/删除/重命名等）不处理；无按钮自绘框的 Enter 兜底与 `WM_CLOSE` 兜底都需要显式开启
- 处理 LabVIEW 自绘 `One Button Dialog` 时，把 `enableKeyboardFallback` 设为 `true`；该路径已用真实 MCP 调用验证：对话框被 Enter 关闭、调用原样返回（见 `docs/verification.md` 3.2）
- 每处理一个对话框写一条日志，可在托盘菜单打开

## 工作原理

1. 用 `EnumWindows` 枚举所有顶层窗口，通过窗口 PID 找到目标进程（不依赖 `Process.GetProcessesByName`，在受限环境里也能工作）。
2. 识别三类对话框：
   - 标准 Win32 对话框（窗口类 `#32770`）；
   - 被已禁用的主窗口拥有的对话框（owner 被禁用，带 `WS_DLGFRAME`）；
   - 配置中额外声明的窗口类。
3. 在对话框内查找文本**精确匹配**“确认”的原生按钮（IDOK 例外）；找不到时：
   - 仅在显式开启 `enableKeyboardFallback` 后，对 LabVIEW 自绘窗口（默认 `LVDChild`，如 `One Button Dialog`）
     先激活窗口再发 Enter，然后才发 `WM_COMMAND(IDOK)+Enter`；
   - 对其他应用先尝试 UI Automation 的 Invoke。
4. 点击后确认窗口消失，记录日志并弹托盘气泡（可关闭）。扫描与点击在线程池执行，不阻塞托盘 UI。
5. 真正的“点击”在轮询线程上完成，不阻塞托盘；连续失败达到上限后停在日志里，不会无限点。

## 构建

需要 Windows 自带的 .NET Framework 4.8（`csc.exe`），不需要安装 .NET SDK：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Tests
```

产物：

- `dist\LabVIEWDialogWatchdog.exe`
- `dist\tests\`（测试夹具与测试运行器）

## 安装与运行

> [!IMPORTANT]
> 必须把 exe 放在**正常程序目录**（如 `%LOCALAPPDATA%\Programs\LabVIEWDialogWatchdog`）。
> 放在带 Low 完整性标签的目录（例如某些沙箱工作区）时，进程也是 Low 完整性，
> 会看不到、也点不动 Medium 完整性的 LabVIEW：表现为扫描结果为空、日志没有“检测到”。

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File install.ps1 -InstallDir "$env:LOCALAPPDATA\Programs\LabVIEWDialogWatchdog" -Start
```

也可以手动复制 `dist\LabVIEWDialogWatchdog.exe` 到目标目录后双击运行。首次运行会在 exe 旁使用默认配置。

开机自启：托盘菜单勾选“开机自动启动”（写 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`），或安装时加 `-AutoStart`。

> [!WARNING]
> 如果 LabVIEW 是**以管理员身份**运行的，守护进程也必须以管理员身份运行，否则 UIPI 会拒绝跨完整性级别的窗口消息（日志会记录点击但对话框不消失）。两者完整性级别相同即可，不要求一定提权。

## 配置

默认读取 exe 同目录的 `LabVIEWDialogWatchdog.json`（可用 `--config` 指定）。文件不存在时使用默认值；托盘菜单“打开配置文件”会生成一份可直接读取的默认配置（加载时也容忍整行 `//` 注释）。

| 字段 | 默认值 | 说明 |
|---|---|---|
| `targetProcessNames` | `["LabVIEW"]` | 目标进程名（不含 `.exe`） |
| `dialogWindowClasses` | `[]` | 额外当作对话框的窗口类 |
| `confirmButtonTexts` | `OK/确定/Continue/继续/Close/关闭` | 视为确认的按钮文本 |
| `excludeButtonTexts` | `Cancel/取消/Stop/停止/Abort/放弃/No/否` | 永不点击的按钮 |
| `excludeDialogTitlePatterns` | 保存/覆盖/删除/重命名/退出/停止 + 英文 | 标题命中片段的对话框不处理 |
| `keyboardFallbackWindowClasses` | `["LVDChild"]` | 没有原生按钮时可按 Enter 的窗口类 |
| `keyboardFallbackDelayMs` | `800` | 首次按 Enter 前的等待，避免误触瞬时进度框 |
| `enableKeyboardFallback` | `false` | 是否启用无按钮时的键盘兜底（默认关闭） |
| `enableWindowCloseFallback` | `false` | 是否允许 `WM_CLOSE` 最后兜底（默认关闭；强关可能破坏调用方） |
| `userActivityGuard` | `true` | 只在用户没有操作 LabVIEW 时自动点击 |
| `userActivityIdleMs` | `2000` | 系统最近键鼠输入早于该毫秒数才视为用户已离开 |
| `userActivityWindowMs` | `3000` | 回看多久内的前台窗口历史，判断对话框出现前用户是否在操作 LabVIEW |
| `pollIntervalMs` | `500` | 扫描间隔（100–5000） |
| `minSeenCount` | `2` | 连续看到多少次才点击 |
| `maxClickAttempts` | `3` | 单个对话框最多尝试次数 |
| `clickRetryDelayMs` | `900` | 两次尝试之间的等待 |
| `startPaused` | `false` | 启动即暂停 |
| `showNotifications` | `true` | 点击成功后弹托盘气泡 |
| `dryRun` | `false` | 只记录不点击 |
| `logDirectory` | `logs` | 日志目录（相对 exe 或绝对路径） |

> [!WARNING]
> 默认只自动确认：带错误语义、模态且 owner 被禁用、或只有一个按钮的标准 `#32770`。
> 普通多按钮的 OK/确定框默认不处理。LabVIEW 自绘无按钮对话框（如 `One Button Dialog`）
> 的 Enter 兜底默认关闭；需要时在配置里把 `enableKeyboardFallback` 设为 `true`，
> 并确认 `excludeDialogTitlePatterns` 覆盖了业务上危险的标题。
> `WM_CLOSE` 默认关闭：真实测试中强关 LabVIEW 自绘框曾导致其 AI gRPC 服务卡死。

## 只在自动化时工作（用户操作闸门）

默认开启 `userActivityGuard`：对话框出现时，如果“最近 3 秒内 LabVIEW 曾在前台”且“系统最近 2 秒内有键鼠输入”，
就判定为你正在手动操作 LabVIEW，**这个对话框的整个生命周期都不自动点击**，留给你自己处理；日志会写
`检测到用户正在操作 LabVIEW，跳过自动点击`，托盘状态显示“用户跳过 N 次”。

判据只看**对话框出现之前**的前台历史，避免对话框自己抢到前台后被误判。
MCP 在后台自动化时通常不满足该条件；你在其他窗口打字也不会触发跳过。

> [!NOTE]
> 这个闸门是启发式的，边界是：
> - 如果你正在操作 LabVIEW，同时 MCP 也触发了对话框，守护会跳过（按你的要求不干扰手动操作），需要你点一下；
> - 如果手动操作时 LabVIEW 不在前台、或距上次输入超过 2 秒，可能仍会被自动点击；
> - 需要完全停止自动点击时，用托盘菜单“暂停监控”。
> 可手动验证：保持守护运行，自己在 LabVIEW 里触发一个对话框，日志应出现“跳过自动点击”而不是“检测到阻塞对话框”。

## 命令行

```text
LabVIEWDialogWatchdog.exe                 # 托盘常驻
  --scan [--json]                         # 扫描一次，输出候选对话框
  --watch-seconds <n>                     # 无界面运行 n 秒（测试用）
  --config <path>                         # 指定配置
  --out <path>                            # 扫描/运行摘要写入文件
  --dry-run                               # 只记录，不点击
  --exit-after-dismiss                    # 成功关闭一个后立即结束
  --require-dismissal                     # 结束时没有成功关闭则返回 2
  --no-single-instance                    # 跳过单实例检查
  --version
```

`--scan --json` 的每一项包含：窗口句柄、PID、进程名、窗口类、标题、文本、按钮文本、
按钮 ID、`buttonSource`（`native`/`keyboard`）、`internalError`、`standardDialog`、`ownerDisabled`。

> [!NOTE]
> exe 是 GUI 子系统，在部分终端/重定向环境下 `--scan` 的 stdout 不可靠；
> 自动化与排障请用 `--out <path>` 读取 UTF-8 结果文件。

## 测试

不需要 LabVIEW 的端到端测试用 `LabVIEWDialogFixture.exe` 模拟四类对话框：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1
```

覆盖：

- Win32 `MessageBox`（`#32770`，含 internal error 文案）
- 带 owner 的 WinForms 模态框（非 `#32770`）
- 原生 `LVDChild` 窗口、无子按钮、只能按 Enter 关闭（模拟 LabVIEW `One Button Dialog`，需测试配置显式开启键盘兜底）
- `--dry-run` 不点击、非目标进程不处理、危险标题（保存）不处理
- 配置解析、按钮精确匹配、internal error 识别等单元测试

真实 LabVIEW 验证记录见 [docs/verification.md](docs/verification.md)：包括 12+6 个真实内部告警自动关闭，
以及 `One Button Dialog` 阻塞 MCP 调用、守护按 Enter 后调用原样返回的完整闭环。CI 只跑构建、单元测试与扫描冒烟，
GUI 端到端测试需在交互桌面本地运行（CI 另有一个 e2e job 跑夹具）。

## 排障

| 现象 | 排查 |
|---|---|
| 日志只有“守护已启动” | exe 与 LabVIEW 完整性级别不同；确认从正常程序目录运行，必要时以管理员运行 |
| 扫描为空 | 目标进程名不对；或对话框不在当前会话/桌面 |
| 日志有“点击”但框不消失 | UIPI 拒绝跨级别消息；让守护与 LabVIEW 同权限运行，日志会带 `failed`/多次尝试警告 |
| 点了但自动化调用仍不返回 | LabVIEW 的 gRPC 服务可能已因强制关闭而卡死，需要重启 LabVIEW；守护只负责关闭对话框，不修复服务内部状态 |
| 托盘图标不见 | 检查单实例是否已有进程；看 `logs\LabVIEWDialogWatchdog.log` |
| 配置改了不生效 | 配置必须是 UTF-8、严格 JSON（容忍整行 `//` 注释）；解析失败会回退默认值并写 W 日志 |

## 已知限制

- 只处理**可见的模态对话框**；标准 `#32770` 默认还要求“错误语义 / owner 被禁用 / 单按钮”之一，避免误点普通属性框。
- 与 LabVIEW 必须同或更高完整性级别；Windows 的 UIPI 无法绕过。
- `WM_CLOSE` 默认关闭，只有显式开启才作为最后兜底；即使开启也可能让调用方拿到“被取消/被中止”。
- 无按钮自绘窗口的 Enter 兜底默认关闭；开启后依赖 LabVIEW 对默认按钮的处理，双按钮框会走默认按钮。
  真实 `One Button Dialog` + MCP 调用已验证开启后可闭环（`docs/verification.md` 3.2）。
- 轮询而非窗口事件钩子：延迟约一个扫描周期（默认 500 ms），换来更低的资源占用与更少的全局钩子副作用。
- 按钮文本只做精确匹配（IDOK 例外），非中英文按钮可能漏检；可通过配置补充。
- 只处理当前会话当前虚拟桌面上的窗口；其他会话、其他虚拟桌面的 cloaked 窗口不处理。

## 目录

```text
src/                守护进程源码（单 exe）
tests/              夹具、单元测试、端到端脚本
docs/verification.md 真实 LabVIEW 验证记录
build.ps1           csc 构建
install.ps1         安装到程序目录、可选自启
```

## License

MIT
