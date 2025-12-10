using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;

namespace SkillbarCapture
{
    internal enum SkillbarStage
    {
        None,
        Filling,
        StableFull,
        BrightPeak,
        Fade,
        Disappear
    }

    internal sealed class FrameFeatures
    {
        public double Energy { get; init; }        // 残差能量（条区域）
        public double WidthRatio { get; init; }    // 激活列宽度比例
        public double EnergyNorm { get; init; }    // 归一化能量（相对全局最大）
        public double EnergyDeriv { get; init; }   // 一阶导（平滑后）
    }

    /// <summary>
    /// 离线分析：自动建模板，输出阶段划分，并导出技能条残差图以便目检。
    /// </summary>
    internal static class PhaseAnalysis
    {
        public static void AnalyzeWithTemplate(string folder)
        {
            var files = Directory.GetFiles(folder, "*.png").OrderBy(f => f, StringComparer.Ordinal).ToList();
            if (files.Count == 0)
            {
                Console.WriteLine("目录下没有 png 文件");
                return;
            }

            var samplesBase = files.Where(f => GetIndex(f) >= 190 && GetIndex(f) <= 210).ToList();
            var samplesBar = files.Where(f => GetIndex(f) >= 309 && GetIndex(f) <= 339).ToList();
            if (samplesBase.Count == 0 || samplesBar.Count == 0)
            {
                Console.WriteLine("样本不足：需要 190-210 做基线，309-339 做满条模板");
                return;
            }

            BuildTemplate(samplesBase, samplesBar, out var baseline, out var mask, out double fullEnergy, out double targetWidthRatio);

            // 输出残差图目录
            string extractDir = Path.Combine(folder, "..", "2");
            Directory.CreateDirectory(extractDir);

            var feats = new List<(int idx, FrameFeatures feat)>();
            double globalMaxEnergy = 0;
            double globalMinEnergy = double.MaxValue;
            var residualList = new List<double[,]>();

            foreach (var f in files)
            {
                using var bmp = new Bitmap(f);
                byte[] bgra = ToBgra(bmp);
                double[,] residual = BuildResidual(bgra, baseline, mask, out double frameEnergy, out double widthRatio, out double frameMax);
                residualList.Add(residual);
                if (frameEnergy > globalMaxEnergy) globalMaxEnergy = frameEnergy;
                if (frameEnergy < globalMinEnergy) globalMinEnergy = frameEnergy;
                feats.Add((GetIndex(f), new FrameFeatures { Energy = frameEnergy, WidthRatio = widthRatio }));

                // 保存残差图
                string outPng = Path.Combine(extractDir, $"{GetIndex(f):D4}.png");
                SaveResidualPng(residual, frameMax, outPng);
            }

            // 归一化 + 平滑 + 导数
            double[] eAdj = feats.Select(f => Math.Max(0, f.feat.Energy - globalMinEnergy)).ToArray();
            double maxAdj = eAdj.Max();
            if (maxAdj <= 0) maxAdj = 1;
            double[] eNorm = eAdj.Select(v => v / maxAdj).ToArray();
            double[] wNorm = feats.Select(f => f.feat.WidthRatio).ToArray();
            double[] eSmooth = Smooth(eNorm, 5);
            double[] deriv = Derivative(eSmooth);

            // 更新特征
            for (int i = 0; i < feats.Count; i++)
            {
                var f = feats[i];
                feats[i] = (f.idx, new FrameFeatures
                {
                    Energy = f.feat.Energy,
                    WidthRatio = f.feat.WidthRatio,
                    EnergyNorm = eSmooth[i],
                    EnergyDeriv = deriv[i]
                });
            }

            // 分段检测
            DetectSegments(feats, out var segFillEnd, out var segStableEnd, out var segBrightEnd, out var segFadeEnd);

            // 打印阶段区间
            Console.WriteLine($"分析目录：{folder}，共 {files.Count} 张");
            Console.WriteLine("阶段划分（估计）：");
            Console.WriteLine($"填充：{feats[0].idx:D4}-{segFillEnd:D4}");
            Console.WriteLine($"稳定：{(segFillEnd + 1):D4}-{segStableEnd:D4}");
            Console.WriteLine($"亮峰：{(segStableEnd + 1):D4}-{segBrightEnd:D4}");
            Console.WriteLine($"淡出：{(segBrightEnd + 1):D4}-{segFadeEnd:D4}");
            Console.WriteLine($"消失：{(segFadeEnd + 1):D4}-{feats[^1].idx:D4}");

            Console.WriteLine();
            Console.WriteLine("Index\t阶段\tE_norm\tW_ratio\tDeriv");
            foreach (var f in feats)
            {
                var stage = StageForIndex(f.idx, segFillEnd, segStableEnd, segBrightEnd, segFadeEnd);
                Console.WriteLine($"{f.idx:D4}\t{StageToCn(stage)}\t{f.feat.EnergyNorm:F3}\t{f.feat.WidthRatio:F3}\t{f.feat.EnergyDeriv:F4}");
            }
        }

