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

        // 默认截取区域：底部中间的进度条（按截图位置预估，可再微调）
        public static NormalizedRect DefaultSkillbar =
            new NormalizedRect(0.39, 0.68, 0.24, 0.05);
    }
}
