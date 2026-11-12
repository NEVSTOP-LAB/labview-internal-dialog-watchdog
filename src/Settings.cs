using System;
using System.Collections.Generic;
using System.IO;
using System.Web.Script.Serialization;

namespace LabVIEWDialogWatchdog
{
    /// <summary>配置模型与默认值。字段名与 JSON 键一一对应（camelCase）。</summary>
    public sealed class WatchdogSettings
    {
        /// <summary>要监控的进程名（不含 .exe），默认 LabVIEW。</summary>
        public string[] targetProcessNames = { "LabVIEW" };

        /// <summary>额外允许的对话框窗口类名；#32770 始终按标准对话框处理。</summary>
        public string[] dialogWindowClasses = { };

        /// <summary>视为“确认/继续”的按钮文本。</summary>
        public string[] confirmButtonTexts = { "OK", "确定", "Continue", "继续", "Close", "关闭" };

        /// <summary>永不点击的按钮文本。</summary>
        public string[] excludeButtonTexts = { "Cancel", "取消", "Stop", "停止", "Abort", "放弃", "No", "否" };

        /// <summary>标题命中这些片段的对话框不处理（默认排除保存/覆盖/删除等危险提示）。</summary>
        public string[] excludeDialogTitlePatterns =
        {
            "保存", "覆盖", "删除", "重命名", "退出", "停止",
            "Save", "Overwrite", "Delete", "Rename", "Exit", "Stop"
        };

        /// <summary>可以在没有原生按钮时按 Enter 的窗口类（LabVIEW 自绘对话框）。</summary>
        public string[] keyboardFallbackWindowClasses = { "LVDChild" };

        /// <summary>对话框出现后等待多久再自动点击（毫秒），默认 5000；用于给值守人员留出自己处理的时间。</summary>
        public int clickDelayMs = 5000;

        /// <summary>是否启用无按钮时的键盘兜底。默认关闭：只有显式开启才会对无按钮自绘框按 Enter。</summary>
        public bool enableKeyboardFallback = false;

        /// <summary>是否允许 WM_CLOSE 作为最后兜底。默认关闭：强关可能破坏调用方状态。</summary>
        public bool enableWindowCloseFallback = false;

        /// <summary>扫描间隔（毫秒）。</summary>
        public int pollIntervalMs = 500;

        /// <summary>同一对话框至少连续出现多少次才点击，避免误触瞬时窗口。</summary>
        public int minSeenCount = 2;

        /// <summary>同一对话框最多尝试点击次数。</summary>
        public int maxClickAttempts = 3;

        /// <summary>两次点击尝试之间的等待（毫秒）。</summary>
        public int clickRetryDelayMs = 900;

        /// <summary>是否只处理“用户没有正在操作 LabVIEW”时出现的对话框（默认开启）。</summary>
        public bool userActivityGuard = true;

        /// <summary>系统最近一次键鼠输入早于该毫秒数，才视为用户已离开（默认 2000ms）。</summary>
        public int userActivityIdleMs = 2000;

        /// <summary>回看多少毫秒内的前台窗口历史，判断对话框出现前用户是否在操作 LabVIEW（默认 3000ms）。</summary>
        public int userActivityWindowMs = 3000;

        /// <summary>启动后立即暂停监控。</summary>
        public bool startPaused = false;

        /// <summary>点击成功后弹出托盘气泡。</summary>
        public bool showNotifications = true;

        /// <summary>只记录不点击，用于观察。</summary>
        public bool dryRun = false;

        /// <summary>日志目录（相对 exe 所在目录或绝对路径）。</summary>
        public string logDirectory = "logs";

