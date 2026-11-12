using System;
using System.Collections.Generic;
using System.Linq;

namespace LabVIEWDialogWatchdog
{
    public sealed class DialogDismissedEventArgs : EventArgs
    {
        public DialogInfo Dialog { get; set; }
        public int Attempts { get; set; }
    }

    /// <summary>轮询扫描并点击确认按钮。所有状态以 UI 窗口句柄为键。</summary>
    public sealed class DialogWatchdogEngine
    {
        private sealed class DialogState
        {
            public int Seen;
            public int Attempts;
            public DateTime NextAttemptUtc;
            public DateTime VerifyAtUtc;
            public DateTime LastSeenUtc;
            public bool Completed;
            public bool GaveUp;
            public bool KeyboardDelayApplied;
            public bool DryRunLogged;
            public bool UserHandled;
            public DialogInfo LastInfo;
        }

        internal struct ForegroundSample
        {
            public DateTime At;
            public int Pid;
        }

        private readonly WatchdogSettings _settings;
        private readonly Log _log;
        private readonly Dictionary<IntPtr, DialogState> _states = new Dictionary<IntPtr, DialogState>();
        private readonly List<ForegroundSample> _foregroundHistory = new List<ForegroundSample>();

        public DialogWatchdogEngine(WatchdogSettings settings, Log log)
        {
            _settings = settings;
            _log = log;
        }

        public bool Paused { get; set; }
        public bool DryRun { get; set; }
        public int DetectedCount { get; private set; }
        public int DismissedCount { get; private set; }
        public int FailedCount { get; private set; }
        public int ClickSendFailures { get; private set; }
        public int UserSkippedCount { get; private set; }

        public event EventHandler<DialogDismissedEventArgs> Dismissed;
        public event EventHandler<DialogInfo> Detected;

        /// <summary>执行一次扫描；返回本次看到的候选对话框。</summary>
        public List<DialogInfo> Tick()
        {
            if (Paused)
            {
                return new List<DialogInfo>();
            }

            List<DialogInfo> dialogs;
            try
            {
                dialogs = WindowScanner.FindDialogs(_settings);
            }
            catch (Exception ex)
            {
                _log.Error("扫描窗口失败：" + ex.Message);
                return new List<DialogInfo>();
            }

            DateTime now = DateTime.UtcNow;
            foreach (DialogInfo dialog in dialogs)
            {
                DialogState state;
                if (_states.TryGetValue(dialog.Handle, out state) && !SameIdentity(state.LastInfo, dialog))
                {
                    // 窗口句柄被复用成另一个对话框：重置状态，避免新对话框被旧状态跳过。
                    _states.Remove(dialog.Handle);
                    state = null;
                }
                if (state == null)
                {
                    bool targetRecentlyForeground = WasForegroundRecently(dialog.ProcessId, now);
                    bool userOperating = UserActivityGuard.ShouldSkip(
                        _settings.userActivityGuard,
                        UserActivityGuard.GetIdleMilliseconds(),
                        _settings.userActivityIdleMs,
                        targetRecentlyForeground);
                    state = new DialogState
                    {
                        LastSeenUtc = now,
                        LastInfo = dialog,
                        UserHandled = userOperating
                    };
                    _states[dialog.Handle] = state;
                    if (userOperating)
                    {
                        // 对话框出现时用户正在操作 LabVIEW：整个生命周期都不自动点，留给用户。
                        UserSkippedCount++;
                        _log.Info("检测到用户正在操作 LabVIEW，跳过自动点击：" + Describe(string.Empty, dialog));
                    }
                    else
                    {
                        DetectedCount++;
                        _log.Info(Describe("检测到阻塞对话框", dialog));
                        EventHandler<DialogInfo> detected = Detected;
                        if (detected != null)
                        {
                            detected(this, dialog);
                        }
                    }
                }

                state.LastSeenUtc = now;
                state.LastInfo = dialog;
                if (state.Completed || state.UserHandled)
                {
                    continue;
                }
                state.Seen++;
                if (state.Seen < _settings.minSeenCount || now < state.NextAttemptUtc)
                {
                    continue;
                }

                if (DryRun)
                {
                    if (!state.DryRunLogged)
                    {
                        state.DryRunLogged = true;
                        _log.Info("[dry-run] 检测到，将点击 " + Describe(string.Empty, dialog));
                    }
                    state.NextAttemptUtc = now.AddSeconds(2);
                    continue;
                }

                if (state.Attempts >= _settings.maxClickAttempts)
                {
                    if (!state.GaveUp)
                    {
                        state.GaveUp = true;
                        FailedCount++;
                        _log.Warn("多次尝试后对话框仍未关闭，停止点击（若同权限仍失败，可能是 UIPI/完整性拒绝）："
                                  + Describe(string.Empty, dialog));
                    }
                    continue;
                }

                // 自绘对话框没有可枚举按钮时，先等一会儿，避免误触瞬时进度框。
                // 这里不消耗尝试次数，保证第一次真正的点击就是 Enter。
                if (state.Attempts == 0
                    && string.Equals(dialog.ButtonSource, "keyboard", StringComparison.OrdinalIgnoreCase)
                    && !state.KeyboardDelayApplied
                    && _settings.keyboardFallbackDelayMs > 0)
                {
                    state.KeyboardDelayApplied = true;
                    state.NextAttemptUtc = now.AddMilliseconds(_settings.keyboardFallbackDelayMs);
                    _log.Info(string.Format(
                        "对话框没有可枚举按钮，等待 {0}ms 后按 Enter：{1}",
                        _settings.keyboardFallbackDelayMs,
                        Describe(string.Empty, dialog)));
                    continue;
                }

                state.Attempts++;
                state.VerifyAtUtc = now.AddMilliseconds(400);
                state.NextAttemptUtc = now.AddMilliseconds(_settings.clickRetryDelayMs);

                string method;
                bool sent = DialogClicker.Click(dialog, state.Attempts, _settings, out method);
                if (sent)
                {
                    _log.Info(string.Format(
                        "第 {0} 次点击（{1}）：{2}",
                        state.Attempts,
                        method,
                        Describe(string.Empty, dialog)));
                }
                else
                {
                    ClickSendFailures++;
                    _log.Warn(string.Format(
                        "第 {0} 次点击发送失败（{1}）：{2}",
                        state.Attempts,
                        method,
                        Describe(string.Empty, dialog)));
                }
            }

            // 点击后确认窗口是否真的消失；成功即移除状态，避免句柄复用时串台。
            foreach (KeyValuePair<IntPtr, DialogState> pair in _states.ToList())
            {
                IntPtr handle = pair.Key;
                DialogState state = pair.Value;

                if (!Native.IsWindow(handle))
                {
                    if (state.Attempts > 0 && now >= state.VerifyAtUtc)
                    {
                        state.Completed = true;
                        DismissedCount++;
                        _log.Info(Describe("对话框已关闭", state.LastInfo));
                        EventHandler<DialogDismissedEventArgs> handler = Dismissed;
                        if (handler != null)
                        {
                            handler(this, new DialogDismissedEventArgs
                            {
                                Dialog = state.LastInfo,
                                Attempts = state.Attempts
                            });
                        }
                        _states.Remove(handle);
                    }
                    else if (state.Attempts == 0)
                    {
                        _states.Remove(handle);
                    }
                    continue;
                }

                if (state.GaveUp && now - state.LastSeenUtc > TimeSpan.FromSeconds(30))
                {
                    _states.Remove(handle);
                }
            }

            // 采样放在扫描之后：记录的是“下一轮扫描看到对话框之前”的前台窗口。
            RecordForeground(now);
            return dialogs;
        }

