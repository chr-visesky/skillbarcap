using System;
using System.Collections.Generic;
using System.Drawing;
using OpenCvSharp;
using System.Runtime.InteropServices;

namespace SkillbarCapture
{
    /// <summary>
    /// 施法进度条定位（OpenCV 红球 + 比例推算）。
    /// - 红球：红色优势度阈值 + 形态学 + 连通域，兼容中央数字，强制居中于屏幕中轴且靠底。
    /// - 施法条：宽=3.85R，高=0.26R，顶=球底-4.40R；最后左右上下各外扩1px。
    /// - 标题栏：自动检测；输出坐标包含标题栏偏移。
    /// </summary>
    public static class SkillbarLocate
    {
        public const double OrbGapA = 0.29;
        public const double OrbGapB = 1.0;
        public const double CastbarWidthRatio = 3.85;
        public const double CastbarTopFromOrbBottomRatio = 4.40;
        public const double CastbarHeightRatio = 0.26;

        // 几何拟合系数（根据用户标注回归）
        private const double MarginBottomFactor = 0.32;   // 红圈离底边的距离系数（乘以半径）
        private const double GapFactor = 2.13;            // 红圈顶到施法条底的距离系数（乘以半径）
        private const double BarHeightFactor = 0.23;      // 施法条高度（乘以半径）
        private const double BarWidthFactor = 3.82;       // 施法条宽度（乘以半径）
        private const double ScaleHFactor = 0.125;        // 由屏幕高度推半径的缩放
        private const double ScaleWFactor = 0.10;         // 由屏幕宽度推半径的缩放
        private const double MaxDiameter = 101.0;         // 红圈直径上限
        private const double MinDiameter = 20.0;          // 红圈直径下限（防止过小）

        public static bool TryDetectCastbar(ImageBuffer image, out Rectangle castbarPixelRect, out int titlebarOffset)
        {
            if (image == null) throw new ArgumentNullException(nameof(image));

            castbarPixelRect = default;
            titlebarOffset = 0;

            int w = image.Width;
            int h = image.Height;
            if (w <= 0 || h <= 0) return false;

            // 在线截图通常不含标题栏；离线检测则尝试检测标题栏高度。
            titlebarOffset = TitleBarDetector.DetectTitleBarHeight(image);
            ComputeAnchorBoxes(w, h, titlebarOffset, null, out var _, out var castBox);
            castbarPixelRect = ExpandAndClamp(castBox, 1, 1, w, h);
            return castbarPixelRect.Width > 0 && castbarPixelRect.Height > 0;
        }

        public static bool TryDetectHealthOrbRect(ImageBuffer image, out Rectangle orbRect, out int titlebarOffset)
        {
            orbRect = default;
            titlebarOffset = 0;
            if (image == null) return false;

            int w = image.Width;
            int h = image.Height;
            if (w <= 0 || h <= 0) return false;

            titlebarOffset = TitleBarDetector.DetectTitleBarHeight(image);
            ComputeAnchorBoxes(w, h, titlebarOffset, null, out var orbBox, out _);
            orbRect = orbBox;
            return true;
        }

        private readonly struct OrbDetection
        {
            public int ClientHeight { get; }
            public double Radius { get; }
            public double Gap { get; }
            public double OrbBottom { get; }
            public double OrbCenterY { get; }
            public double VerticalSum { get; }

            public OrbDetection(int clientHeight, double radius, double gap, double orbBottom, double orbCenterY, double vSum)
            {
                ClientHeight = clientHeight;
                Radius = radius;
                Gap = gap;
                OrbBottom = orbBottom;
                OrbCenterY = orbCenterY;
                VerticalSum = vSum;
            }
        }

        // 调试用：仅返回 CV 检测到的半径（若失败返回 false）
        public static bool TryDetectOrbCvRadius(ImageBuffer image, int yoff, out double radius)
        {
            radius = 0;
            if (TryDetectOrbByComponentCv(image, yoff, out var orb, out _))
            {
                radius = orb.Radius;
                return true;
            }
            return false;
        }

