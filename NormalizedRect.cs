using System;

namespace SkillbarCapture
{
    public struct NormalizedRect
    {
        public double X;
        public double Y;
        public double Width;
        public double Height;

        public NormalizedRect(double x, double y, double width, double height)
        {
            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        // 旧默认截取区域（相对坐标），保留向后兼容；如需自动拟合，请使用 FitSkillbar。
        public static NormalizedRect DefaultSkillbar =
            new NormalizedRect(0.415, 0.694, 0.171, 0.03);

        /// <summary>
        /// 根据窗口尺寸（可选减去标题栏）拟合技能条 ROI，返回相对整窗的归一化坐标。
        /// titleBarPixels 例如 37（Win10/11 标题栏）。
        /// </summary>
        public static NormalizedRect FitSkillbar(int width, int height, bool removeTitleBar = false, double titleBarPixels = 37.0)
        {
            return SkillbarLocate.Compute(width, height, removeTitleBar, titleBarPixels).NormalizedRect;
        }
    }
}
