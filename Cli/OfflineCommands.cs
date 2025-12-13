using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace SkillbarCapture
{
    internal static class OfflineCommands
    {
        public static void RunMarkCastbar(string[] args)
        {
            string inputFolder = args.Length >= 2 ? args[1] : "screen";
            string outputFolder = args.Length >= 3 ? args[2] : Path.Combine(Directory.GetCurrentDirectory(), "castbar");

            if (!Directory.Exists(inputFolder))
            {
                Console.WriteLine($"目录不存在：{inputFolder}");
                return;
            }

            Directory.CreateDirectory(outputFolder);

            var files = Directory.GetFiles(inputFolder, "*.png", SearchOption.TopDirectoryOnly);
            Array.Sort(files, StringComparer.Ordinal);

            if (files.Length == 0)
            {
                Console.WriteLine("目录下没有 png 文件。");
                return;
            }

            Console.WriteLine($"输入目录：{inputFolder}");
            Console.WriteLine($"输出目录：{outputFolder}");

            foreach (var path in files)
            {
                using var bmp = new Bitmap(path);
                using var bufferBmp = bmp.Clone(new Rectangle(0, 0, bmp.Width, bmp.Height), PixelFormat.Format32bppArgb);
                var buffer = BitmapToImageBuffer(bufferBmp);

                SkillbarLocate.TryDetectHealthOrbRect(buffer, out var orbRect, out var orbYoff);
                SkillbarLocate.TryDetectOrbCvRadius(buffer, orbYoff, out var cvR);
                if (!SkillbarLocate.TryDetectCastbar(buffer, out var castRect, out var yoff))
                {
                    Console.WriteLine($"{Path.GetFileName(path)}: detect failed");
                    continue;
                }

                int midX = bmp.Width / 2;
                using (var g = Graphics.FromImage(bmp))
                using (var penCast = new Pen(Color.Lime, 2f))
                using (var penOrb = new Pen(Color.Red, 2f))
                using (var penGuide = new Pen(Color.Yellow, 1f))
                {
                    g.DrawLine(penGuide, midX, 0, midX, bmp.Height - 1);
                    if (yoff > 0)
                        g.DrawLine(penGuide, 0, yoff, bmp.Width - 1, yoff);
                    if (orbYoff > 0 && orbYoff != yoff)
                        g.DrawLine(penGuide, 0, orbYoff, bmp.Width - 1, orbYoff);

                    var drawRect = castRect.Width > 1 && castRect.Height > 1
                        ? new Rectangle(castRect.Left, castRect.Top, castRect.Width - 1, castRect.Height - 1)
                        : castRect;

                    g.DrawRectangle(penCast, drawRect);

                    if (orbRect.Width > 0 && orbRect.Height > 0)
                    {
                        var drawOrb = new Rectangle(orbRect.Left, orbRect.Top, orbRect.Width - 1, orbRect.Height - 1);
                        g.DrawRectangle(penOrb, drawOrb);
                    }
                }

                string outPath = Path.Combine(outputFolder, Path.GetFileNameWithoutExtension(path) + "_castbar.png");
                bmp.Save(outPath, ImageFormat.Png);

                Console.WriteLine(
                    "{0}: yoff={1}px, cast L={2},T={3},W={4},H={5}; orb yoff={6}, orb L={7},T={8},W={9},H={10}; cvR={11:F2}",
                    Path.GetFileName(path),
                    yoff,
                    castRect.Left,
                    castRect.Top,
                    castRect.Width,
                    castRect.Height,
                    orbYoff,
                    orbRect.Left,
                    orbRect.Top,
                    orbRect.Width,
                    orbRect.Height,
                    cvR);
            }
        }

        private static ImageBuffer BitmapToImageBuffer(Bitmap bmp)
        {
            if (bmp == null) throw new ArgumentNullException(nameof(bmp));

            int w = bmp.Width;
            int h = bmp.Height;
            var rect = new Rectangle(0, 0, w, h);

            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

            try
            {
                int bytesPerPixel = 4;
                byte[] buffer = new byte[w * h * bytesPerPixel];

                for (int y = 0; y < h; y++)
                {
                    IntPtr src = data.Scan0 + y * data.Stride;
                    int dstOffset = y * w * bytesPerPixel;
                    Marshal.Copy(src, buffer, dstOffset, w * bytesPerPixel);
                }

                return new ImageBuffer(w, h, buffer);
            }
            finally
            {
                bmp.UnlockBits(data);
            }
        }
    }
}