        private static bool TryDetectOrbByComponentCv(ImageBuffer image, int yoff, out OrbDetection orb, out Rectangle orbRect)
        {
            orb = default;
            orbRect = default;

            int Hc = image.Height - yoff;
            int W = image.Width;
            if (Hc <= 0 || W <= 0) return false;

            int xStart = (int)(W * 0.30);
            int xEnd = (int)(W * 0.70);
            int yStart = yoff + (int)(Hc * 0.65);
            int yEnd = image.Height;
            if (xEnd <= xStart || yEnd <= yStart)
                return false;

            GCHandle handle = GCHandle.Alloc(image.Data, GCHandleType.Pinned);
            try
            {
                using var mat = Mat.FromPixelData(image.Height, image.Width, MatType.CV_8UC4, handle.AddrOfPinnedObject());
                Cv2.CvtColor(mat, mat, ColorConversionCodes.BGRA2BGR);
                Cv2.Split(mat, out var ch);
                using var b = ch[0];
                using var g = ch[1];
                using var r = ch[2];
                using var hsv = new Mat();
                Cv2.CvtColor(mat, hsv, ColorConversionCodes.BGR2HSV);
                using var maxBG = new Mat();
                Cv2.Max(b, g, maxBG);
                using var redAdv = new Mat();
                Cv2.Subtract(r, maxBG, redAdv);

                var roi = new Rect(xStart, yStart, xEnd - xStart, yEnd - yStart);
                double thr = ComputeRedAdvThresholdCv(redAdv, roi, 90.0, 25.0);
                if (thr <= 0) return false;

                using var maskAdv = new Mat();
                Cv2.Threshold(redAdv, maskAdv, thr, 255, ThresholdTypes.Binary);

                Scalar lower1 = new Scalar(0, 70, 40);
                Scalar upper1 = new Scalar(20, 255, 255);
                Scalar lower2 = new Scalar(160, 70, 40);
                Scalar upper2 = new Scalar(180, 255, 255);
                using var maskHue1 = new Mat();
                using var maskHue2 = new Mat();
                Cv2.InRange(hsv, lower1, upper1, maskHue1);
                Cv2.InRange(hsv, lower2, upper2, maskHue2);

                using var mask = new Mat();
                Cv2.BitwiseOr(maskAdv, maskHue1, mask);
                Cv2.BitwiseOr(mask, maskHue2, mask);

                using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(7, 7));
                Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
                using var kernelDilate = Cv2.GetStructuringElement(MorphShapes.Rect, new OpenCvSharp.Size(3, 3));
                Cv2.Dilate(mask, mask, kernelDilate);

                using var labels = new Mat();
                using var stats = new Mat();
                using var centroids = new Mat();
                Cv2.ConnectedComponentsWithStats(mask, labels, stats, centroids);

                double? bestScore = null;
                Rect best = default;
                for (int i = 1; i < stats.Rows; i++)
                {
                    int area = stats.Get<int>(i, (int)ConnectedComponentsTypes.Area);
                    if (area < 80) continue;

                    int left = stats.Get<int>(i, (int)ConnectedComponentsTypes.Left);
                    int top = stats.Get<int>(i, (int)ConnectedComponentsTypes.Top);
                    int width = stats.Get<int>(i, (int)ConnectedComponentsTypes.Width);
                    int height = stats.Get<int>(i, (int)ConnectedComponentsTypes.Height);
                    double cx = centroids.Get<double>(i, 0);
                    double bottom = top + height;
                    double cy = top + height * 0.5;

                    double aspect = width / (double)Math.Max(1, height);
                    if (aspect < 0.6 || aspect > 1.4) continue;

                    if (bottom < yoff + Hc * 0.80) continue; // 必须在下方

                    double score = area
                        - Math.Abs(cx - W / 2.0) * 0.5
                        + bottom * 0.5
                        - Math.Abs(aspect - 1.0) * 200.0
                        - Math.Abs(cy - (yoff + Hc * 0.92)) * 0.2;

                    if (bestScore == null || score > bestScore)
                    {
                        bestScore = score;
                        best = new Rect(left, top, width, height);
                    }
                }

                if (bestScore == null)
                    return false;

                double R = Math.Max(best.Width, best.Height) / 2.0 * 1.6;
                double minR = Math.Max(1.0, Hc * 0.03);
                double maxR = Math.Max(minR + 1.0, Hc * 0.11);
                if (R < minR) R = minR;
                if (R > maxR) R = maxR;

                double orbBottom = Hc - (OrbGapA * R + OrbGapB);
                if (orbBottom > Hc) orbBottom = Hc;
                double orbCy = orbBottom - R;
                double cxAligned = W / 2.0;

                orb = new OrbDetection(Hc, R, OrbGapA * R + OrbGapB, orbBottom, orbCy, best.Width * best.Height);
                orbRect = ClampRect(
                    (int)Math.Floor(cxAligned - R),
                    (int)Math.Floor(orbCy + yoff - R),
                    (int)Math.Ceiling(cxAligned + R),
                    (int)Math.Ceiling(orbCy + yoff + R),
                    image.Width,
                    image.Height);
                return true;
            }
            finally
            {
                if (handle.IsAllocated) handle.Free();
            }
        }

