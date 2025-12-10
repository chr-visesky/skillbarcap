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

        // 这里先给一个大概位置：底部中间一条长条，你可以后面再微调
        public static NormalizedRect DefaultSkillbar =
            new NormalizedRect(0.30, 0.86, 0.40, 0.07);
    }
}
