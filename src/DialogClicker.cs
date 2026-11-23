using System;
using System.Windows.Automation;

namespace LabVIEWInternalDialogWatchdog
{
    /// <summary>对已识别的 Internal Warning 对话框执行确认点击。</summary>
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

            IntPtr button = dialog.ButtonHandle;
            bool hasButton = button != IntPtr.Zero && Native.IsWindow(button) && Native.IsWindowEnabled(button);
            if (!hasButton)
            {
                method = "skipped: no button";
                return false;
            }

            if (attempt == 1)
            {
                // 异步点击，避免对话框线程卡住时拖住守护。
                bool posted = Native.PostMessage(button, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero);
                method = posted ? "BM_CLICK(post)" : "BM_CLICK(post) failed";
                return posted;
            }

            if (attempt == 2)
            {
                // WinForms/自绘按钮常忽略 BM_CLICK，先试 UI Automation。
                if (TryInvokeAutomation(dialog.Handle, dialog.ButtonText))
                {
                    method = "UIA Invoke";
                    return true;
                }

                IntPtr result;
                IntPtr sent = Native.SendMessageTimeout(
                    button, Native.BM_CLICK, IntPtr.Zero, IntPtr.Zero,
                    Native.SMTO_ABORTIFHUNG, 1500, out result);
                method = sent != IntPtr.Zero ? "BM_CLICK(send)" : "BM_CLICK(send) failed";
                return sent != IntPtr.Zero;
            }

            // 最后：向对话框发已匹配按钮的控件 ID，必要时再回车。
            IntPtr commandId = dialog.ButtonId > 0 ? (IntPtr)dialog.ButtonId : (IntPtr)Native.IDOK;
            bool commandPosted = Native.PostMessage(dialog.Handle, Native.WM_COMMAND, commandId, button);
            bool keyPosted = false;
            if (dialog.ButtonId == 0 || dialog.ButtonId == Native.IDOK)
            {
                keyPosted = Native.PostMessage(dialog.Handle, Native.WM_KEYDOWN, (IntPtr)Native.VK_RETURN, IntPtr.Zero);
                Native.PostMessage(dialog.Handle, Native.WM_KEYUP, (IntPtr)Native.VK_RETURN, IntPtr.Zero);
            }
            method = "WM_COMMAND(" + commandId + ")+Enter";
            return commandPosted || keyPosted;
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
                }
            }
            catch
            {
                // UI Automation 失败交给下一层兜底。
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
