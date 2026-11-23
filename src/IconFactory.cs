using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace LabVIEWInternalDialogWatchdog
{
    internal static class IconFactory
    {
        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr handle);

        public static Icon Create()
        {
            using (var bitmap = new Bitmap(32, 32))
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
                graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                graphics.Clear(Color.Transparent);

                using (var background = new SolidBrush(Color.FromArgb(24, 63, 122)))
                {
                    graphics.FillEllipse(background, 0, 0, 31, 31);
                }
                using (var ring = new Pen(Color.FromArgb(255, 255, 255), 1.6f))
                {
                    graphics.DrawEllipse(ring, 0.8f, 0.8f, 29.4f, 29.4f);
                }
                using (var font = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Pixel))
                using (var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                })
                {
                    graphics.DrawString("LV", font, Brushes.White, new RectangleF(0, 0, 31, 31), format);
                }

                using (var badge = new SolidBrush(Color.FromArgb(40, 167, 69)))
                {
                    graphics.FillEllipse(badge, 18, 18, 13, 13);
                }
                using (var mark = new Pen(Color.White, 2f))
                {
                    graphics.DrawLine(mark, 21, 24.5f, 23.5f, 27f);
                    graphics.DrawLine(mark, 23.5f, 27f, 28f, 21.5f);
                }

                IntPtr handle = bitmap.GetHicon();
                try
                {
                    using (Icon icon = Icon.FromHandle(handle))
                    {
                        return (Icon)icon.Clone();
                    }
                }
                finally
                {
                    DestroyIcon(handle);
                }
            }
        }
    }
}
