using System;
using System.Collections.Generic;
using System.Linq;

namespace LabVIEWInternalDialogWatchdog
{
    public sealed class DialogDismissedEventArgs : EventArgs
    {
        public DialogInfo Dialog { get; set; }
        public int Attempts { get; set; }
    }

    /// <summary>
    /// 轮询识别 Internal Warning/Error 对话框；出现后等待 clickDelaySeconds 秒，
    /// 仍存在则自动点击确认。其他对话框不处理。
    /// </summary>
    public sealed class DialogWatchdogEngine
    {
        private sealed class DialogState
        {
            public int Attempts;
            public DateTime NextAttemptUtc;
            public DateTime VerifyAtUtc;
            public DateTime LastSeenUtc;
            public bool GaveUp;
            public bool DelayLogged;
            public bool DryRunLogged;
            public DialogInfo LastInfo;
        }

        private readonly WatchdogSettings _settings;
        private readonly Log _log;
        private readonly Dictionary<IntPtr, DialogState> _states = new Dictionary<IntPtr, DialogState>();

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

        public event EventHandler<DialogDismissedEventArgs> Dismissed;
        public event EventHandler<DialogInfo> Detected;

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
                    _states.Remove(dialog.Handle);
                    state = null;
                }
                if (state == null)
                {
                    state = new DialogState { LastSeenUtc = now, LastInfo = dialog };
                    _states[dialog.Handle] = state;
                    DetectedCount++;
                    _log.Info(Describe("检测到 Internal Warning 对话框", dialog));
                    EventHandler<DialogInfo> detected = Detected;
                    if (detected != null)
                    {
                        detected(this, dialog);
                    }
                }

                state.LastSeenUtc = now;
                state.LastInfo = dialog;

                // 出现后先等 N 秒；期间窗口消失就不动作。
                if (state.Attempts == 0 && !state.DelayLogged && _settings.clickDelaySeconds > 0)
                {
                    state.DelayLogged = true;
                    state.NextAttemptUtc = now.AddSeconds(_settings.clickDelaySeconds);
                    _log.Info(string.Format(
                        "等待 {0} 秒后自动点击：{1}",
                        _settings.clickDelaySeconds,
                        Describe(string.Empty, dialog)));
                    continue;
                }
                if (now < state.NextAttemptUtc)
                {
                    continue;
                }

                if (DryRun)
                {
                    if (!state.DryRunLogged)
                    {
                        state.DryRunLogged = true;
                        _log.Info("[dry-run] 将点击 " + Describe(string.Empty, dialog));
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
                        _log.Warn("多次尝试后对话框仍未关闭，停止点击：" + Describe(string.Empty, dialog));
                    }
                    continue;
                }

                state.Attempts++;
                state.VerifyAtUtc = now.AddMilliseconds(400);
                state.NextAttemptUtc = now.AddMilliseconds(_settings.clickRetryDelayMs);

                string method;
                bool sent = DialogClicker.Click(dialog, state.Attempts, out method);
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

            foreach (KeyValuePair<IntPtr, DialogState> pair in _states.ToList())
            {
                IntPtr handle = pair.Key;
                DialogState state = pair.Value;
                if (!Native.IsWindow(handle))
                {
                    if (state.Attempts > 0 && now >= state.VerifyAtUtc)
                    {
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

            return dialogs;
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
                "{0}pid={1} process={2} class={3} title='{4}' button='{5}' text='{6}'",
                head,
                dialog.ProcessId,
                dialog.ProcessName,
                dialog.ClassName,
                dialog.Title,
                dialog.ButtonText,
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