        // 垂直/水平扫描回退（gap bridging）
        /// <summary>
        /// 以屏幕几何模型推算红圈/施法条矩形（优先使用检测到的半径，若无则用缩放推算）。
        /// 所有计算在 client 坐标系（扣除标题栏），最后再加回 yoff。
        /// </summary>
        private static void ComputeAnchorBoxes(int imgW, int imgH, int yoff, double? detectedR, out Rectangle orbBox, out Rectangle castBox)
        {
            double clientH = Math.Max(1, imgH - yoff);
            double clientW = imgW;

            double scaleH = imgH; // 不再补标题栏，按传入尺寸直接计算
            double scaleW = imgW;

            double rawDiameter = Math.Min(scaleH * ScaleHFactor, scaleW * ScaleWFactor);
            double diameter = Math.Floor(rawDiameter); // 向下取整模拟引擎

            // 低高度补丁：小窗口时额外减小直径
            if (scaleH < 600) diameter -= 1.0;
            if (scaleH < 250) diameter -= 2.0;
            diameter = Math.Min(diameter, MaxDiameter);
            diameter = Math.Max(diameter, MinDiameter);
            double targetR = diameter / 2.0;

            double R = targetR;
            if (detectedR.HasValue)
            {
                double minR = Math.Max(MinDiameter / 2.0, targetR * 0.7);
                double maxR = Math.Max(minR + 1.0, targetR * 1.3);
                R = Math.Max(minR, Math.Min(maxR, detectedR.Value));
            }

            double orbBottomClient = clientH - R * MarginBottomFactor;
            double orbTopClient = orbBottomClient - 2.0 * R;
            double orbCenterClientY = orbTopClient + R;
            double cx = clientW / 2.0;

            // castbar
            double castBottom = orbTopClient - GapFactor * R;
            double castTop = castBottom - BarHeightFactor * R;
            double castW = BarWidthFactor * R;

            orbBox = ClampRect(
                (int)Math.Floor(cx - R),
                (int)Math.Floor(orbCenterClientY - R + yoff),
                (int)Math.Ceiling(cx + R),
                (int)Math.Ceiling(orbCenterClientY + R + yoff),
                imgW, imgH);

            castBox = ClampRect(
                (int)Math.Floor(cx - castW / 2.0),
                (int)Math.Floor(castTop + yoff - 1), // 向上抬 1px，高度随之 +1
                (int)Math.Ceiling(cx + castW / 2.0),
                (int)Math.Ceiling(castBottom + yoff + 1), // 右下角再下移 1px
                imgW, imgH);
        }

        private readonly struct Run1d
        {
            public int Start { get; }
            public int EndInclusive { get; }
            public double Sum { get; }
            public Run1d(int s, int e, double sum) { Start = s; EndInclusive = e; Sum = sum; }
        }

