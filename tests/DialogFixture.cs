using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LabVIEWDialogWatchdog.Tests
{
    /// <summary>
    /// 测试夹具：模拟被监控进程弹出的模态对话框。
    /// --messagebox / --internal-error 用 Win32 MessageBox（#32770）；
    /// --custom 用带 owner 的 WinForms 模态窗体，覆盖非 #32770 路径；
    /// --labview-custom 用原生 Win32 的 LVDChild 窗口，覆盖“无子按钮、只能按 Enter 关闭”。
    /// </summary>
    internal static class DialogFixture
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);

        [STAThread]
        private static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0] : "--messagebox";
            string marker = args.Length > 1 ? args[1] : null;

            if (string.Equals(mode, "--custom", StringComparison.OrdinalIgnoreCase))
            {
                ShowCustomDialog();
            }
            else if (string.Equals(mode, "--labview-custom", StringComparison.OrdinalIgnoreCase))
            {
                RawDialogFixture.Show();
            }
            else
            {
                bool internalError = string.Equals(mode, "--internal-error", StringComparison.OrdinalIgnoreCase);
                string text = internalError
                    ? "LabVIEW internal error: 0x8000 test fixture"
                    : "LabVIEW test fixture dialog";
                MessageBox(IntPtr.Zero, text, "LabVIEW", 0x00000000);
            }

            if (!string.IsNullOrEmpty(marker))
            {
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            }
            return 0;
        }

        private static void ShowCustomDialog()
        {
            Application.EnableVisualStyles();
            using (var owner = new Form
            {
                Text = "Fixture owner",
                ShowInTaskbar = false,
                WindowState = FormWindowState.Minimized,
                Opacity = 0
            })
            {
                owner.Show();
                using (var dialog = new Form
                {
                    Text = "LabVIEW Internal Error",
                    FormBorderStyle = FormBorderStyle.FixedDialog,
                    StartPosition = FormStartPosition.CenterScreen,
                    ClientSize = new Size(460, 150),
                    TopMost = true,
                    MinimizeBox = false,
                    MaximizeBox = false
                })
                {
                    dialog.Controls.Add(new Label
                    {
                        Text = "LabVIEW internal error (custom dialog fixture)",
                        AutoSize = true,
                        Location = new Point(16, 22)
                    });
                    var ok = new Button
                    {
                        Text = "确定",
                        DialogResult = DialogResult.OK,
                        Location = new Point(350, 95),
                        Size = new Size(90, 30)
                    };
                    dialog.Controls.Add(ok);
                    dialog.AcceptButton = ok;
                    dialog.ShowDialog(owner);
                }
                owner.Close();
            }
        }

        /// <summary>
        /// 原生 Win32 的 LabVIEW 式对话框：类名 LVDChild（注册失败时用 LVDChildWatchdogFixture），
        /// 没有子按钮，收到 Enter（WM_KEYDOWN VK_RETURN）或 WM_CLOSE 才关闭。
        /// </summary>
        private static class RawDialogFixture
        {
            private const int WS_OVERLAPPED = 0x00000000;
            private const int WS_POPUP = unchecked((int)0x80000000);
            private const int WS_CAPTION = 0x00C00000;
            private const int WS_SYSMENU = 0x00080000;
            private const int WS_DLGFRAME = 0x00400000;
            private const int SW_SHOWNOACTIVATE = 4;
            private const int SW_SHOW = 5;
            private const int WM_KEYDOWN = 0x0100;
            private const int WM_CLOSE = 0x0010;
            private const int WM_DESTROY = 0x0002;
            private const int VK_RETURN = 0x0D;

            [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
            private struct WNDCLASSEX
            {
                public uint cbSize;
                public uint style;
                public IntPtr lpfnWndProc;
                public int cbClsExtra;
                public int cbWndExtra;
                public IntPtr hInstance;
                public IntPtr hIcon;
                public IntPtr hCursor;
                public IntPtr hbrBackground;
                public string lpszMenuName;
                public string lpszClassName;
                public IntPtr hIconSm;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct MSG
            {
                public IntPtr hwnd;
                public uint message;
                public IntPtr wParam;
                public IntPtr lParam;
                public uint time;
                public int x;
                public int y;
            }

            private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

            [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
            private static extern IntPtr CreateWindowEx(
                int exStyle, string className, string windowName, int style,
                int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

            [DllImport("user32.dll")]
            private static extern bool ShowWindow(IntPtr hWnd, int command);

            [DllImport("user32.dll")]
            private static extern bool EnableWindow(IntPtr hWnd, bool enable);

            [DllImport("user32.dll")]
            private static extern bool DestroyWindow(IntPtr hWnd);

            [DllImport("user32.dll")]
            private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

            [DllImport("user32.dll")]
            private static extern int GetMessage(out MSG message, IntPtr hWnd, uint min, uint max);

            [DllImport("user32.dll")]
            private static extern bool TranslateMessage(ref MSG message);

            [DllImport("user32.dll")]
            private static extern IntPtr DispatchMessage(ref MSG message);

            [DllImport("user32.dll")]
            private static extern void PostQuitMessage(int exitCode);

            [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
            private static extern IntPtr GetModuleHandle(string moduleName);

            private static WndProcDelegate _wndProcDelegate;

            public static void Show()
            {
                IntPtr instance = GetModuleHandle(null);
                _wndProcDelegate = WndProc;
                IntPtr procedure = Marshal.GetFunctionPointerForDelegate(_wndProcDelegate);

                string dialogClass = Register("LVDChild", procedure, instance) ? "LVDChild" : "LVDChildWatchdogFixture";
                if (dialogClass != "LVDChild")
                {
                    Register("LVDChildWatchdogFixture", procedure, instance);
                }
                Register("FixtureOwnerWindow", procedure, instance);

                IntPtr owner = CreateWindowEx(0, "FixtureOwnerWindow", "Fixture owner", WS_OVERLAPPED,
                    0, 0, 200, 100, IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
                ShowWindow(owner, SW_SHOWNOACTIVATE);
                EnableWindow(owner, false);

                int style = WS_POPUP | WS_CAPTION | WS_SYSMENU | WS_DLGFRAME;
                IntPtr dialog = CreateWindowEx(0, dialogClass, string.Empty, style,
                    500, 300, 247, 116, owner, IntPtr.Zero, instance, IntPtr.Zero);
                ShowWindow(dialog, SW_SHOW);

                MSG message;
                while (GetMessage(out message, IntPtr.Zero, 0, 0) > 0)
                {
                    TranslateMessage(ref message);
                    DispatchMessage(ref message);
                }
            }

            private static bool Register(string className, IntPtr procedure, IntPtr instance)
            {
                var windowClass = new WNDCLASSEX
                {
                    cbSize = (uint)Marshal.SizeOf(typeof(WNDCLASSEX)),
                    style = 0,
                    lpfnWndProc = procedure,
                    hInstance = instance,
                    hbrBackground = IntPtr.Zero,
                    lpszClassName = className
                };
                return RegisterClassEx(ref windowClass) != 0;
            }

            private static IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
            {
                if (message == WM_KEYDOWN && wParam == (IntPtr)VK_RETURN)
                {
                    DestroyWindow(hWnd);
                    return IntPtr.Zero;
                }
                if (message == WM_CLOSE)
                {
                    DestroyWindow(hWnd);
                    return IntPtr.Zero;
                }
                if (message == WM_DESTROY)
                {
                    PostQuitMessage(0);
                    return IntPtr.Zero;
                }
                return DefWindowProc(hWnd, message, wParam, lParam);
            }
        }
    }
}
