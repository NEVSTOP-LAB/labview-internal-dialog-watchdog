using System;

namespace LabVIEWDialogWatchdog
{
    /// <summary>
    /// 用户操作闸门：只有“用户没有正在操作 LabVIEW”时才自动点击。
    /// 判据：对话框出现前的最近一段时间里 LabVIEW 曾在前台，且系统最近有键鼠输入。
    /// 只看对话框出现前的历史，避免对话框自己抢到前台后被误判成用户操作。
    /// </summary>
    internal static class UserActivityGuard
    {
        internal static bool ShouldSkip(bool enabled, int idleMilliseconds, int idleThresholdMs, bool targetRecentlyForeground)
        {
            if (!enabled)
            {
                return false;
            }
            return targetRecentlyForeground && idleMilliseconds >= 0 && idleMilliseconds < idleThresholdMs;
        }

        internal static int GetIdleMilliseconds()
        {
            var info = new Native.LASTINPUTINFO();
            info.cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.LASTINPUTINFO));
            if (!Native.GetLastInputInfo(ref info))
            {
                return -1;
            }
            // GetLastInputInfo 与 Environment.TickCount 都是 32 位回绕计数，直接做无符号减法。
            uint now = unchecked((uint)Environment.TickCount);
            return unchecked((int)(now - info.dwTime));
        }

        internal static int GetForegroundProcessId()
        {
            IntPtr foreground = Native.GetForegroundWindow();
            if (foreground == IntPtr.Zero)
            {
                return 0;
            }
            uint pid;
            Native.GetWindowThreadProcessId(foreground, out pid);
            return (int)pid;
        }
    }
}