        private static Run1d? FindBestRun(float[] score, int start, int end, int minLen, int maxLen, int maxGap)
        {
            if (score == null || score.Length == 0) return null;
            start = Clamp(start, 0, score.Length);
            end = Clamp(end, 0, score.Length);
            if (end <= start) return null;

            float p90 = Percentile(score, start, end - start, 90.0);
            float thr = Math.Max(20.0f, 0.5f * p90);

            Run1d? best = null;
            double bestScore = 0.0;

            int i = start;
            while (i < end)
            {
                if (score[i] <= thr)
                {
                    i++;
                    continue;
                }

                int j = i;
                double currentSum = 0.0;
                int currentGap = 0;
                int lastValidIndex = i;

                while (j < end)
                {
                    if (score[j] > thr)
                    {
                        currentSum += score[j];
                        currentGap = 0;
                        lastValidIndex = j;
                        j++;
                    }
                    else
                    {
                        if (currentGap < maxGap)
                        {
                            currentGap++;
                            j++;
                        }
                        else
                        {
                            break;
                        }
                    }
                }

                int top = i;
                int bot = lastValidIndex;
                int length = bot - top + 1;

                if (length >= minLen && length <= maxLen)
                {
                    double weightedScore = currentSum;

                    if (best == null || weightedScore > bestScore)
                    {
                        bestScore = weightedScore;
                        best = new Run1d(top, bot, currentSum);
                    }
                }

                i = j;
            }

            return best;
        }

        private static float[] RednessScoreLine(ImageBuffer image, int x, int yoff, int h)
        {
            int w = image.Width;
            int x0 = Clamp(x - 1, 0, w - 1);
            int x1 = Clamp(x, 0, w - 1);
            int x2 = Clamp(x + 1, 0, w - 1);
            var res = new float[h];
            for (int y = 0; y < h; y++)
            {
                int absY = yoff + y;
                res[y] = (RednessAt(image, x0, absY) + RednessAt(image, x1, absY) + RednessAt(image, x2, absY)) / 3f;
            }
            return res;
        }

        private static float[] RednessScoreRow(ImageBuffer image, int yClient, int yoff, int h)
        {
            int w = image.Width;
            int absY = yoff + yClient;
            int top = Clamp(absY - 1, yoff, yoff + h - 1);
            int mid = Clamp(absY, yoff, yoff + h - 1);
            int bot = Clamp(absY + 1, yoff, yoff + h - 1);
            var res = new float[w];
            for (int x = 0; x < w; x++)
                res[x] = (RednessAt(image, x, top) + RednessAt(image, x, mid) + RednessAt(image, x, bot)) / 3f;
            return res;
        }

        private static float RednessAt(ImageBuffer image, int x, int y)
        {
            int i = (y * image.Width + x) * 4;
            byte b = image.Data[i + 0];
            byte g = image.Data[i + 1];
            byte r = image.Data[i + 2];
            int m = b > g ? b : g;
            int s = r - m;
            return s < 0 ? 0 : s;
        }

        private static int Clamp(int v, int min, int max) => (v < min) ? min : (v > max ? max : v);

        private static Rectangle ClampRect(int x1, int y1, int x2, int y2, int w, int h)
        {
            x1 = Clamp(x1, 0, w); y1 = Clamp(y1, 0, h);
            x2 = Clamp(x2, 0, w); y2 = Clamp(y2, 0, h);
            if (x2 < x1) { int t = x1; x1 = x2; x2 = t; }
            if (y2 < y1) { int t = y1; y1 = y2; y2 = t; }
            if (x2 == x1 && x1 < w) x2++;
            if (y2 == y1 && y1 < h) y2++;
            return new Rectangle(x1, y1, x2 - x1, y2 - y1);
        }

        private static Rectangle ExpandAndClamp(Rectangle r, int px, int py, int w, int h)
        {
            return ClampRect(r.Left - px, r.Top - py, r.Right + px, r.Bottom + py, w, h);
        }

        private static float[] Smooth3(float[] src)
        {
            var dst = new float[src.Length];
            for (int i = 0; i < src.Length; i++)
            {
                float p = i > 0 ? src[i - 1] : 0;
                float c = src[i];
                float n = i + 1 < src.Length ? src[i + 1] : 0;
                dst[i] = 0.25f * p + 0.5f * c + 0.25f * n;
            }
            return dst;
        }

        private static float Percentile(float[] data, int start, int length, double p)
        {
            if (length <= 0) return 0;
            float[] tmp = new float[length];
            Array.Copy(data, start, tmp, 0, length);
            Array.Sort(tmp);
            int idx = (int)((tmp.Length - 1) * (p / 100.0));
            return tmp[idx];
        }

