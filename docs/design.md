# 设计说明

本文记录 LabVIEW Internal Dialog Watchdog 简化后的设计：只处理 LabVIEW 的 Internal Warning/Error 对话框。
使用与配置见 [README](../README.md)，真实验证见 [verification.md](verification.md)。

## 1. 目标与非目标

**目标**

- 识别 LabVIEW 进程的 `Internal Warning` / `Internal Error` 对话框（窗口类 `#32770`）。
- 出现后等待 `clickDelaySeconds`（默认 1 秒），仍存在则点击“确定”，让 labview-mcp 调用继续。
- 单 exe、托盘常驻、低资源占用；不引入运行时依赖。

**非目标**

- 不处理普通错误、保存/覆盖提示、`One Button Dialog`、崩溃转储、Windows 错误报告、UAC、其他会话窗口。
- 不区分用户是否值守、不判断是不是手动操作；只按“出现 + 延迟”执行。
- 不修复 LabVIEW 内部状态、不重启 LabVIEW、不绕过 UIPI/完整性级别。

## 2. 总体架构

```text
Program.Main
  ├─ --scan / --watch-seconds/--dry-run   CLI 与自动化测试入口
  └─ TrayContext                          托盘宿主（无主窗口）
       ├─ DialogWatchdogEngine            轮询、延迟、点击、验证
       │    ├─ WindowScanner              枚举窗口、只识别 Internal Warning
       │    ├─ DialogClicker              点击确认按钮
       │    └─ Log                        追加日志 + 1 MB 轮转
       └─ NotifyIcon / ContextMenu        状态、暂停、日志、配置、自启、退出
```

一次 Tick：

```text
EnumWindows 找目标进程窗口
  → 只保留 class=#32770、可见、在虚拟屏幕内
  → 收标题 + 静态文本，必须命中 Internal Warning/Error 标记
  → 找“确定/OK”按钮
  → 新对话框：记录并启动 N 秒延迟
  → 延迟结束仍存在：点击
  → 下一 Tick 验证窗口消失 → 日志/气泡
```

## 3. 关键实现

### 3.1 进程识别

在受限上下文里 `Process.GetProcessesByName`、Toolhelp32、WMI 可能只看到极少进程，
但 `EnumWindows` 仍能看到全部窗口。因此：

1. `EnumWindows` → `GetWindowThreadProcessId` 得到 PID；
2. `OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION)` + `QueryFullProcessImageNameW` 得到 exe 名；
3. 与 `targetProcessNames`（默认 `LabVIEW`）比较。

取不到进程名时不做兜底猜测，直接跳过（避免误点其他进程同名窗口）。

### 3.2 只识别 Internal Warning/Error

只接受同时满足以下条件的顶层窗口：

- 可见、在虚拟屏幕内（`SM_XVIRTUALSCREEN` 等，兼容多显示器与 `-32000` 隐藏面板）；
- 窗口类为 `#32770`；
- 所属进程匹配 `targetProcessNames`；
- 标题 + 静态文本包含 `internalDialogMarkers`（默认 `internal warning`、`internal error`、
  `内部警告`、`内部错误`，不区分大小写）。

其他对话框一律跳过。这一条是简化的核心：没有“通用 OK 框”“单按钮错误框”“自绘 LVDChild”分支。

### 3.3 确认按钮

- 只找原生子按钮：类名 `Button`、含 `button`，或控件 ID 为 `IDOK`。
- 文本精确匹配 `confirmButtonTexts`（OK/确定/Continue/继续/Close/关闭），去 `&` 与尾部省略号。
- `IDOK` 是语义兜底，但文本含保存/覆盖/删除/重命名等危险词时不点。
- 命中 `excludeButtonTexts`（取消/停止/放弃/No 等）永不点击。
- 找不到确认按钮就跳过，不做键盘兜底、不发 `WM_CLOSE`。

### 3.4 点击策略

1. `PostMessage(BM_CLICK)`：异步，不阻塞守护线程。
2. 仍不消失：UI Automation `InvokePattern`，再退到 `SendMessageTimeout(BM_CLICK, 1.5s)`。
3. 仍不消失：向对话框发已匹配按钮控件 ID 的 `WM_COMMAND`，必要时回车。
4. 每次点击检查发送结果；失败单独计数并写 `W` 日志。

没有 `WM_CLOSE`：真实测试中强关 LabVIEW 对话框曾导致其 AI gRPC 服务卡死。也没有键盘 Enter 兜底：
无法确认按钮时不动作，避免按到默认的危险按钮。

### 3.5 延迟（clickDelaySeconds）

首次识别到对话框后等待 `clickDelaySeconds` 秒（默认 1）再点击：

