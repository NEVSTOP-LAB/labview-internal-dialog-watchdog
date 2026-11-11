using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LabVIEWDialogWatchdog
{
    /// <summary>一个被识别为“阻塞对话框”的窗口及其确认按钮。</summary>
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
        public string ButtonSource;
        public bool InternalError;
        public bool IsStandardDialog;
        public bool OwnerDisabled;
    }

    /// <summary>按 PID 取 exe 名称；进程枚举被限制的环境下也能工作。</summary>
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

    /// <summary>枚举顶层窗口，筛出目标进程中带确认按钮（或可键盘确认）的模态对话框。</summary>
    public static class WindowScanner
    {
        private static readonly string[] InternalErrorMarkers =
        {
            "internal error", "internal error occurred", "internal warning",
            "内部错误", "内部警告", "unexpected error"
        };

        private static readonly HashSet<string> IgnoredClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "tooltips_class32", "#32768", "ComboLBox", "SysShadow", "IME", "MSCTFIME UI",
            "GDI+ Hook Window Class", "OleDdeWndClass", "OleAutomationWnd", "DDEMLEvent",
            "DDEMLMom", "LVWinSessionMonitor"
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
                if (IgnoredClasses.Contains(className))
                {
                    return true;
                }

                string processName = ProcessNameCache.Get(processId);
                string title = Native.GetWindowTextSafe(hWnd, 512);
                if (!IsTarget(processName, className, title, targets))
                {
                    return true;
                }

                Native.RECT rect;
                if (!Native.GetWindowRect(hWnd, out rect) || !IsOnScreen(rect))
                {
                    return true;
                }

                IntPtr owner = Native.GetWindow(hWnd, Native.GW_OWNER);
                int style = Native.GetWindowLong(hWnd, Native.GWL_STYLE);
                bool owned = owner != IntPtr.Zero;
                bool ownerDisabled = owned && !Native.IsWindowEnabled(owner);
                bool standard = string.Equals(className, "#32770", StringComparison.OrdinalIgnoreCase);
                bool dialogFrame = (style & Native.WS_DLGFRAME) != 0;

                // 非标准对话框只接受“被已禁用的主窗口拥有的对话框”，避免误碰浮动面板。
                bool customDialog = owned && dialogFrame && ownerDisabled;
                bool configuredClass = ContainsClassName(settings.dialogWindowClasses, className);

                if (!standard && !customDialog && !configuredClass)
                {
                    return true;
                }
                if (string.Equals(className, "LVFrame", StringComparison.OrdinalIgnoreCase) && !owned)
                {
                    return true;
                }
                if (!Native.IsWindowEnabled(hWnd))
                {
                    return true;
                }
                if (IsExcludedTitle(settings.excludeDialogTitlePatterns, title))
                {
                    return true;
                }

                string text = CollectDialogText(hWnd, title);
                bool internalError = ContainsAny(text + " " + title, InternalErrorMarkers);

                ButtonChoice native = FindNativeConfirmButton(hWnd, settings);
                ButtonChoice button = native;
                string source = "native";
                if (button == null)
                {
                    bool keyboardAllowed = settings.enableKeyboardFallback
                                           && ContainsClassName(settings.keyboardFallbackWindowClasses, className)
                                           && (customDialog || standard);
                    if (!keyboardAllowed && standard && internalError)
                    {
                        keyboardAllowed = settings.enableKeyboardFallback;
                    }
                    if (!keyboardAllowed)
                    {
                        return true;
                    }
                    button = new ButtonChoice { Handle = IntPtr.Zero, Text = "(Enter)", Id = 0 };
                    source = "keyboard";
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
                    ButtonId = button.Id,
                    ButtonSource = source,
                    InternalError = internalError,
                    IsStandardDialog = standard,
                    OwnerDisabled = ownerDisabled
                });
                return true;
            }, IntPtr.Zero);

            return results;
        }

        private static bool IsOnScreen(Native.RECT rect)
        {
            return rect.Left > -10000 && rect.Top > -10000 &&
                   rect.Right > rect.Left && rect.Bottom > rect.Top;
        }

        private static bool IsTarget(string processName, string className, string title, HashSet<string> targets)
        {
            if (!string.IsNullOrEmpty(processName) && targets.Contains(processName))
            {
                return true;
            }

            // 进程名取不到（权限不足）时的保守兜底：只认 LabVIEW 自己的窗口类/标题。
            if (string.IsNullOrEmpty(processName))
            {
                if (className.StartsWith("LV", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (title.IndexOf("LabVIEW", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static string StripExeSuffix(string name)
        {
            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                ? name.Substring(0, name.Length - 4)
                : name;
        }

        private static bool ContainsClassName(string[] classes, string className)
        {
            if (classes == null)
            {
                return false;
            }
            foreach (string candidate in classes)
            {
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    string.Equals(candidate.Trim(), className, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool IsExcludedTitle(string[] patterns, string title)
        {
            if (patterns == null || string.IsNullOrEmpty(title))
            {
                return false;
            }
            foreach (string pattern in patterns)
            {
                if (!string.IsNullOrWhiteSpace(pattern) &&
                    title.IndexOf(pattern.Trim(), StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private sealed class ButtonChoice
        {
            public IntPtr Handle;
            public string Text;
            public int Id;
        }

        private static ButtonChoice FindNativeConfirmButton(IntPtr dialog, WatchdogSettings settings)
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
                string candidate = NormalizeButtonText(exclude);
                if (text.Equals(candidate, StringComparison.OrdinalIgnoreCase))
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
                string candidate = NormalizeButtonText(confirm);
                if (text.Equals(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    best = Math.Max(best, 500);
                }
                else if (text.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    best = Math.Max(best, 300);
                }
                else if (text.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    best = Math.Max(best, 100);
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
            foreach (string needle in needles)
            {
                if (haystack.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        // 测试入口：与生产路径共用同一套判定。
        internal static bool IsConfirmText(string text, string[] confirms)
        {
            return ScoreButton(NormalizeButtonText(text), confirms) > 0;
        }

        internal static bool IsExcludedText(string text, string[] excludes)
        {
            return IsExcludedButton(excludes, NormalizeButtonText(text));
        }

        internal static bool HasInternalErrorText(string text)
        {
            return ContainsAny(text ?? string.Empty, InternalErrorMarkers);
        }
    }
}
