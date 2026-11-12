using System;
using System.IO;
using System.Runtime.InteropServices;

namespace LabVIEWDialogWatchdog.Tests
{
    /// <summary>测试夹具：模拟 LabVIEW 的 Internal Warning 对话框（以及一个普通对话框用于反向测试）。</summary>
    internal static class DialogFixture
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int MessageBox(IntPtr owner, string text, string caption, uint type);

        private static int Main(string[] args)
        {
            string mode = args.Length > 0 ? args[0] : "--internal-warning";
            string marker = args.Length > 1 ? args[1] : null;

            string text;
            if (string.Equals(mode, "--other", StringComparison.OrdinalIgnoreCase))
            {
                text = "Some unrelated dialog text";
            }
            else
            {
                text = "Internal Warning 0x0A6FE5367 : \"undo.cpp\", line 5385\n"
                       + "LabVIEW version 26.3d539\n"
                       + "For assistance in resolving this problem, please contact National Instruments Support.";
            }

            MessageBox(IntPtr.Zero, text, "错误", 0x00000000);

            if (!string.IsNullOrEmpty(marker))
            {
                File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            }
            return 0;
        }
    }
}
