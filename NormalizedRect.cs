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

        // 默认截取区域（相对坐标）：居中偏下的技能条，约占窗口 24% 宽、5% 高
        public static NormalizedRect DefaultSkillbar =
            new NormalizedRect(0.415, 0.694, 0.171, 0.03);
    }
}