        private static void BuildTemplate(IReadOnlyList<string> baseFiles, IReadOnlyList<string> barFiles, out double[,] baseline, out byte[,] mask, out double fullEnergy, out double targetWidthRatio)
        {
            using var first = new Bitmap(baseFiles[0]);
            int w = first.Width;
            int h = first.Height;
            baseline = new double[h, w];
            mask = new byte[h, w];

            double[,,] stack = new double[baseFiles.Count, h, w];
            for (int i = 0; i < baseFiles.Count; i++)
            {
                using var bmp = new Bitmap(baseFiles[i]);
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        double lum = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                        stack[i, y, x] = lum;
                    }
                }
            }

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double min = double.MaxValue;
                    for (int i = 0; i < baseFiles.Count; i++)
                    {
                        if (stack[i, y, x] < min) min = stack[i, y, x];
                    }
                    baseline[y, x] = min;
                }
            }

            double[,] barMean = new double[h, w];
            foreach (var file in barFiles)
            {
                using var bmp = new Bitmap(file);
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        var c = bmp.GetPixel(x, y);
                        double lum = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;
                        double diff = lum - baseline[y, x];
                        if (diff < 0) diff = 0;
                        barMean[y, x] += diff;
                    }
                }
            }

            double frameCount = barFiles.Count;
            double maxMean = 0;
            int maskCount = 0;
            int maskCols = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    barMean[y, x] /= frameCount;
                    if (barMean[y, x] > maxMean) maxMean = barMean[y, x];
                }
            }

            double th = maxMean * 0.35;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (barMean[y, x] > th)
                    {
                        mask[y, x] = 1;
                        maskCount++;
                    }
                }
            }

            // 估算掩码宽度
            for (int x = 0; x < w; x++)
            {
                bool any = false;
                for (int y = 0; y < h; y++)
                {
                    if (mask[y, x] != 0)
                    {
                        any = true;
                        break;
                    }
                }
                if (any) maskCols++;
            }

            fullEnergy = Math.Max(1.0, maxMean * maskCount * 1.80);
            targetWidthRatio = w == 0 ? 0 : (double)maskCols / w;
        }

        private static void DetectSegments(List<(int idx, FrameFeatures feat)> feats,
            out int fillEnd, out int stableEnd, out int brightEnd, out int fadeEnd)
        {
            int n = feats.Count;
            double[] e = feats.Select(f => f.feat.EnergyNorm).ToArray();
            double[] w = feats.Select(f => f.feat.WidthRatio).ToArray();
            double[] d = feats.Select(f => f.feat.EnergyDeriv).ToArray();

            int first = Array.FindIndex(e, v => v > 0.02);
            if (first < 0) first = 0;

            // 填充结束：能量>0.38 且宽度>0.18，或导数下降到 <0.0015 之后
            fillEnd = feats[Math.Min(first + 20, n - 1)].idx;
            for (int i = first; i < n; i++)
            {
                if (e[i] > 0.38 && w[i] > 0.18)
                {
                    fillEnd = feats[i].idx;
                    break;
                }
                if (i > first + 15 && d[i] < 0.0015 && e[i] > 0.30)
                {
                    fillEnd = feats[i].idx;
                    break;
                }
            }

            // 稳定结束：能量>0.75 或导数 >0.010
            stableEnd = fillEnd;
            for (int i = IndexOf(feats, fillEnd) + 1; i < n; i++)
            {
                if (e[i] > 0.75 || d[i] > 0.010)
                {
                    stableEnd = feats[i].idx;
                    break;
                }
            }

            // 亮峰结束：能量达到0.99 或导数连续为负
            brightEnd = stableEnd;
            int negCount = 0;
            for (int i = IndexOf(feats, stableEnd) + 1; i < n; i++)
            {
                if (d[i] < -0.0025) negCount++; else negCount = 0;
                if (e[i] >= 0.99 || negCount >= 5)
                {
                    brightEnd = feats[i].idx;
                    break;
                }
            }

            // 淡出结束：能量<0.1 或宽度<0.05，或连续下降 20 帧
            fadeEnd = brightEnd;
            negCount = 0;
            for (int i = IndexOf(feats, brightEnd) + 1; i < n; i++)
            {
                if (d[i] < -0.002) negCount++; else negCount = 0;
                if (e[i] < 0.10 || w[i] < 0.05 || negCount >= 20)
                {
                    fadeEnd = feats[i].idx;
                    break;
                }
            }
            if (fadeEnd <= brightEnd) fadeEnd = feats[Math.Min(n - 1, IndexOf(feats, brightEnd) + 50)].idx;
        }

        private static int IndexOf(List<(int idx, FrameFeatures feat)> feats, int frameIdx)
        {
            for (int i = 0; i < feats.Count; i++)
                if (feats[i].idx == frameIdx) return i;
            return 0;
        }

        private static double[] Smooth(double[] data, int window)
        {
            if (window < 1) return data;
            int n = data.Length;
            double[] res = new double[n];
            int half = window / 2;
            for (int i = 0; i < n; i++)
            {
                int start = Math.Max(0, i - half);
                int end = Math.Min(n - 1, i + half);
                double sum = 0;
                int count = 0;
                for (int j = start; j <= end; j++)
                {
                    sum += data[j];
                    count++;
                }
                res[i] = count > 0 ? sum / count : data[i];
            }
            return res;
        }

        private static double[] Derivative(double[] data)
        {
            int n = data.Length;
            double[] res = new double[n];
            for (int i = 1; i < n; i++)
            {
                res[i] = data[i] - data[i - 1];
            }
            res[0] = res[1];
            return res;
        }

        private static double[,] BuildResidual(byte[] bgra, double[,] baseline, byte[,] mask, out double energy, out double widthRatio, out double maxVal)
        {
            int h = baseline.GetLength(0);
            int w = baseline.GetLength(1);
            double[,] res = new double[h, w];
            energy = 0;
            maxVal = 0;
            double[] col = new double[w];
            int idx = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    byte b = bgra[idx + 0];
                    byte g = bgra[idx + 1];
                    byte r = bgra[idx + 2];
                    double lum = 0.2126 * r + 0.7152 * g + 0.0722 * b;
                    double diff = lum - baseline[y, x];
                    if (diff < 0) diff = 0;
                    if (mask[y, x] != 0)
                    {
                        res[y, x] = diff;
                        energy += diff;
                        col[x] += diff;
                        if (diff > maxVal) maxVal = diff;
                    }
                    idx += 4;
                }
            }
            double maxCol = col.Max();
            int active = 0;
            if (maxCol > 0)
            {
                double th = maxCol * 0.3;
                for (int x = 0; x < w; x++)
                {
                    if (col[x] > th) active++;
                }
            }
            widthRatio = w == 0 ? 0 : (double)active / w;
            if (maxVal < 1) maxVal = 1;
            return res;
        }

        private static void SaveResidualPng(double[,] residual, double maxVal, string path)
        {
            int h = residual.GetLength(0);
            int w = residual.GetLength(1);
            using var bmp = new Bitmap(w, h);
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    double v = residual[y, x] / maxVal;
                    int c = (int)Math.Clamp(v * 255.0, 0, 255);
                    bmp.SetPixel(x, y, Color.FromArgb(255, c, c, 0)); // 黄色
                }
            }
            bmp.Save(path);
        }

        private static int GetIndex(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            for (int i = name.Length - 1; i >= 0; i--)
            {
                if (!char.IsDigit(name[i]))
                {
                    string num = name[(i + 1)..];
                    if (int.TryParse(num, out int idx)) return idx;
                    break;
                }
            }
            return 0;
        }

        private static byte[] ToBgra(Bitmap bmp)
        {
            int w = bmp.Width;
            int h = bmp.Height;
            byte[] data = new byte[w * h * 4];
            int idx = 0;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    var c = bmp.GetPixel(x, y);
                    data[idx++] = c.B;
                    data[idx++] = c.G;
                    data[idx++] = c.R;
                    data[idx++] = 255;
                }
            }
            return data;
        }

        private static SkillbarStage StageForIndex(int idx, int fillEnd, int stableEnd, int brightEnd, int fadeEnd)
        {
            if (idx <= fillEnd) return SkillbarStage.Filling;
            if (idx <= stableEnd) return SkillbarStage.StableFull;
            if (idx <= brightEnd) return SkillbarStage.BrightPeak;
            if (idx <= fadeEnd) return SkillbarStage.Fade;
            return SkillbarStage.Disappear;
        }

        private static string StageToCn(SkillbarStage s) => s switch
        {
            SkillbarStage.Filling => "填充",
            SkillbarStage.StableFull => "满条稳定",
            SkillbarStage.BrightPeak => "亮峰",
            SkillbarStage.Fade => "淡出",
            SkillbarStage.Disappear => "消失",
            _ => "无"
        };
    }
}
