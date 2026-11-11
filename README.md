# LabVIEW Dialog Watchdog

Windows 托盘守护进程：检测 LabVIEW 被模态对话框卡住的情况，自动点击确认按钮（或按 Enter），让 labview-mcp 等自动化调用继续执行。

针对的场景：labview-mcp 调用 LabVIEW 后，LabVIEW 弹出错误框或 `Internal Warning`，整个进程被模态循环阻塞，MCP 调用一直等不到返回、后续也连不上 LabVIEW；需要有人在桌面上点一下“确定”。

- 常驻系统托盘，无主窗口
- 单文件 exe（约 40 KB），只依赖 Windows 自带的 .NET Framework 4.8
- 默认每 300–500 ms 扫描一次，静止时 CPU 接近 0
- 只处理指定进程（默认 `LabVIEW`）的可见模态对话框，默认只点“确定/OK/继续/关闭”
- 每处理一个对话框写一条日志，可在托盘菜单打开

## 工作原理

1. 用 `EnumWindows` 枚举所有顶层窗口，通过窗口 PID 找到目标进程（不依赖 `Process.GetProcessesByName`，在受限环境里也能工作）。
2. 识别三类对话框：
   - 标准 Win32 对话框（窗口类 `#32770`）；
   - 被已禁用的主窗口拥有的对话框（owner 被禁用，带 `WS_DLGFRAME`）；
   - 配置中额外声明的窗口类。
3. 在对话框内查找文本匹配“确认”的原生按钮；找不到时：
   - 对 LabVIEW 自绘窗口（默认 `LVDChild`，如 `One Button Dialog`）先激活窗口再发 Enter，然后才退到 `WM_COMMAND(IDOK)+Enter`、`WM_CLOSE`；
   - 对其他应用先尝试 UI Automation 的 Invoke。
4. 点击后确认窗口消失，记录日志并弹托盘气泡（可关闭）。
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

默认读取 exe 同目录的 `LabVIEWDialogWatchdog.json`（可用 `--config` 指定）。文件不存在时使用默认值；托盘菜单“打开配置文件”会生成一份带注释的默认配置。

| 字段 | 默认值 | 说明 |
|---|---|---|
| `targetProcessNames` | `["LabVIEW"]` | 目标进程名（不含 `.exe`） |
| `dialogWindowClasses` | `[]` | 额外当作对话框的窗口类 |
| `confirmButtonTexts` | `OK/确定/Continue/继续/Close/关闭` | 视为确认的按钮文本 |
| `excludeButtonTexts` | `Cancel/取消/Stop/停止/Abort/放弃/No/否` | 永不点击的按钮 |
| `excludeDialogTitlePatterns` | `[]` | 标题命中片段的对话框不处理 |
| `keyboardFallbackWindowClasses` | `["LVDChild"]` | 没有原生按钮时可按 Enter 的窗口类 |
| `keyboardFallbackDelayMs` | `800` | 首次按 Enter 前的等待，避免误触瞬时进度框 |
| `enableKeyboardFallback` | `true` | 是否启用无按钮时的键盘兜底 |
| `pollIntervalMs` | `500` | 扫描间隔（100–5000） |
| `minSeenCount` | `1` | 连续看到多少次才点击 |
| `maxClickAttempts` | `3` | 单个对话框最多尝试次数 |
| `clickRetryDelayMs` | `900` | 两次尝试之间的等待 |
| `startPaused` | `false` | 启动即暂停 |
| `showNotifications` | `true` | 点击成功后弹托盘气泡 |
| `dryRun` | `false` | 只记录不点击 |
| `logDirectory` | `logs` | 日志目录（相对 exe 或绝对路径） |

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

## 测试

不需要 LabVIEW 的端到端测试用 `LabVIEWDialogFixture.exe` 模拟四类对话框：

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1
```

覆盖：

- Win32 `MessageBox`（`#32770`，含 internal error 文案）
- 带 owner 的 WinForms 模态框（非 `#32770`，UIA 兜底）
- 原生 `LVDChild` 窗口、无子按钮、只能按 Enter 关闭（模拟 LabVIEW `One Button Dialog`）
- `--dry-run` 不点击、非目标进程不处理
- 配置解析、按钮文本判定、internal error 识别等单元测试

真实 LabVIEW 验证记录见 [docs/verification.md](docs/verification.md)。

## 排障

| 现象 | 排查 |
|---|---|
| 日志只有“守护已启动” | exe 与 LabVIEW 完整性级别不同；确认从正常程序目录运行，必要时以管理员运行 |
| 扫描为空 | 目标进程名不对；或对话框不在当前会话/桌面 |
| 日志有“点击”但框不消失 | UIPI 拒绝跨级别消息；让守护与 LabVIEW 同权限运行 |
| 点了但自动化调用仍不返回 | LabVIEW 的 gRPC 服务可能已因强制关闭而卡死，需要重启 LabVIEW；守护只负责关闭对话框，不修复服务内部状态 |
| 托盘图标不见 | 检查单实例是否已有进程；看 `logs\LabVIEWDialogWatchdog.log` |

## 已知限制

- 只处理**可见的模态对话框**，不处理崩溃转储、Windows 错误报告、UAC 提权框、其他会话的窗口。
- 与 LabVIEW 必须同或更高完整性级别；Windows 的 UIPI 无法绕过。
- `WM_CLOSE` 是最后兜底：它能解除阻塞，但可能让调用方拿到“被取消/被中止”，不保证业务结果正常返回。
- 对 LabVIEW 自绘窗口，Enter 依赖 LabVIEW 的默认按钮处理；不同版本/不同对话框可能需要调整 `keyboardFallbackWindowClasses`。
- 轮询而非窗口事件钩子：延迟约一个扫描周期（默认 300–500 ms），换来更低的资源占用与更少的全局钩子副作用。

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
