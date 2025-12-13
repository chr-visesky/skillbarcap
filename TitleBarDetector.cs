using System;

namespace SkillbarCapture
{
    /// <summary>
    /// 自动检测 Windows 标准白色标题栏高度（GW2 截图优化）。
    /// 原理：检测屏幕中轴线顶部亮度突变；如果顶部不亮，则视为全屏，返回 0。
    /// </summary>
    public static class TitleBarDetector
    {
        public static int DetectTitleBarHeight(ImageBuffer image)
        {
            if (image == null || image.Width <= 0 || image.Height <= 0) return 0;

            int w = image.Width;
            int h = image.Height;
            int maxScanHeight = Math.Min(h, 60);

            int sampleX = w / 2;
            byte[] data = image.Data;

            // 预检：顶部是否足够亮（y=2..7）
            bool isHeaderBright = true;
            for (int y = 2; y < 8 && y < h; y++)
            {
                if (!IsPixelBright(data, sampleX, y, w))
                {
                    isHeaderBright = false;
                    break;
                }
            }
            if (!isHeaderBright) return 0; // 全屏或无标题栏

            for (int y = 8; y + 1 < maxScanHeight; y++)
            {
                double lumCurrent = GetLuminance(data, sampleX, y, w);
                double lumNext = GetLuminance(data, sampleX, y + 1, w);

                if (lumCurrent < 200) return y; // 亮度掉下去了
                if (Math.Abs(lumCurrent - lumNext) > 20) return y + 1; // 梯度突变
            }

            // 兜底：扫满仍然是亮的，认为没有明确标题栏
            return 0;
        }

        private static bool IsPixelBright(byte[] data, int x, int y, int w)
        {
            return GetLuminance(data, x, y, w) > 210;
        }

        private static double GetLuminance(byte[] data, int x, int y, int w)
        {
            int idx = (y * w + x) * 4;
            byte b = data[idx];
            byte g = data[idx + 1];
            byte r = data[idx + 2];
            return (r + g + b) / 3.0;
        }
    }
}
