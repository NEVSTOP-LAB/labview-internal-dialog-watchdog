# 真实验证记录

- 日期：2026-11-12
- 环境：Windows x64，LabVIEW 2026（26.3d539），labview-mcp 2.6.8
- 守护进程：`%LOCALAPPDATA%\Programs\LabVIEWDialogWatchdog`（Medium 完整性）
- 范围：只处理 `Internal Warning` / `Internal Error` 的 `#32770` 对话框

## 1. 自动化夹具（不需要 LabVIEW）

```powershell
pwsh -NoProfile -ExecutionPolicy Bypass -File tests\run-tests.ps1
```

结果：`ALL PASS (13 checks)`。

覆盖：

- `Internal Warning 0x...` 文本识别、标题“错误”+“确定”按钮
- 配置 `clickDelaySeconds=1` 时，检测到点击约 1 秒（日志时间差 900–2500ms）
- 普通对话框（非 Internal Warning）不被识别、不被点击
- 非目标进程不处理
- `--dry-run` 不点击
- 单元测试：标记匹配、按钮精确匹配、危险词、配置解析、延迟上限、默认 5 秒

## 2. 真实 LabVIEW Internal Warning

### 2.1 简化版直接验证（2026-11-12 23:29）

重启 LabVIEW 后出现 `Internal Warning 0x0CA3C7EB : "LVFPGATarget.cpp", line 971`，
守护日志：

```text
23:29:16.902 检测到 Internal Warning 对话框：pid=47664 class=#32770 title='错误' button='确定' text='错误 | Internal Warning 0x0CA3C7EB : "LVFPGATarget.cpp", line 971 ...'
23:29:16.902 等待 5 秒后自动点击：...
23:29:21.925 第 1 次点击（BM_CLICK(post)）：...
23:29:22.544 对话框已关闭：...
```

检测到点击 **5.02 秒**，符合默认 `clickDelaySeconds=5`；点击后对话框关闭，LabVIEW 恢复。

### 2.2 历史批量验证（旧版本，同一识别路径）

在之前版本中，守护进程在 LabVIEW 启动阶段自动关闭了 **12 + 6 个**真实 `#32770` 内部告警
（`Internal Warning` 的 `LVFPGATarget.cpp` / `ConfigManager.cpp` / `image.cpp` 等），
每个约 0.3–0.6 秒（当时延迟配置较小）。这些对话框的识别条件（`#32770` + 文本含 `Internal Warning`）
与当前简化版相同。

### 2.3 非目标对话框按设计忽略

同一启动阶段出现的 `error 8 creating temporary LVSB resource file` 不是 Internal Warning，
简化版不处理；需要人工处理这类启动错误。这是“只监护 Internal Warning”的预期行为。

## 3. MCP 阻塞链路

2026-11-11 23:48，`lvai_validate_aixml backend="grpc"` 触发真实 `#32770` 内部告警
`Internal Warning 0xBB613420 : "HeapObjMapImpl.cpp", line 226`，标题「错误」；弹出后 LabVIEW
`LVFrame enabled=0`，所有 labview-mcp 调用超时/失败。向按钮发送 `BM_CLICK` 后对话框关闭，MCP 恢复可用。
守护进程处理的是同一类 `#32770` Internal Warning 对话框。

## 4. 复现步骤

1. 构建并安装到正常程序目录：

   ```powershell
   pwsh -NoProfile -ExecutionPolicy Bypass -File build.ps1 -Tests
   pwsh -NoProfile -ExecutionPolicy Bypass -File install.ps1 -Start
   ```

2. 确认守护启动（日志出现“守护已启动”），配置中的 `clickDelaySeconds` 默认 5。

3. 触发方式：

   - 重启 LabVIEW，观察启动阶段的 Internal Warning 在约 5 秒后被自动关闭；
   - 用 `tests\LabVIEWDialogFixture.exe --internal-warning` 模拟。

4. 验证：

   - 日志出现“检测到 Internal Warning 对话框” → “等待 5 秒后自动点击” → “第 1 次点击” → “对话框已关闭”；
   - 普通对话框不产生任何“检测到”日志；
   - 点击后 LabVIEW 主窗口恢复可操作。

## 5. 已知限制

- 只处理 `#32770` + Internal Warning/Error 文本；其他错误/提示/自绘对话框不处理，包括可能阻塞启动的
  `error 8 ...`。
- 守护与 LabVIEW 必须同或更高完整性；Low 完整性目录里的 exe 会失效。
- 不修复 LabVIEW 服务内部状态；关闭对话框后若 gRPC 服务仍卡死，需要重启 LabVIEW。
- 实际点击时间 = `clickDelaySeconds` + 最多一个轮询周期。

原始日志保留在本机 `%LOCALAPPDATA%\Programs\LabVIEWDialogWatchdog\logs\`，不随仓库提交。

