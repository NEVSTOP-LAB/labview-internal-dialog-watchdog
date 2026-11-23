using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;

namespace LabVIEWInternalDialogWatchdog
{
    internal sealed class CliOptions
    {
        public bool Scan;
        public bool Json;
        public string ConfigPath;
        public int WatchSeconds;
        public bool DryRun;
        public bool NoSingleInstance;
        public bool ShowHelp;
        public bool ShowVersion;
        public bool RequireDismissal;
        public string OutPath;
        public bool ExitAfterDismiss;

        public static CliOptions Parse(string[] args)
        {
            var options = new CliOptions();
            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg.ToLowerInvariant())
                {
                    case "--scan":
                        options.Scan = true;
                        break;
                    case "--json":
                        options.Json = true;
                        break;
                    case "--config":
                        if (i + 1 < args.Length)
                        {
                            options.ConfigPath = args[++i];
                        }
                        break;
                    case "--out":
                        if (i + 1 < args.Length)
                        {
                            options.OutPath = args[++i];
                        }
                        break;
                    case "--watch-seconds":
                        if (i + 1 < args.Length)
                        {
                            int seconds;
                            if (int.TryParse(args[++i], out seconds))
                            {
                                options.WatchSeconds = seconds;
                            }
                        }
                        break;
                    case "--dry-run":
                        options.DryRun = true;
                        break;
                    case "--no-single-instance":
                        options.NoSingleInstance = true;
                        break;
                    case "--require-dismissal":
                        options.RequireDismissal = true;
                        break;
                    case "--exit-after-dismiss":
                        options.ExitAfterDismiss = true;
                        break;
                    case "--help":
                    case "-h":
                    case "/?":
                        options.ShowHelp = true;
                        break;
                    case "--version":
                        options.ShowVersion = true;
                        break;
                }
            }
            return options;
        }
    }

    internal static class Program
    {
        private const string AppTitle = "LabVIEW 对话框守护";

        [STAThread]
        private static int Main(string[] args)
        {
            CliOptions options = CliOptions.Parse(args);
            if (options.ShowHelp)
            {
                PrintHelp();
                return 0;
            }
            if (options.ShowVersion)
            {
                WriteConsole(AppTitle + " " + typeof(Program).Assembly.GetName().Version);
                return 0;
            }

            string warning;
            WatchdogSettings settings = WatchdogSettings.Load(
                string.IsNullOrEmpty(options.ConfigPath) ? WatchdogSettings.DefaultPath : options.ConfigPath,
                out warning);
            if (options.DryRun)
            {
                settings.dryRun = true;
            }

            Log log = CreateLog(settings, options.Scan || options.WatchSeconds > 0);
            if (log == null)
            {
                return 1;
            }
            if (!string.IsNullOrEmpty(warning))
            {
                log.Warn(warning);
            }

            if (options.Scan)
            {
                return RunScan(settings, options);
            }
            if (options.WatchSeconds > 0)
            {
                return RunHeadless(settings, log, options);
            }
            return RunTray(settings, log, options);
        }

        private static Log CreateLog(WatchdogSettings settings, bool echo)
        {
            try
            {
                return new Log(settings.ResolveLogDirectory(), "LabVIEWInternalDialogWatchdog.log", echo);
            }
            catch
            {
                // 配置的日志目录不可写时退回临时目录。
            }
            try
            {
                string fallback = Path.Combine(Path.GetTempPath(), "LabVIEWInternalDialogWatchdog");
                return new Log(fallback, "LabVIEWInternalDialogWatchdog.log", echo);
            }
            catch
            {
                MessageBox.Show(
                    "无法创建日志目录，守护进程无法启动。请检查 logDirectory 配置或程序目录权限。",
                    AppTitle,
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Error);
                return null;
            }
        }

        private static int RunScan(WatchdogSettings settings, CliOptions options)
        {
            List<DialogInfo> dialogs = WindowScanner.FindDialogs(settings);
            var payload = new List<object>();
            foreach (DialogInfo dialog in dialogs)
            {
                payload.Add(new
                {
                    handle = "0x" + dialog.Handle.ToInt64().ToString("X"),
                    pid = dialog.ProcessId,
                    process = dialog.ProcessName,
                    className = dialog.ClassName,
                    title = dialog.Title,
                    text = dialog.Text,
                    button = dialog.ButtonText,
                    buttonId = dialog.ButtonId
                });
            }

            string json = new JavaScriptSerializer().Serialize(payload);
            WriteJson(options.OutPath, json);
            WriteConsole(json);
            return 0;
        }

        private static void WriteJson(string path, string json)
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }
            try
            {
                string directory = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllText(path, json, new UTF8Encoding(false));
            }
            catch
            {
                // 输出文件写失败时仍保留控制台输出。
            }
        }

        private static void WriteConsole(string text)
        {
            try
            {
                Console.OutputEncoding = Encoding.UTF8;
            }
            catch
            {
                // GUI 子系统没有控制台句柄时忽略。
            }
            Console.WriteLine(text);
        }

        private static int RunHeadless(WatchdogSettings settings, Log log, CliOptions options)
        {
            var engine = new DialogWatchdogEngine(settings, log)
            {
                Paused = settings.startPaused,
                DryRun = options.DryRun || settings.dryRun
            };
            var lines = new List<string>();
            engine.Dismissed += delegate(object sender, DialogDismissedEventArgs e)
            {
                string line = "DISMISSED " + DialogWatchdogEngine.Describe(string.Empty, e.Dialog);
                lines.Add(line);
                Console.WriteLine(line);
            };

            DateTime deadline = DateTime.UtcNow.AddSeconds(options.WatchSeconds);
            while (DateTime.UtcNow < deadline)
            {
                engine.Tick();
                if (options.ExitAfterDismiss && engine.DismissedCount > 0)
                {
                    break;
                }
                Thread.Sleep(settings.pollIntervalMs);
            }

            string summary = string.Format(
                "SUMMARY detected={0} dismissed={1} failed={2}",
                engine.DetectedCount,
                engine.DismissedCount,
                engine.FailedCount);
            lines.Add(summary);
            Console.WriteLine(summary);
            if (!string.IsNullOrEmpty(options.OutPath))
            {
                WriteJson(options.OutPath, string.Join(Environment.NewLine + Environment.NewLine, lines));
            }

            if (options.RequireDismissal && engine.DismissedCount == 0)
            {
                return 2;
            }
            return 0;
        }

        private static int RunTray(WatchdogSettings settings, Log log, CliOptions options)
        {
            Mutex mutex = null;
            bool ownsMutex = false;
            if (!options.NoSingleInstance)
            {
                mutex = new Mutex(false, @"Local\LabVIEWInternalDialogWatchdog");
                try
                {
                    ownsMutex = mutex.WaitOne(0, false);
                }
                catch (AbandonedMutexException)
                {
                    ownsMutex = true;
                }
                catch (UnauthorizedAccessException)
                {
                    log.Warn("单实例互斥量不可用（权限不足），继续启动。");
                    mutex.Dispose();
                    mutex = null;
                    ownsMutex = true;
                }
                if (!ownsMutex)
                {
                    // 开机自启/重复启动时静默退出，避免无人值守挂一个模态框。
                    log.Info("已有实例在运行，本次启动退出。");
                    mutex.Dispose();
                    return 0;
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.ThreadException += delegate(object sender, System.Threading.ThreadExceptionEventArgs e)
            {
                log.Error("UI 线程异常：" + e.Exception);
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object sender, UnhandledExceptionEventArgs e)
            {
                log.Error("未处理异常：" + e.ExceptionObject);
            };

            try
            {
                Application.Run(new TrayContext(settings, log));
            }
            finally
            {
                if (ownsMutex && mutex != null)
                {
                    mutex.ReleaseMutex();
                }
                if (mutex != null)
                {
                    mutex.Dispose();
                }
            }
            return 0;
        }

        private static void PrintHelp()
        {
            WriteConsole(
                AppTitle + Environment.NewLine +
                "用法：LabVIEWInternalDialogWatchdog.exe [选项]" + Environment.NewLine +
                Environment.NewLine +
                "  （无参数）              以托盘方式运行" + Environment.NewLine +
                "  --scan [--json]         扫描一次当前对话框并输出，然后退出" + Environment.NewLine +
                "  --watch-seconds <n>     无界面运行 n 秒，用于自动化测试" + Environment.NewLine +
                "  --config <path>         指定配置文件" + Environment.NewLine +
                "  --out <path>            把扫描结果/运行摘要写入文件（GUI 子系统下更可靠）" + Environment.NewLine +
                "  --dry-run               只记录不点击" + Environment.NewLine +
                "  --require-dismissal     --watch-seconds 结束时若无成功关闭则返回 2" + Environment.NewLine +
                "  --exit-after-dismiss    成功关闭一个对话框后立即结束 --watch-seconds" + Environment.NewLine +
                "  --no-single-instance    跳过单实例检查（测试用）" + Environment.NewLine +
                "  --version               输出版本");
        }
    }
}
