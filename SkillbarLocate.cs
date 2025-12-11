using System;
using System.Drawing;

namespace SkillbarCapture
{
    /// <summary>
    /// 负责拟合技能条位置（像素 + 归一化），支持可选标题栏高度。
    /// 数据来自 1~8.png 样本，标题栏 37px，8.png 目标框 (432,141)-(471,145)。
    /// </summary>
    public static class SkillbarLocate
    {
        public const double DefaultTitleBarPixels = 37.0;

        public static SkillbarLocation Compute(int width, int height, bool removeTitleBar = false, double titleBarPixels = DefaultTitleBarPixels)
        {
            if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));

            double title = removeTitleBar ? titleBarPixels : 0.0;
            double contentHeight = height - title;
            if (contentHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(height), "Height must be greater than title bar.");

            double ar = width / contentHeight;

            double wr = Interp(ar, WidthAnchors);
            double hr = Interp(ar, HeightAnchors);
            double cyRatio = Interp(ar, CenterAnchors);

            double cx = 0.5 * width;
            double cy = cyRatio * contentHeight + title;
            double pixelWidth = wr * width;
            double pixelHeight = hr * contentHeight;

            var pixelRect = new RectangleF(
                (float)(cx - pixelWidth / 2.0),
                (float)(cy - pixelHeight / 2.0),
                (float)pixelWidth,
                (float)pixelHeight);

            var normalizedRect = new NormalizedRect(
                (cx - pixelWidth / 2.0) / width,
                (cy - pixelHeight / 2.0) / height,
                pixelWidth / width,
                pixelHeight / height);

            return new SkillbarLocation(pixelRect, normalizedRect, ar);
        }

        public static RectangleF PredictPixelRect(int width, int height, bool removeTitleBar = false, double titleBarPixels = DefaultTitleBarPixels)
            => Compute(width, height, removeTitleBar, titleBarPixels).PixelRect;

        public static NormalizedRect PredictNormalizedRect(int width, int height, bool removeTitleBar = false, double titleBarPixels = DefaultTitleBarPixels)
            => Compute(width, height, removeTitleBar, titleBarPixels).NormalizedRect;

        public readonly struct SkillbarLocation
        {
            public SkillbarLocation(RectangleF pixelRect, NormalizedRect normalizedRect, double aspectRatio)
            {
                PixelRect = pixelRect;
                NormalizedRect = normalizedRect;
                AspectRatio = aspectRatio;
            }

            public RectangleF PixelRect { get; }
            public NormalizedRect NormalizedRect { get; }
            public double AspectRatio { get; }
        }

        private static double Interp(double ar, (double ar, double val)[] anchors)
        {
            if (ar <= anchors[0].ar) return anchors[0].val;
            if (ar >= anchors[^1].ar) return anchors[^1].val;

            for (int i = 0; i < anchors.Length - 1; i++)
            {
                var a = anchors[i];
                var b = anchors[i + 1];
                if (ar >= a.ar && ar <= b.ar)
                {
                    double t = (ar - a.ar) / (b.ar - a.ar);
                    return a.val + t * (b.val - a.val);
                }
            }

            return anchors[^1].val;
        }

        // 锚点：按 ar（宽 / 有效高）升序，来源 1~8.png（标题栏 37px），8.png 拟合到 (432,141)-(471,145)
        private static readonly (double ar, double val)[] WidthAnchors = new[]
        {
            (0.557512953, 0.191449814),
            (0.996794872, 0.194533762),
            (1.404699739, 0.180297398),
            (1.58974359,  0.161290323),
            (1.781181619, 0.121007371),
            (1.82466144,  0.077734375),
            (2.608974359, 0.097665848),
            (5.908496732, 0.043141593),
        };

        private static readonly (double ar, double val)[] HeightAnchors = new[]
        {
            (0.557512953, 0.009326425),
            (0.996794872, 0.017628205),
            (1.404699739, 0.023498695),
            (1.58974359,  0.020833333),
            (1.781181619, 0.019693654),
            (1.82466144,  0.012829651),
            (2.608974359, 0.020833333),
            (5.908496732, 0.026143791),
        };

        private static readonly (double ar, double val)[] CenterAnchors = new[]
        {
            (0.557512953, 0.874093264),
            (0.996794872, 0.774839744),
            (1.404699739, 0.69843342),
            (1.58974359,  0.699519231),
            (1.781181619, 0.74726477),
            (1.82466144,  0.836065574),
            (2.608974359, 0.699519231),
            (5.908496732, 0.692810458),
        };
    }
}
