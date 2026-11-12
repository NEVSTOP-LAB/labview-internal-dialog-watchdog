using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace LabVIEWDialogWatchdog
{
    /// <summary>一个小型追加式日志，超过 1 MB 轮转一次（保留 1 个历史文件）。</summary>
    public sealed class Log
    {
        private const long MaxBytes = 1024 * 1024;
        private readonly object _gate = new object();
        private readonly string _path;
        private readonly bool _echo;

        public Log(string directory, string fileName, bool echo)
        {
            Directory.CreateDirectory(directory);
            _path = Path.Combine(directory, fileName);
            _echo = echo;
        }

        public string FilePath
        {
            get { return _path; }
        }

        public void Info(string message)
        {
            Write("I", message);
        }

        public void Warn(string message)
        {
            Write("W", message);
        }

        public void Error(string message)
        {
            Write("E", message);
        }

        private void Write(string level, string message)
        {
            string line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:yyyy-MM-dd HH:mm:ss.fff} [{1}] {2}",
                DateTime.Now,
                level,
                message);

            lock (_gate)
            {
                try
                {
                    RotateIfNeeded();
                    File.AppendAllText(_path, line + Environment.NewLine);
                }
                catch
                {
                    // 日志写失败不能影响守护逻辑。
                }
            }

            if (_echo)
            {
                Console.WriteLine(line);
            }
        }

        private void RotateIfNeeded()
        {
            try
            {
                var info = new FileInfo(_path);
                if (!info.Exists || info.Length <= MaxBytes)
                {
                    return;
                }
                string previous = _path + ".1";
                if (File.Exists(previous))
                {
                    File.Delete(previous);
                }
                File.Move(_path, previous);
            }
            catch
            {
                // 轮转失败时继续写当前文件。
            }
        }
    }
}