        private static double ComputeRedAdvThresholdCv(Mat redAdv, Rect roi, double percentile, double minThr)
        {
            roi = new Rect(
                Math.Max(0, roi.X),
                Math.Max(0, roi.Y),
                Math.Min(redAdv.Width - roi.X, roi.Width),
                Math.Min(redAdv.Height - roi.Y, roi.Height));
            if (roi.Width <= 0 || roi.Height <= 0) return 0;

            using var roiMat = new Mat(redAdv, roi);
            int histSize = 256;
            Rangef range = new Rangef(0, 256);
            var hist = new Mat();
            Cv2.CalcHist(new[] { roiMat }, new[] { 0 }, null, hist, 1, new[] { histSize }, new[] { range }, uniform: true, accumulate: false);
            float total = (float)Cv2.Sum(hist).Val0;
            if (total <= 0) return minThr;

            float target = total * (float)(percentile / 100.0);
            float cum = 0;
            int p = 0;
            for (int i = 0; i < histSize; i++)
            {
                cum += hist.Get<float>(i);
                if (cum >= target)
                {
                    p = i;
                    break;
                }
            }
            return Math.Max(minThr, p);
        }

        private static int AutoDetectTitlebar(ImageBuffer image)
        {
            int h = image.Height;
            int w = image.Width;
            if (h < 40) return 0;

            // 先试 CV 梯度（更稳），再回退老逻辑
            int searchH = Math.Min(80, h);
            int yoffCv = 0;
            {
                GCHandle handle = GCHandle.Alloc(image.Data, GCHandleType.Pinned);
                try
                {
                    using var mat = Mat.FromPixelData(image.Height, image.Width, MatType.CV_8UC4, handle.AddrOfPinnedObject());
                    using var gray = new Mat();
                    Cv2.CvtColor(mat, gray, ColorConversionCodes.BGRA2GRAY);
                    using var sobel = new Mat();
                    Cv2.Sobel(gray, sobel, MatType.CV_32F, 0, 1, ksize: 3);
                    var rowSum = new float[searchH];
                    for (int y = 0; y < searchH; y++)
                    {
                        using var row = sobel.Row(y);
                        rowSum[y] = (float)Cv2.Sum(row).Val0 / w;
                    }
                    int bestY = 0;
                    float bestVal = 0;
                    for (int y = 1; y < searchH - 1; y++)
                    {
                        if (rowSum[y] > bestVal)
                        {
                            bestVal = rowSum[y];
                            bestY = y;
                        }
                    }
                    if (bestVal > 50f && bestY > 5 && bestY < 70)
                        yoffCv = bestY;
                }
                finally
                {
                    if (handle.IsAllocated) handle.Free();
                }
            }

            if (yoffCv > 0) return yoffCv;

            // 回退：方差 + 行梯度
            double sum = 0, sumSq = 0;
            int checkH = Math.Min(10, h);
            for (int i = 0; i < w * checkH * 4; i += 4)
            {
                double g = 0.114 * image.Data[i] + 0.587 * image.Data[i + 1] + 0.299 * image.Data[i + 2];
                sum += g; sumSq += g * g;
            }
            double std = Math.Sqrt(sumSq / (w * checkH) - Math.Pow(sum / (w * checkH), 2));
            if (std > 10.0) return 0;

            double bestGrad = -1;
            int bestY2 = 0;
            for (int y = 0; y < searchH - 1; y++)
            {
                double rowDiff = 0;
                for (int x = 0; x < w; x += 4)
                {
                    int i0 = (y * w + x) * 4; int i1 = ((y + 1) * w + x) * 4;
                    double g0 = image.Data[i0] + image.Data[i0 + 1] + image.Data[i0 + 2];
                    double g1 = image.Data[i1] + image.Data[i1 + 1] + image.Data[i1 + 2];
                    rowDiff += Math.Abs(g1 - g0) / 3.0;
                }
                rowDiff /= (w / 4.0);
                if (rowDiff > bestGrad) { bestGrad = rowDiff; bestY2 = y; }
            }

            if (bestGrad > 15.0 && bestY2 > 10 && bestY2 < 70) return bestY2 + 1;
            return 0;
        }

    }
}
