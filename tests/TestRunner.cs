using System;
using System.Collections.Generic;
using System.IO;

namespace LabVIEWInternalDialogWatchdog.Tests
{
    /// <summary>不依赖 LabVIEW 的单元测试；参数 1 为可写临时目录。</summary>
    internal static class TestRunner
    {
        private static int _failures;

        private static int Main(string[] args)
        {
            string tempDir = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "LabVIEWInternalDialogWatchdog.Tests");
            Directory.CreateDirectory(tempDir);

            var defaults = new WatchdogSettings();
            string normalizeWarning;
            defaults.Normalize(out normalizeWarning);

            Check("默认 1 秒后点击", defaults.clickDelaySeconds == 1);
            Check("默认只监控 LabVIEW", defaults.targetProcessNames[0] == "LabVIEW");
            Check("识别 Internal Warning", WindowScanner.HasInternalMarker(
                "Internal Warning 0x0A6FE5367 : \"undo.cpp\"", defaults.internalDialogMarkers));
            Check("识别 Internal Error", WindowScanner.HasInternalMarker(
                "LabVIEW Internal Error 0x8000", defaults.internalDialogMarkers));
            Check("识别中文内部警告", WindowScanner.HasInternalMarker("内部警告", defaults.internalDialogMarkers));
            Check("普通对话框不匹配", !WindowScanner.HasInternalMarker("Some unrelated dialog text", defaults.internalDialogMarkers));

            Check("确定视为确认", WindowScanner.IsConfirmText("确定", defaults.confirmButtonTexts));
            Check("OK 视为确认", WindowScanner.IsConfirmText("OK", defaults.confirmButtonTexts));
            Check("Cancel 不是确认", !WindowScanner.IsConfirmText("Cancel", defaults.confirmButtonTexts));
            Check("关闭而不保存不视为确认", !WindowScanner.IsConfirmText("关闭而不保存", defaults.confirmButtonTexts));
            Check("危险按钮文本可识别", WindowScanner.IsDangerousButtonText("关闭而不保存"));
            Check("取消属于排除文本", WindowScanner.IsExcludedText("取消", defaults.excludeButtonTexts));

            string configPath = Path.Combine(tempDir, "unit-config.json");
            File.WriteAllText(configPath,
                "{\"targetProcessNames\":[\"LabVIEW\"],\"clickDelaySeconds\":999,\"pollIntervalMs\":50,\"maxClickAttempts\":99}");
            string warning;
            WatchdogSettings loaded = WatchdogSettings.Load(configPath, out warning);
            Check("配置读取无警告", string.IsNullOrEmpty(warning));
            Check("延迟上限被夹紧", loaded.clickDelaySeconds == 300);
            Check("轮询间隔下限被夹紧", loaded.pollIntervalMs == 100);
            Check("最大尝试次数上限被夹紧", loaded.maxClickAttempts == 10);

            File.WriteAllText(configPath, "{not-json");
            WatchdogSettings broken = WatchdogSettings.Load(configPath, out warning);
            Check("坏配置回退默认值", broken.clickDelaySeconds == 1);
            Check("坏配置给出警告", !string.IsNullOrEmpty(warning));

            string defaultPath = Path.Combine(tempDir, "default-config.json");
            File.WriteAllText(defaultPath, WatchdogSettings.DefaultJson());
            WatchdogSettings defaultConfig = WatchdogSettings.Load(defaultPath, out warning);
            Check("默认配置可被解析", string.IsNullOrEmpty(warning));
            Check("默认配置保留 1 秒", defaultConfig.clickDelaySeconds == 1);

            string commentedPath = Path.Combine(tempDir, "commented-config.json");
            File.WriteAllText(commentedPath, "{\n  // 手工注释\n  \"clickDelaySeconds\": 7\n}");
            WatchdogSettings commented = WatchdogSettings.Load(commentedPath, out warning);
            Check("整行注释被容忍", string.IsNullOrEmpty(warning) && commented.clickDelaySeconds == 7);

            Console.WriteLine(_failures == 0 ? "UNIT PASS" : "UNIT FAIL " + _failures);
            return _failures == 0 ? 0 : 1;
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

