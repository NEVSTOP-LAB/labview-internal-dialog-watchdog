using System;
using System.Windows.Automation;

namespace LabVIEWDialogWatchdog
{
    /// <summary>把“点击确认”的分层策略集中起来：越靠后越保守。</summary>
    public static class DialogClicker
    {
        public static bool Click(DialogInfo dialog, int attempt, out string method)
        {
            method = string.Empty;
            if (dialog == null || !Native.IsWindow(dialog.Handle))
            {
                method = "skipped: window gone";
                return false;
            }

            if (string.Equals(dialog.ButtonSource, "keyboard", StringComparison.OrdinalIgnoreCase))
            {
                return ClickKeyboard(dialog, attempt, out method);
            }

            IntPtr button = dialog.ButtonHandle;
            bool hasButton = button != IntPtr.Zero && Native.IsWindow(button) && Native.IsWindowEnabled(button);

            if (hasButton && attempt <= 2)
            {
                if (attempt == 1)
                {
                    // 异步点击，避免 LabVIEW UI 线程卡住时拖住守护进程。
                    bool posted = Native.PostMessage(button, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                    method = posted ? "BM_CLICK(post)" : "BM_CLICK(post) failed";
                    return posted;
                }

                // WinForms / 自绘按钮常常忽略 BM_CLICK，先用 UI Automation 的 Invoke。
                if (TryInvokeAutomation(dialog.Handle, dialog.ButtonText))
                {
                    method = "UIA Invoke";
                    return true;
                }

                IntPtr result;
                Native.SendMessageTimeout(
                    button, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero,
                    Native.SMTO_ABORTIFHUNG, 1500, out result);
                method = "BM_CLICK(send)";
                return true;
            }

            // 兜底 1：向对话框发 IDOK 命令。
            Native.PostMessage(dialog.Handle, Native.WM_COMMAND, (IntPtr)Native.IDOK, button);
            // 兜底 2：回车触发默认按钮。
            Native.PostMessage(dialog.Handle, Native.WM_KEYDOWN, (IntPtr)Native.VK_RETURN, IntPtr.Zero);
            Native.PostMessage(dialog.Handle, Native.WM_KEYUP, (IntPtr)Native.VK_RETURN, IntPtr.Zero);
            method = "WM_COMMAND(IDOK)+Enter";
            return true;
        }

        /// <summary>
        /// LabVIEW 自绘对话框（如 One Button Dialog）没有可枚举的按钮：
        /// 先激活窗口再发 Enter；仍不关闭则发 IDOK+Enter；最后才退到 WM_CLOSE。
        /// </summary>
        private static bool ClickKeyboard(DialogInfo dialog, int attempt, out string method)
        {
            if (attempt <= 1)
            {
                Native.SetForegroundWindow(dialog.Handle);
                System.Threading.Thread.Sleep(80);
                Native.PostMessage(dialog.Handle, Native.WM_KEYDOWN, (IntPtr)Native.VK_RETURN, IntPtr.Zero);
                Native.PostMessage(dialog.Handle, Native.WM_KEYUP, (IntPtr)Native.VK_RETURN, IntPtr.Zero);
                method = "Enter(foreground)";
                return true;
            }
            if (attempt == 2)
            {
                Native.PostMessage(dialog.Handle, Native.WM_COMMAND, (IntPtr)Native.IDOK, IntPtr.Zero);
                Native.PostMessage(dialog.Handle, Native.WM_KEYDOWN, (IntPtr)Native.VK_RETURN, IntPtr.Zero);
                Native.PostMessage(dialog.Handle, Native.WM_KEYUP, (IntPtr)Native.VK_RETURN, IntPtr.Zero);
                method = "WM_COMMAND(IDOK)+Enter";
                return true;
            }

            Native.PostMessage(dialog.Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            method = "WM_CLOSE";
            return true;
        }

        /// <summary>通过 UI Automation 找到名为 buttonText 的按钮并 Invoke。</summary>
        public static bool TryInvokeAutomation(IntPtr dialogHandle, string buttonText)
        {
            try
            {
                AutomationElement root = AutomationElement.FromHandle(dialogHandle);
                if (root == null)
                {
                    return false;
                }

                AutomationElementCollection buttons = root.FindAll(
                    TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button));
                string wanted = Normalize(buttonText);
                AutomationElement fallback = null;
                foreach (AutomationElement button in buttons)
                {
                    string name;
                    try
                    {
                        name = button.Current.Name;
                    }
                    catch
                    {
                        continue;
                    }
                    string normalized = Normalize(name);
                    if (normalized.Length == 0)
                    {
                        continue;
                    }
                    if (normalized.Equals(wanted, StringComparison.OrdinalIgnoreCase) ||
                        normalized.Equals("确定", StringComparison.OrdinalIgnoreCase) ||
                        normalized.Equals("OK", StringComparison.OrdinalIgnoreCase))
                    {
                        object pattern;
                        if (button.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                        {
                            try
                            {
                                ((InvokePattern)pattern).Invoke();
                                return true;
                            }
                            catch
                            {
                                // 继续尝试其他按钮。
                            }
                        }
                    }
                    if (fallback == null && (normalized == "确定" || normalized == "OK"))
                    {
                        fallback = button;
                    }
                }

                if (fallback != null)
                {
                    object pattern;
                    if (fallback.TryGetCurrentPattern(InvokePattern.Pattern, out pattern))
                    {
                        ((InvokePattern)pattern).Invoke();
                        return true;
                    }
                }
            }
            catch
            {
                // UI Automation 失败不抛给调用方，交给下一层兜底。
            }
            return false;
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrEmpty(value)
                ? string.Empty
                : value.Replace("&", string.Empty).Trim();
        }
    }
}
