using System;
using System.IO;
using System.Web.Script.Serialization;

namespace LabVIEWDialogWatchdog
{
    /// <summary>
    /// 配置模型：只关心 LabVIEW 的 Internal Warning/Error 对话框。
    /// 字段名与 JSON 键一一对应。
    /// </summary>
    public sealed class WatchdogSettings
    {
        /// <summary>要监控的进程名（不含 .exe），默认 LabVIEW。</summary>
        public string[] targetProcessNames = { "LabVIEW" };

        /// <summary>对话框文本包含这些片段时视为 Internal Warning/Error。</summary>
        public string[] internalDialogMarkers =
        {
            "internal warning", "internal error", "内部警告", "内部错误"
        };

        /// <summary>视为“确认”的按钮文本（精确匹配）。</summary>
        public string[] confirmButtonTexts = { "OK", "确定", "Continue", "继续", "Close", "关闭" };

        /// <summary>永不点击的按钮文本。</summary>
        public string[] excludeButtonTexts = { "Cancel", "取消", "Stop", "停止", "Abort", "放弃", "No", "否" };

        /// <summary>对话框出现后等待多少秒再点击，默认 5。</summary>
        public int clickDelaySeconds = 5;

        /// <summary>扫描间隔（毫秒）。</summary>
        public int pollIntervalMs = 500;

        /// <summary>同一对话框最多尝试点击次数。</summary>
        public int maxClickAttempts = 3;

        /// <summary>两次点击尝试之间的等待（毫秒）。</summary>
        public int clickRetryDelayMs = 900;

        public bool startPaused = false;
        public bool showNotifications = true;

        /// <summary>只记录不点击。</summary>
        public bool dryRun = false;

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
            if (internalDialogMarkers == null || internalDialogMarkers.Length == 0)
            {
                internalDialogMarkers = new[] { "internal warning", "internal error" };
            }
            if (confirmButtonTexts == null || confirmButtonTexts.Length == 0)
            {
                confirmButtonTexts = new[] { "OK", "确定" };
            }
            if (excludeButtonTexts == null) excludeButtonTexts = new string[0];
            if (clickDelaySeconds < 0) clickDelaySeconds = 0;
            if (clickDelaySeconds > 300) clickDelaySeconds = 300;
            if (pollIntervalMs < 100) pollIntervalMs = 100;
            if (pollIntervalMs > 5000) pollIntervalMs = 5000;
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
                if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }
                builder.Append(line).Append('\n');
            }
            return builder.ToString();
        }

        public static string DefaultJson()
        {
            return
@"{
  ""targetProcessNames"": [""LabVIEW""],
  ""internalDialogMarkers"": [""internal warning"", ""internal error"", ""内部警告"", ""内部错误""],
  ""confirmButtonTexts"": [""OK"", ""确定"", ""Continue"", ""继续"", ""Close"", ""关闭""],
  ""excludeButtonTexts"": [""Cancel"", ""取消"", ""Stop"", ""停止"", ""Abort"", ""放弃"", ""No"", ""否""],
  ""clickDelaySeconds"": 5,
  ""pollIntervalMs"": 500,
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
