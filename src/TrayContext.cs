using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using Microsoft.Win32;

namespace LabVIEWDialogWatchdog
{
    internal static class Autostart
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "LabVIEWDialogWatchdog";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKey, false))
                {
                    if (key == null)
                    {
                        return false;
                    }
                    var value = key.GetValue(ValueName) as string;
                    return !string.IsNullOrEmpty(value) &&
                           value.IndexOf(Application.ExecutablePath, StringComparison.OrdinalIgnoreCase) >= 0;
                }
            }
            catch
            {
                return false;
            }
        }

        public static void SetEnabled(bool enabled)
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("无法打开当前用户的启动项注册表键。");
                }
                if (enabled)
                {
                    key.SetValue(ValueName, "\"" + Application.ExecutablePath + "\"", RegistryValueKind.String);
                }
                else
                {
                    key.DeleteValue(ValueName, false);
                }
            }
        }
    }

    /// <summary>托盘宿主：一个 NotifyIcon + 一个轮询 Timer，没有可见窗体。</summary>
    internal sealed class TrayContext : ApplicationContext
    {
        private readonly WatchdogSettings _settings;
        private readonly Log _log;
        private readonly DialogWatchdogEngine _engine;
        private readonly NotifyIcon _notifyIcon;
        private readonly System.Threading.Timer _timer;
        private readonly Control _marshaler;
        private readonly ToolStripMenuItem _statusItem;
        private readonly ToolStripMenuItem _pauseItem;
        private readonly ToolStripMenuItem _autostartItem;
        private readonly Icon _icon;
        private int _tickBusy;
        private bool _disposed;

        public TrayContext(WatchdogSettings settings, Log log)
        {
            _settings = settings;
            _log = log;
            _engine = new DialogWatchdogEngine(settings, log)
            {
                Paused = settings.startPaused,
                DryRun = settings.dryRun
            };
            _engine.Dismissed += OnDismissed;

            _statusItem = new ToolStripMenuItem("状态：正在启动…") { Enabled = false };
            _pauseItem = new ToolStripMenuItem("暂停监控") { CheckOnClick = false };
            _pauseItem.Click += delegate { TogglePause(); };
            _autostartItem = new ToolStripMenuItem("开机自动启动") { CheckOnClick = false, Checked = Autostart.IsEnabled() };
            _autostartItem.Click += delegate { ToggleAutostart(); };

            var menu = new ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_pauseItem);
            menu.Items.Add(new ToolStripMenuItem("打开日志", null, delegate { OpenLog(); }));
            menu.Items.Add(new ToolStripMenuItem("打开配置文件", null, delegate { OpenConfig(); }));
            menu.Items.Add(new ToolStripMenuItem("打开程序目录", null, delegate { OpenProgramFolder(); }));
            menu.Items.Add(_autostartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(new ToolStripMenuItem("关于", null, delegate { ShowAbout(); }));
            menu.Items.Add(new ToolStripMenuItem("退出", null, delegate { ExitThread(); }));

            _icon = IconFactory.Create();
            _notifyIcon = new NotifyIcon
            {
                Icon = _icon,
                Text = "LabVIEW 对话框守护 · 监控中",
                ContextMenuStrip = menu,
                Visible = true
            };
            _notifyIcon.DoubleClick += delegate { OpenLog(); };

            // 扫描与点击放到线程池，托盘 UI 不被 SendMessageTimeout/UIA 阻塞。
            _marshaler = new Control();
            IntPtr forceHandle = _marshaler.Handle;
            _timer = new System.Threading.Timer(OnTick, null, 0, settings.pollIntervalMs);

            UpdateStatus();
            _log.Info(string.Format(
                "守护已启动：进程={0} 间隔={1}ms 暂停={2} 只观察={3}",
                string.Join(",", settings.targetProcessNames),
                settings.pollIntervalMs,
                _engine.Paused,
                _engine.DryRun));

            if (settings.showNotifications)
            {
                _notifyIcon.ShowBalloonTip(2500, "LabVIEW 对话框守护", "监控已启动。", ToolTipIcon.Info);
            }
        }

        private void OnTick(object state)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _tickBusy, 1, 0) != 0)
            {
                return;
            }
            try
            {
                _engine.Tick();
                PostToUi(UpdateStatus);
            }
            catch (Exception ex)
            {
                _log.Error("轮询异常：" + ex);
            }
            finally
            {
                System.Threading.Interlocked.Exchange(ref _tickBusy, 0);
            }
        }

        private void PostToUi(Action action)
        {
            try
            {
                _marshaler.BeginInvoke(action);
            }
            catch
            {
                // 窗口销毁后忽略。
            }
        }

        private void OnDismissed(object sender, DialogDismissedEventArgs e)
        {
            PostToUi(delegate
            {
                UpdateStatus();
                if (!_settings.showNotifications)
                {
                    return;
                }

                string suffix = e.Dialog.InternalError ? "（internal warning）" : string.Empty;
                string title = string.IsNullOrEmpty(e.Dialog.Title) ? "LabVIEW" : e.Dialog.Title;
                if (title.Length > 40)
                {
                    title = title.Substring(0, 40) + "…";
                }
                string message = string.Format("已点击“{0}”关闭对话框{1}：{2}", e.Dialog.ButtonText, suffix, title);
                _notifyIcon.ShowBalloonTip(3000, "LabVIEW 对话框守护", message, ToolTipIcon.Info);
            });
        }

        private void UpdateStatus()
        {
            string state = _engine.Paused ? "已暂停" : (_engine.DryRun ? "观察模式" : "监控中");
            _statusItem.Text = string.Format(
                "状态：{0} · 已处理 {1} 次 · 用户跳过 {2} 次",
                state,
                _engine.DismissedCount,
                _engine.UserSkippedCount);
            _pauseItem.Text = _engine.Paused ? "恢复监控" : "暂停监控";
            _notifyIcon.Text = Truncate("LabVIEW 对话框守护 · " + state, 63);
        }

        private void TogglePause()
        {
            _engine.Paused = !_engine.Paused;
            _log.Info(_engine.Paused ? "监控已暂停。" : "监控已恢复。");
            UpdateStatus();
        }

        private void ToggleAutostart()
        {
            try
            {
                bool enable = !Autostart.IsEnabled();
                Autostart.SetEnabled(enable);
                _autostartItem.Checked = Autostart.IsEnabled();
                _log.Info(enable ? "已设置开机自动启动。" : "已取消开机自动启动。");
            }
            catch (Exception ex)
            {
                _log.Error("设置开机启动失败：" + ex.Message);
                MessageBox.Show(ex.Message, "LabVIEW 对话框守护", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OpenLog()
        {
            try
            {
                if (File.Exists(_log.FilePath))
                {
                    Process.Start(new ProcessStartInfo(_log.FilePath) { UseShellExecute = true });
                }
                else
                {
                    OpenProgramFolder();
                }
            }
            catch (Exception ex)
            {
                _log.Error("打开日志失败：" + ex.Message);
            }
        }

        private void OpenConfig()
        {
            try
            {
                string path = WatchdogSettings.DefaultPath;
                if (!File.Exists(path))
                {
                    File.WriteAllText(path, WatchdogSettings.DefaultJson());
                }
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _log.Error("打开配置失败：" + ex.Message);
            }
        }

        private void OpenProgramFolder()
        {
            try
            {
                Process.Start(new ProcessStartInfo(AppDomain.CurrentDomain.BaseDirectory) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                _log.Error("打开程序目录失败：" + ex.Message);
            }
        }

        private void ShowAbout()
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            string text = string.Format(
                "LabVIEW 对话框守护 {0}\r\n\r\n监控 LabVIEW 进程的模态对话框，自动点击确认按钮，\r\n让 labview-mcp 等自动化调用继续执行。\r\n\r\n日志：{1}\r\n配置：{2}",
                version,
                _log.FilePath,
                WatchdogSettings.DefaultPath);
            MessageBox.Show(text, "关于", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        private static string Truncate(string value, int max)
        {
            return value.Length <= max ? value : value.Substring(0, max);
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                _timer.Dispose();
                _notifyIcon.Visible = false;
                _notifyIcon.Dispose();
                _marshaler.Dispose();
                _icon.Dispose();
                _log.Info("守护已退出。");
            }
            base.Dispose(disposing);
        }
    }
}