        public static string DefaultPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "LabVIEWDialogWatchdog.json"); }
        }

        public static WatchdogSettings Load(string path, out string warning)
        {
            warning = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
            {
                return new WatchdogSettings();
            }

            try
            {
                string json = StripLineComments(File.ReadAllText(path));
                var serializer = new JavaScriptSerializer();
                var settings = serializer.Deserialize<WatchdogSettings>(json) ?? new WatchdogSettings();
                settings.Normalize(out warning);
                return settings;
            }
            catch (Exception ex)
            {
                warning = "读取配置失败，使用默认配置：" + ex.Message;
                return new WatchdogSettings();
            }
        }

        public void Normalize(out string warning)
        {
            warning = null;
            if (targetProcessNames == null || targetProcessNames.Length == 0)
            {
                targetProcessNames = new[] { "LabVIEW" };
            }
            if (confirmButtonTexts == null || confirmButtonTexts.Length == 0)
            {
                confirmButtonTexts = new[] { "OK", "确定" };
            }
            if (excludeButtonTexts == null) excludeButtonTexts = new string[0];
            if (dialogWindowClasses == null) dialogWindowClasses = new string[0];
            if (excludeDialogTitlePatterns == null || excludeDialogTitlePatterns.Length == 0)
            {
                excludeDialogTitlePatterns = new[]
                {
                    "保存", "覆盖", "删除", "重命名", "退出", "停止",
                    "Save", "Overwrite", "Delete", "Rename", "Exit", "Stop"
                };
            }
            if (keyboardFallbackWindowClasses == null || keyboardFallbackWindowClasses.Length == 0)
            {
                keyboardFallbackWindowClasses = new[] { "LVDChild" };
            }
            if (clickDelayMs < 0) clickDelayMs = 0;
            if (clickDelayMs > 60000) clickDelayMs = 60000;
            if (userActivityIdleMs < 0) userActivityIdleMs = 0;
            if (userActivityIdleMs > 60000) userActivityIdleMs = 60000;
            if (userActivityWindowMs < 0) userActivityWindowMs = 0;
            if (userActivityWindowMs > 60000) userActivityWindowMs = 60000;
            if (pollIntervalMs < 100) pollIntervalMs = 100;
            if (pollIntervalMs > 5000) pollIntervalMs = 5000;
            if (minSeenCount < 1) minSeenCount = 1;
            if (minSeenCount > 10) minSeenCount = 10;
            if (maxClickAttempts < 1) maxClickAttempts = 1;
            if (maxClickAttempts > 10) maxClickAttempts = 10;
            if (clickRetryDelayMs < 100) clickRetryDelayMs = 100;
            if (clickRetryDelayMs > 10000) clickRetryDelayMs = 10000;
            if (string.IsNullOrWhiteSpace(logDirectory)) logDirectory = "logs";
        }

        public string ResolveLogDirectory()
        {
            if (Path.IsPathRooted(logDirectory))
            {
                return logDirectory;
            }
            return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, logDirectory);
        }

        /// <summary>忽略整行 // 注释，兼容用户手工编辑的配置。</summary>
        private static string StripLineComments(string json)
        {
            if (string.IsNullOrEmpty(json) || json.IndexOf("//", StringComparison.Ordinal) < 0)
            {
                return json;
            }
            var builder = new System.Text.StringBuilder();
            foreach (string line in json.Replace("\r\n", "\n").Split('\n'))
            {
                string trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }
                builder.Append(line).Append('\n');
            }
            return builder.ToString();
        }

        /// <summary>写出可被本程序直接读取的默认配置。</summary>
        public static string DefaultJson()
        {
            return
@"{
  ""targetProcessNames"": [""LabVIEW""],
  ""dialogWindowClasses"": [],
  ""confirmButtonTexts"": [""OK"", ""确定"", ""Continue"", ""继续"", ""Close"", ""关闭""],
  ""excludeButtonTexts"": [""Cancel"", ""取消"", ""Stop"", ""停止"", ""Abort"", ""放弃"", ""No"", ""否""],
  ""excludeDialogTitlePatterns"": [""保存"", ""覆盖"", ""删除"", ""重命名"", ""退出"", ""停止"", ""Save"", ""Overwrite"", ""Delete"", ""Rename"", ""Exit"", ""Stop""],
  ""keyboardFallbackWindowClasses"": [""LVDChild""],
  ""clickDelayMs"": 5000,
  ""enableKeyboardFallback"": false,
  ""enableWindowCloseFallback"": false,
  ""userActivityGuard"": true,
  ""userActivityIdleMs"": 2000,
  ""userActivityWindowMs"": 3000,
  ""pollIntervalMs"": 500,
  ""minSeenCount"": 2,
  ""maxClickAttempts"": 3,
  ""clickRetryDelayMs"": 900,
  ""startPaused"": false,
  ""showNotifications"": true,
  ""dryRun"": false,
  ""logDirectory"": ""logs""
}";
        }
    }
}
