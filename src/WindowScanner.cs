using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LabVIEWDialogWatchdog
{
    /// <summary>一个 LabVIEW Internal Warning/Error 对话框及其确认按钮。</summary>
    public sealed class DialogInfo
    {
        public IntPtr Handle;
        public uint ProcessId;
        public string ProcessName;
        public string ClassName;
        public string Title;
        public string Text;
        public IntPtr ButtonHandle;
        public string ButtonText;
        public int ButtonId;
    }

    /// <summary>按 PID 取 exe 名称；进程枚举受限时也能工作。</summary>
    internal static class ProcessNameCache
    {
        private sealed class Entry
        {
            public string Name;
            public DateTime ExpiresUtc;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<uint, Entry> Cache = new Dictionary<uint, Entry>();

        internal static string Get(uint processId)
        {
            lock (Gate)
            {
                Entry entry;
                DateTime now = DateTime.UtcNow;
                if (Cache.TryGetValue(processId, out entry) && entry.ExpiresUtc > now)
                {
                    return entry.Name;
                }

                string name = Resolve(processId);
                Cache[processId] = new Entry { Name = name, ExpiresUtc = now.AddSeconds(10) };
                if (Cache.Count > 256)
                {
                    var expired = new List<uint>();
                    foreach (KeyValuePair<uint, Entry> pair in Cache)
                    {
                        if (pair.Value.ExpiresUtc <= now)
                        {
                            expired.Add(pair.Key);
                        }
                    }
                    foreach (uint key in expired)
                    {
                        Cache.Remove(key);
                    }
                }
                return name;
            }
        }

        private static string Resolve(uint processId)
        {
            IntPtr process = Native.OpenProcess(Native.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
            if (process == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var buffer = new StringBuilder(1024);
                uint size = 1024;
                if (!Native.QueryFullProcessImageName(process, 0, buffer, ref size))
                {
                    return null;
                }
                return Path.GetFileNameWithoutExtension(buffer.ToString());
            }
            finally
            {
                Native.CloseHandle(process);
            }
        }
    }

    /// <summary>
    /// 只识别目标进程中窗口类为 #32770、文本包含 Internal Warning/Error 标记的对话框。
    /// 其他对话框一律不处理。
    /// </summary>
    public static class WindowScanner
    {
        private static readonly HashSet<string> IgnoredClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tooltips_class32", "#32768", "ComboLBox", "SysShadow", "IME", "MSCTFIME UI",
            "GDI+ Hook Window Class", "OleDdeWndClass", "OleAutomationWnd", "DDEMLEvent",
            "DDEMLMom", "LVWinSessionMonitor"
        };

        private static readonly string[] DangerousButtonWords =
        {
            "保存", "不保存", "覆盖", "删除", "重命名",
            "Save", "Overwrite", "Delete", "Rename"
        };

        public static List<DialogInfo> FindDialogs(WatchdogSettings settings)
        {
            var results = new List<DialogInfo>();
            uint self = (uint)System.Diagnostics.Process.GetCurrentProcess().Id;
            var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in settings.targetProcessNames)
            {
                if (!string.IsNullOrWhiteSpace(name))
                {
                    targets.Add(StripExeSuffix(name.Trim()));
                }
            }

            Native.EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (!Native.IsWindowVisible(hWnd))
                {
                    return true;
                }

                uint processId;
                Native.GetWindowThreadProcessId(hWnd, out processId);
                if (processId == self || processId == 0)
                {
                    return true;
                }

                string className = Native.GetClassNameSafe(hWnd);
                if (!string.Equals(className, "#32770", StringComparison.OrdinalIgnoreCase) ||
                    IgnoredClasses.Contains(className))
                {
                    return true;
                }

                string processName = ProcessNameCache.Get(processId);
                if (!IsTarget(processName, targets))
                {
                    return true;
                }
                if (!Native.IsWindowEnabled(hWnd))
                {
                    return true;
                }

                Native.RECT rect;
                if (!Native.GetWindowRect(hWnd, out rect) || !IsOnScreen(rect))
                {
                    return true;
                }

                string title = Native.GetWindowTextSafe(hWnd, 512);
                string text = CollectDialogText(hWnd, title);
                if (!ContainsAny(text + " " + title, settings.internalDialogMarkers))
                {
                    return true;
                }

                ButtonChoice button = FindConfirmButton(hWnd, settings);
                if (button == null)
                {
                    return true;
                }

                results.Add(new DialogInfo
                {
                    Handle = hWnd,
                    ProcessId = processId,
                    ProcessName = processName ?? "(unknown)",
                    ClassName = className,
                    Title = title,
                    Text = text,
                    ButtonHandle = button.Handle,
                    ButtonText = button.Text,
                    ButtonId = button.Id
                });
                return true;
            }, IntPtr.Zero);

            return results;
        }

        private static bool IsTarget(string processName, HashSet<string> targets)
        {
            return !string.IsNullOrEmpty(processName) && targets.Contains(processName);
        }

        private static string StripExeSuffix(string name)
        {
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 4)
                : name;
        }

        private static bool IsOnScreen(Native.RECT rect)
        {
            int left = Native.GetSystemMetrics(Native.SM_XVIRTUALSCREEN);
            int top = Native.GetSystemMetrics(Native.SM_YVIRTUALSCREEN);
            int width = Native.GetSystemMetrics(Native.SM_CXVIRTUALSCREEN);
            int height = Native.GetSystemMetrics(Native.SM_CYVIRTUALSCREEN);
            int right = left + width;
            int bottom = top + height;
            return rect.Right > left && rect.Left < right && rect.Bottom > top && rect.Top < bottom;
        }

        private sealed class ButtonChoice
        {
            public IntPtr Handle;
            public string Text;
            public int Id;
        }

        private static ButtonChoice FindConfirmButton(IntPtr dialog, WatchdogSettings settings)
        {
            ButtonChoice best = null;
            int bestScore = 0;
            Native.EnumChildWindows(dialog, delegate(IntPtr child, IntPtr lParam)
            {
                if (!Native.IsWindowVisible(child) || !Native.IsWindowEnabled(child))
                {
                    return true;
                }

                string className = Native.GetClassNameSafe(child);
                string rawText = Native.GetWindowTextSafe(child, 256);
                if (rawText.Length == 0)
                {
                    return true;
                }

                int id = Native.GetDlgCtrlID(child);
                bool looksLikeButton = string.Equals(className, "Button", StringComparison.OrdinalIgnoreCase)
                                       || className.IndexOf("button", StringComparison.OrdinalIgnoreCase) >= 0
                                       || id == Native.IDOK;
                if (!looksLikeButton)
                {
                    return true;
                }

                string text = NormalizeButtonText(rawText);
                if (text.Length == 0 || IsExcludedButton(settings.excludeButtonTexts, text))
                {
                    return true;
                }

                int score = ScoreButton(text, settings.confirmButtonTexts);
                if (score <= 0 && id == Native.IDOK && !ContainsAny(text, DangerousButtonWords))
                {
                    // IDOK 是确认语义；危险文本即使挂 IDOK 也不点。
                    score = 100;
                }
                if (score <= 0)
                {
                    return true;
                }
                if (id == Native.IDOK)
                {
                    score += 1000;
                }

                if (best == null || score > bestScore)
                {
                    best = new ButtonChoice { Handle = child, Text = rawText.Trim(), Id = id };
                    bestScore = score;
                }
                return true;
            }, IntPtr.Zero);
            return best;
        }

        private static string NormalizeButtonText(string text)
        {
            if (text == null)
            {
                return string.Empty;
            }
            string value = text.Replace("&", string.Empty).Trim();
            value = value.TrimEnd('.', '\u2026');
            while (value.Contains("  "))
            {
                value = value.Replace("  ", " ");
            }
            return value.Trim();
        }

        private static bool IsExcludedButton(string[] excludes, string text)
        {
            if (excludes == null)
            {
                return false;
            }
            foreach (string exclude in excludes)
            {
                if (string.IsNullOrWhiteSpace(exclude))
                {
                    continue;
                }
                if (text.Equals(NormalizeButtonText(exclude), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static int ScoreButton(string text, string[] confirms)
        {
            int best = 0;
            foreach (string confirm in confirms)
            {
                if (string.IsNullOrWhiteSpace(confirm))
                {
                    continue;
                }
                if (text.Equals(NormalizeButtonText(confirm), StringComparison.OrdinalIgnoreCase))
                {
                    best = Math.Max(best, 500);
                }
            }
            return best;
        }

        private static string CollectDialogText(IntPtr dialog, string title)
        {
            var builder = new StringBuilder();
            if (!string.IsNullOrEmpty(title))
            {
                builder.Append(title).Append(" | ");
            }

            Native.EnumChildWindows(dialog, delegate(IntPtr child, IntPtr lParam)
            {
                string className = Native.GetClassNameSafe(child);
                if (!string.Equals(className, "Static", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(className, "Edit", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                string text = Native.GetWindowTextSafe(child, 512);
                if (text.Length > 0)
                {
                    builder.Append(text.Trim()).Append(" | ");
                }
                return true;
            }, IntPtr.Zero);

            string result = builder.ToString().Trim();
            return result.Length > 800 ? result.Substring(0, 800) : result;
        }

        private static bool ContainsAny(string haystack, string[] needles)
        {
            if (needles == null || string.IsNullOrEmpty(haystack))
            {
                return false;
            }
            foreach (string needle in needles)
            {
                if (!string.IsNullOrWhiteSpace(needle) &&
                    haystack.IndexOf(needle.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        // 测试入口：与生产路径共用同一套判定。
        internal static bool HasInternalMarker(string text, string[] markers)
        {
            return ContainsAny(text ?? string.Empty, markers);
        }

        internal static bool IsDangerousButtonText(string text)
        {
            return ContainsAny(text ?? string.Empty, DangerousButtonWords);
        }

        internal static bool IsConfirmText(string text, string[] confirms)
        {
            return ScoreButton(NormalizeButtonText(text), confirms) > 0;
        }

        internal static bool IsExcludedText(string text, string[] excludes)
        {
            return IsExcludedButton(excludes, NormalizeButtonText(text));
        }
    }
}