- 期间对话框消失（人手动点掉、或它自己关闭）→ 状态移除，守护不做任何动作；
- 仍存在 → 进入点击流程；
- 配置 0 表示立即点击，最大 300 秒。

延迟在第一次点击之前，不消耗 `maxClickAttempts`。实际点击时间 = 延迟 + 最多一个轮询周期。

### 3.6 状态机

每个窗口句柄一个状态：尝试次数、下次尝试时间、验证时间、是否已记录延迟、是否放弃、最后信息。

- 对话框身份 = PID + 类名 + 标题 + 文本；句柄被复用成另一个对话框时重置状态。
- 点击后 400ms 起验证窗口是否消失；成功写日志、发事件、移除状态。
- 失败达到 `maxClickAttempts` 后放弃并告警。
- `dryRun` 只记录、不累计尝试与失败。
- 扫描与点击在线程池执行，UI 更新经隐藏 `Control` 调度；不会冻结托盘菜单。

### 3.7 完整性、会话与部署

- 进程完整性由可执行文件所在位置的强制标签决定：工作区（Low）里的 exe 是 Low，看不到/点不动
  Medium 的 LabVIEW。实测工作区 exe 只看到 13 个进程，复制到 `%LOCALAPPDATA%\Programs` 后为 Medium、
  看到 244+。部署必须放正常程序目录（`install.ps1` 默认如此）。
- LabVIEW 以管理员运行时，守护也要管理员，否则 UIPI 拒绝消息。
- 只处理当前会话、当前虚拟桌面；其他会话/虚拟桌面不处理。
- 单实例用 `Local\...` 互斥量，重复启动静默退出；日志 1 MB 轮转，目录不可写时回退 `%TEMP%`。

## 4. 配置

- 默认读 exe 同目录 `LabVIEWInternalDialogWatchdog.json`，`--config` 可覆盖。
- 严格 JSON；加载时容忍整行 `//` 注释；解析失败回退默认并写 `W` 日志。
- 文件需 UTF-8；`DefaultJson()` 生成的内容可直接读取。
- 安装脚本不覆盖已有配置；自启只在显式 `-AutoStart` / `-RemoveAutoStart` 时改动。

## 5. 安全默认值

| 项 | 默认 | 原因 |
|---|---|---|
| `clickDelaySeconds` | 1 | 留出人工处理时间 |
| `internalDialogMarkers` | Internal Warning/Error | 只处理目标对话框 |
| `maxClickAttempts` | 3 | 失败不无限重试 |
| `excludeButtonTexts` | 取消/停止/放弃/No | 不点否定按钮 |
| `clickRetryDelayMs` | 900 | 重试间隔 |

## 6. 测试策略

- **单元测试**：Internal Warning/Error 标记、按钮精确匹配、危险文本、配置解析、延迟上限、默认值。
- **端到端夹具**（不需要 LabVIEW）：`Internal Warning` 对话框识别、约 1 秒延迟后点击、普通对话框不识别、
  非目标进程不处理、`--dry-run` 不点击。共 13 项检查。
- **CI**：`build` 构建 + 单元测试 + 扫描 JSON 断言；`e2e` job 跑完整夹具。
- **真实 LabVIEW**：12 + 6 个真实 `#32770` 内部告警自动关闭；简化版在 23:29 对
  `Internal Warning 0x0CA3C7EB : LVFPGATarget.cpp` 检测时默认 5 秒，实际等待 5.02 秒点击并关闭；现默认 1 秒。
  非 Internal Warning 的 `error 8` 框按设计忽略。详见 [verification.md](verification.md)。

## 7. 已知限制

- 只处理 `#32770` + Internal Warning/Error 文本；其他错误/提示/自绘对话框都不处理（包括会阻塞启动的
  `error 8 ...` 等）。
- 必须与 LabVIEW 同或更高完整性；Low 完整性目录里的副本失效。
- 不修复 LabVIEW 内部状态；对话框关闭后服务若卡死仍需重启 LabVIEW。
- 非中英文按钮可能漏检，可通过配置补充。
- 轮询延迟约一个扫描周期；实际点击 = `clickDelaySeconds` + 最多一个周期。

## 8. 文件对应

| 文件 | 作用 |
|---|---|
| `src/Program.cs` | 入口、CLI、单实例、日志初始化 |
| `src/TrayContext.cs` | 托盘、菜单、线程池轮询、UI 调度 |
| `src/DialogWatchdogEngine.cs` | 状态机、延迟、点击、验证 |
| `src/WindowScanner.cs` | 窗口枚举、`#32770` + 文本标记、按钮匹配 |
| `src/DialogClicker.cs` | 点击策略 |
| `src/Settings.cs` | 配置模型与默认值 |
| `src/Native.cs` | Win32 P/Invoke |
| `tests/` | 单元测试、夹具、端到端脚本 |

