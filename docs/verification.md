# 真实环境验证记录

- 日期：2026-11-12
- 环境：Windows 10/11 x64，LabVIEW 2026（26.3d539），labview-mcp 2.6.8
- 守护进程：`%LOCALAPPDATA%\Programs\LabVIEWDialogWatchdog`（Medium 完整性）

## 1. 无 LabVIEW 的端到端测试

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1
```

结果：`ALL PASS (31 checks)`。

覆盖：

- Win32 `MessageBox`（`#32770`）扫描、点击、调用方恢复
- `Internal Error` 文案识别
- 带 owner 的 WinForms 模态框（非 `#32770`）
- 原生 `LVDChild` 窗口、无子按钮、只能按 Enter 关闭（模拟 LabVIEW `One Button Dialog`，测试配置显式开启键盘兜底）
- 危险标题（“保存更改”）不被点击；默认配置下无按钮自绘框不被处理
- `--dry-run` 不点击
- 非目标进程不处理
- 配置解析、按钮精确匹配、internal error 识别等单元测试

## 1.1 独立 review 后的默认安全策略

两轮独立 agent review（2026-11-12）指出默认策略过于激进，已调整并补测：

- `enableKeyboardFallback` 默认 `false`：无按钮自绘框不再默认按 Enter，需显式开启。
- `enableWindowCloseFallback` 默认 `false`：`WM_CLOSE` 只在显式开启时作为最后兜底；真实事故中强关曾导致 LabVIEW AI gRPC 服务卡死。
- `excludeDialogTitlePatterns` 默认排除保存/覆盖/删除/重命名/退出/停止（中英）。
- `minSeenCount` 默认 `2`，减少瞬时窗口误触。
- 按钮文本只做精确匹配（IDOK 例外），避免“关闭而不保存”命中“关闭”。
- 标准 `#32770` 默认只处理：owner 被禁用 / 带错误语义 / 只有一个按钮；普通多按钮 OK 框不处理。
- 扫描/点击移到线程池，不再阻塞托盘 UI；句柄复用会按 PID+类+标题+文本重置状态；dry-run 不再累计失败。

> [!NOTE]
> 第 2 节的 12 个真实对话框是在 review 前的默认值下抓到的；新逻辑对它们的判定依据仍然成立
> （`Internal Warning` 命中错误语义、`error 8 ...` 对话框只有一个“确定”按钮），但建议在
> LabVIEW gRPC 服务恢复后用新版本复跑一次真实场景确认。

## 2. 真实 LabVIEW 内部告警自动关闭

在 `%LOCALAPPDATA%\Programs\LabVIEWDialogWatchdog\logs\LabVIEWDialogWatchdog.log` 中，
守护进程识别并关闭了 **12 个**真实 LabVIEW 对话框（两个 LabVIEW 实例，PID 13060 / 28600）：

| 次数 | 对话框文本 | 窗口类 | 按钮 |
|---|---|---|---|
| 2 | `error 8 creating temporary LVSB resource file` | `#32770` | 确定（id=2） |
| 2 | `Internal Warning 0x0CA3C7EB : "LVFPGATarget.cpp", line 971` | `#32770` | 确定 |
| 2 | `Internal Warning 0x200F30E9 : "ConfigManager.cpp", line 579` | `#32770` | 确定 |
| 2 | `Internal Warning 0x04701DFA : "image.cpp", line 7828` | `#32770` | 确定 |
| 2 | `Internal Warning 0x0E2E7DCF : "image.cpp", line 14786` | `#32770` | 确定 |
| 2 | `Internal Warning 0x802D6264 : "image.cpp", line 5378` | `#32770` | 确定 |

典型日志（检测到关闭约 0.3–0.6 秒；这批日志产生于“internal warning”标记加入前，
因此 `internalError=False`；新版本会把 `Internal Warning` 标为 `True`）：

```text
2026-11-12 00:44:14.004 [I] 检测到阻塞对话框：pid=13060 process=LabVIEW class=#32770 title='错误' button='确定' source=native internalError=False text='错误 | error 8 creating temporary LVSB resource file |'
2026-11-12 00:44:14.005 [I] 第 1 次点击（BM_CLICK(post)）：...
2026-11-12 00:44:14.602 [I] 对话框已关闭：...
```

这些对话框弹出时 LabVIEW 主窗口 `LVFrame` 被禁用（模态状态），关闭后恢复可用；
这正是 labview-mcp 调用被卡住的直接原因。

## 2.1 收紧默认策略后的真实回归

2026-11-12 01:19 用 review 后的新版本（键盘兜底/WM_CLOSE 默认关闭、危险标题排除、
标准框只处理 owner 禁用/错误语义/单按钮）重启 LabVIEW，日志记录 **6 个真实对话框全部自动关闭**：

| 次数 | 对话框文本 | 命中规则 |
|---|---|---|
| 1 | `error 8 creating temporary LVSB resource file` | 标准 `#32770` + 单按钮“确定” |
| 5 | `Internal Warning`（`ConfigManager.cpp`、`image.cpp`、`LVFPGATarget.cpp`） | 标准 `#32770` + 错误语义，`internalError=True` |

示例：

```text
2026-11-12 01:19:30.159 [I] 检测到阻塞对话框：pid=28864 process=LabVIEW class=#32770 title='错误' button='确定' source=native internalError=True ownerDisabled=False text='错误 | Internal Warning 0x200F30E9 :  "ConfigManager.cpp", line 579 ...'
2026-11-12 01:19:30.473 [I] 第 1 次点击（BM_CLICK(post)）：...
2026-11-12 01:19:31.101 [I] 对话框已关闭：...
```

说明收紧后的条件没有误伤真实内部告警；`error 8` 框走单按钮规则、`Internal Warning` 走错误语义规则。