        private bool WasForegroundRecently(uint processId, DateTime now)
        {
            return HasRecentForeground(_foregroundHistory, processId, now, _settings.userActivityWindowMs);
        }

        internal static bool HasRecentForeground(
            IEnumerable<ForegroundSample> history, uint processId, DateTime now, int windowMs)
        {
            foreach (ForegroundSample sample in history)
            {
                if (sample.Pid == (int)processId &&
                    (now - sample.At).TotalMilliseconds <= windowMs)
                {
                    return true;
                }
            }
            return false;
        }

        private void RecordForeground(DateTime now)
        {
            _foregroundHistory.Add(new ForegroundSample
            {
                At = now,
                Pid = UserActivityGuard.GetForegroundProcessId()
            });

            int keepMs = Math.Max(8000, _settings.userActivityWindowMs + 2000);
            DateTime cutoff = now.AddMilliseconds(-keepMs);
            _foregroundHistory.RemoveAll(sample => sample.At < cutoff);
        }

        private static bool SameIdentity(DialogInfo left, DialogInfo right)
        {
            if (left == null || right == null)
            {
                return false;
            }
            return left.ProcessId == right.ProcessId
                   && string.Equals(left.ClassName, right.ClassName, StringComparison.Ordinal)
                   && string.Equals(left.Title, right.Title, StringComparison.Ordinal)
                   && string.Equals(left.Text, right.Text, StringComparison.Ordinal);
        }

        public static string Describe(string prefix, DialogInfo dialog)
        {
            string head = string.IsNullOrEmpty(prefix) ? string.Empty : prefix + "：";
            return string.Format(
                "{0}pid={1} process={2} class={3} title='{4}' button='{5}' source={6} internalError={7} ownerDisabled={8} text='{9}'",
                head,
                dialog.ProcessId,
                dialog.ProcessName,
                dialog.ClassName,
                dialog.Title,
                dialog.ButtonText,
                dialog.ButtonSource,
                dialog.InternalError,
                dialog.OwnerDisabled,
                Shorten(dialog.Text, 240));
        }

        private static string Shorten(string value, int max)
        {
            if (string.IsNullOrEmpty(value))
            {
                return string.Empty;
            }
            string oneLine = value.Replace("\r", " ").Replace("\n", " ");
            return oneLine.Length <= max ? oneLine : oneLine.Substring(0, max) + "...";
        }
    }
}
