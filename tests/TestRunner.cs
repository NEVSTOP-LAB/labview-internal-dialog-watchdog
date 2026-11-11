using System;
using System.IO;

namespace LabVIEWDialogWatchdog.Tests
{
    /// <summary>不依赖 LabVIEW 的单元测试；参数 1 为可写临时目录。</summary>
    internal static class TestRunner
    {
        private static int _failures;

        private static int Main(string[] args)
        {
            string tempDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "LabVIEWDialogWatchdog.Tests");
            Directory.CreateDirectory(tempDir);

            var defaults = new WatchdogSettings();
            string normalizeWarning;
            defaults.Normalize(out normalizeWarning);

            Check("默认关闭键盘兜底", !defaults.enableKeyboardFallback);
            Check("默认关闭 WM_CLOSE", !defaults.enableWindowCloseFallback);
            Check("默认 minSeenCount=2", defaults.minSeenCount == 2);
            Check("默认排除保存类标题", IsSaveTitleExcluded(defaults));
            Check("危险按钮文本可识别", WindowScanner.IsDangerousButtonText("关闭而不保存"));

            Check("默认确认文本包含 确定", WindowScanner.IsConfirmText("确定", defaults.confirmButtonTexts));
            Check("默认确认文本包含 OK", WindowScanner.IsConfirmText("OK", defaults.confirmButtonTexts));
            Check("Continue 视为确认", WindowScanner.IsConfirmText("Continue", defaults.confirmButtonTexts));
            Check("Cancel 不是确认文本", !WindowScanner.IsConfirmText("Cancel", defaults.confirmButtonTexts));
            Check("关闭而不保存 不视为确认", !WindowScanner.IsConfirmText("关闭而不保存", defaults.confirmButtonTexts));
            Check("Close without saving 不视为确认", !WindowScanner.IsConfirmText("Close without saving", defaults.confirmButtonTexts));
            Check("危险标题默认被排除", IsSaveTitleExcluded(defaults));
            Check("取消属于排除文本", WindowScanner.IsExcludedText("取消", defaults.excludeButtonTexts));
            Check("确定不属于排除文本", !WindowScanner.IsExcludedText("确定", defaults.excludeButtonTexts));
            Check("识别 internal error", WindowScanner.HasInternalErrorText("LabVIEW internal error: 0x8000"));
            Check("识别 Internal Warning", WindowScanner.HasInternalErrorText("Internal Warning 0xBB613420"));
            Check("识别中文内部错误", WindowScanner.HasInternalErrorText("发生内部错误"));
            Check("普通文本不是 internal error", !WindowScanner.HasInternalErrorText("Save changes?"));

            string configPath = Path.Combine(tempDir, "unit-config.json");
            File.WriteAllText(configPath,
                "{\"targetProcessNames\":[\"LabVIEW\"],\"pollIntervalMs\":50,\"minSeenCount\":0,\"maxClickAttempts\":99}");
            string warning;
            WatchdogSettings loaded = WatchdogSettings.Load(configPath, out warning);
            Check("配置读取无警告", string.IsNullOrEmpty(warning));
            Check("配置读取目标进程", loaded.targetProcessNames[0] == "LabVIEW");
            Check("轮询间隔下限被夹紧", loaded.pollIntervalMs == 100);
            Check("minSeenCount 下限被夹紧", loaded.minSeenCount == 1);
            Check("maxClickAttempts 上限被夹紧", loaded.maxClickAttempts == 10);

            File.WriteAllText(configPath, "{not-json");
            WatchdogSettings broken = WatchdogSettings.Load(configPath, out warning);
            Check("坏配置回退默认值", broken.pollIntervalMs == 500);
            Check("坏配置给出警告", !string.IsNullOrEmpty(warning));

            string defaultPath = Path.Combine(tempDir, "default-config.json");
            File.WriteAllText(defaultPath, WatchdogSettings.DefaultJson());
            WatchdogSettings defaultConfig = WatchdogSettings.Load(defaultPath, out warning);
            Check("默认配置可被解析", string.IsNullOrEmpty(warning));
            Check("默认配置保留键盘兜底", defaultConfig.keyboardFallbackWindowClasses[0] == "LVDChild");
            Check("默认配置延迟正确", defaultConfig.keyboardFallbackDelayMs == 800);

            string commentedPath = Path.Combine(tempDir, "commented-config.json");
            File.WriteAllText(commentedPath,
                "{\n  // 手工注释\n  \"pollIntervalMs\": 700\n}");
            WatchdogSettings commented = WatchdogSettings.Load(commentedPath, out warning);
            Check("整行注释被容忍", string.IsNullOrEmpty(warning) && commented.pollIntervalMs == 700);

            Console.WriteLine(_failures == 0 ? "UNIT PASS" : "UNIT FAIL " + _failures);
            return _failures == 0 ? 0 : 1;
        }

        private static bool IsSaveTitleExcluded(WatchdogSettings settings)
        {
            foreach (string pattern in settings.excludeDialogTitlePatterns)
            {
                if ("保存更改".IndexOf(pattern, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }
            }
            return false;
        }

        private static void Check(string name, bool condition)
        {
            if (condition)
            {
                Console.WriteLine("PASS " + name);
                return;
            }
            _failures++;
            Console.WriteLine("FAIL " + name);
        }
    }
}