## 3. MCP 被阻塞 → 守护按 Enter → 调用返回（闭环）

### 3.1 标准 #32770 内部告警

- 2026-11-11 23:48：`lvai_validate_aixml backend="grpc"` 触发真实 `#32770` 内部告警
  `Internal Warning 0xBB613420 : "HeapObjMapImpl.cpp", line 226`，标题「错误」。
- 弹出后 LabVIEW `LVFrame enabled=0`，所有 labview-mcp 调用超时/失败（无法继续）。
- 向按钮 `hwnd` 发送 `BM_CLICK` 后对话框关闭，MCP 恢复可用。
- 同一类 `#32770` 对话框随后由守护进程在真实 LabVIEW 启动过程中自动关闭（见第 2 节）。

### 3.2 自绘 LVDChild（One Button Dialog）闭环

2026-11-12 07:08 与 07:09，用 labview-mcp 的 **pylabview/ActiveX** 后端运行真实的
`One Button Dialog` VI（`LvaiDialogProbe.vi`）；守护进程开启键盘兜底（`enableKeyboardFallback=true`，
`WM_CLOSE` 仍关闭）后连续两次都完成了闭环：

| 次数 | MCP 结果 | 检测到弹窗 | 发 Enter | 弹窗关闭 |
|---|---|---|---|---|
| 1 | `ok: true, finished: true, runMs: 957` | 07:08:34.584 | 07:08:35.307 | 07:08:35.849 |
| 2 | `ok: true, finished: true, runMs: 901` | 07:09:08.439 | 07:09:09.152 | 07:09:09.690 |

守护日志原文：

```text
2026-11-12 07:08:34.584 [I] 检测到阻塞对话框：pid=28864 process=LabVIEW class=LVDChild title='' button='(Enter)' source=keyboard internalError=False ownerDisabled=True text=''
2026-11-12 07:08:34.585 [I] 对话框没有可枚举按钮，等待 500ms 后按 Enter：...
2026-11-12 07:08:35.307 [I] 第 1 次点击（Enter(foreground)）：...
2026-11-12 07:08:35.849 [I] 对话框已关闭：...
```

MCP 调用返回：

```json
{"ok":true,"via":"ActiveX","ran":true,"finished":true,"aborted":false,"runMs":957}
```

两次都是“第一次尝试 Enter 即关闭”，没有使用 `WM_CLOSE`；调用恢复后 LabVIEW 主窗口
`LVFrame enabled=1`，无残留对话框。

> [!NOTE]
> 环境修复：DSH 启动的 labview-mcp 服务进程是 Low 完整性，写不了 Medium 的
> `%TEMP%\LabVIEWMCP`（报 `Access to the path ... panel-read is denied`）。给该临时目录
> 加 Low 完整性标签后可正常写：
> `icacls "$env:LOCALAPPDATA\Temp\LabVIEWMCP" /setintegritylevel "(OI)(CI)L" /T`。
> 该实例的 gRPC 服务在之前一次强制 `WM_CLOSE` 后一直未恢复，因此这次闭环走
> pylabview/ActiveX 后端；对话框与点击路径与 gRPC 场景相同。

## 4. 复现步骤（真实 LabVIEW）

1. 构建并安装到正常程序目录：

   ```powershell
   pwsh -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Tests
   pwsh -NoProfile -ExecutionPolicy Bypass -File install.ps1 -Start
   ```

2. 确认守护与 LabVIEW 同为 Medium（或同为管理员）完整性：

   ```powershell
   # 日志应出现“守护已启动”
   Get-Content "$env:LOCALAPPDATA\Programs\LabVIEWDialogWatchdog\logs\LabVIEWDialogWatchdog.log" -Tail 5
   ```

3. 触发方式（任一）：

   - 启动/重启 LabVIEW，观察启动阶段内部告警被自动关闭；
   - 用 labview-mcp 运行本仓库 `tests/real-labview/LvaiDialogProbe.vi`（由
     `tests/real-labview/OneButtonDialog.xml` 生成），守护配置开启 `enableKeyboardFallback`；
   - 用 `tests\LabVIEWDialogFixture.exe --messagebox` 模拟。

   运行真实 VI 的 MCP 调用：

   ```text
   lvai_run_vi_as_top_level backend="pylabview" viPath="<repo>\tests\real-labview\LvaiDialogProbe.vi"
   ```

   预期 MCP 调用先阻塞，守护日志出现 `LVDChild` 检测、`Enter(foreground)`、`对话框已关闭`，
   随后调用返回 `ok:true, finished:true`。

4. 验证：

   - 对话框在 1 秒内消失；
   - 日志依次出现“检测到阻塞对话框”“第 N 次点击”“对话框已关闭”；
   - LabVIEW 主窗口恢复可操作，MCP 调用恢复。

## 5. 已知限制

- 守护进程与目标进程必须同或更高完整性；Low 完整性目录（如某些沙箱工作区）里的 exe 会失效。
- `WM_CLOSE` 默认关闭，只有显式开启才作为最后兜底；即使开启也可能让调用方拿到“被取消/被中止”。
- 无按钮自绘窗口的 Enter 兜底默认关闭；需要时在配置里开启 `enableKeyboardFallback`。
  已用真实 `One Button Dialog` + MCP 调用验证：开启后第一次 Enter 即可关闭并让调用返回（见 3.2）。
- 不处理崩溃转储、Windows 错误报告、UAC 提权框、其他会话窗口。
- 标准 `#32770` 默认只处理 owner 被禁用 / 错误语义 / 单按钮三种情况，普通多按钮 OK 框不处理。
- 轮询延迟约一个扫描周期。

原始日志保留在本机 `%LOCALAPPDATA%\Programs\LabVIEWDialogWatchdog\logs\`，不随仓库提交。

