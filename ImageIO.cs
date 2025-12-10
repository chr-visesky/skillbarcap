using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SkillbarCapture
{
    public static class ImageIO
    {
        public static void SavePng(ImageBuffer buffer, string path)
        {
            if (buffer == null) throw new ArgumentNullException(nameof(buffer));
            if (path == null) throw new ArgumentNullException(nameof(path));

            using (var bmp = new Bitmap(buffer.Width, buffer.Height, PixelFormat.Format32bppArgb))
            {
                var rect = new Rectangle(0, 0, buffer.Width, buffer.Height);
                var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, bmp.PixelFormat);

                try
                {
                    int bytesPerPixel = 4;
                    for (int y = 0; y < buffer.Height; y++)
                    {
                        IntPtr dest = data.Scan0 + y * data.Stride;
                        int srcOffset = y * buffer.Width * bytesPerPixel;
                        Marshal.Copy(buffer.Data, srcOffset, dest, buffer.Width * bytesPerPixel);
                    }
                }
                finally
                {
                    bmp.UnlockBits(data);
                }

                bmp.Save(path, ImageFormat.Png);
            }
        }
    }
}
