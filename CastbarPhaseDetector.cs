// CastbarPhaseDetector.cs
// Single-file, drop-in class for OpenCvSharp.
// Target: .NET 6+ (works on .NET Framework with minor tweaks if needed).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Runtime.InteropServices;
using OpenCvSharp;

namespace Castbar
{
    public enum CastbarStage
    {
        Idle = 0,
        Fill = 1,
        TurnLight = 2,
        Turnout = 3
    }

    public enum TurnoutLevel
    {
        None = 0,
        Fade0 = 1,
        Fade50 = 2,
        Fade100 = 3
    }

    public readonly struct CastbarPhaseResult
    {
        public readonly CastbarStage Stage;
        public readonly CastbarStage InternalStage;
        public readonly TurnoutLevel Turnout;

        public readonly double FillRatio;
        public readonly int FillPos;
        public readonly bool HasEmpty;

        public readonly double Intensity;
        public readonly double IntensityAlpha;

        public readonly double FadeRatio;
        public readonly double Baseline;
        public readonly double Peak;

        public readonly double HueCenter;

        public CastbarPhaseResult(
            CastbarStage stage, CastbarStage internalStage, TurnoutLevel turnout,
            double fillRatio, int fillPos, bool hasEmpty,
            double intensity, double intensityAlpha,
            double fadeRatio, double baseline, double peak,
            double hueCenter)
        {
            Stage = stage;
            InternalStage = internalStage;
            Turnout = turnout;

            FillRatio = fillRatio;
            FillPos = fillPos;
            HasEmpty = hasEmpty;

            Intensity = intensity;
            IntensityAlpha = intensityAlpha;

            FadeRatio = fadeRatio;
            Baseline = baseline;
            Peak = peak;

            HueCenter = hueCenter;
        }
    }

    public sealed class CastbarPhaseConfig
    {
        public double HueCenterInit { get; set; } = 25.0;
        public double HueSigma { get; set; } = 12.0;
        public double HueUpdateAlpha { get; set; } = 0.15;
        public int HueUpdateWindow { get; set; } = 12;
        public int HueUpdateMinPixels { get; set; } = 50;
        public double HueUpdateSThr { get; set; } = 0.25;
        public double HueUpdateVThr { get; set; } = 0.25;
        public double HueUpdateAThr { get; set; } = 0.20;

        public double CollapseQuantile { get; set; } = 0.80;
        public double EdgeTh { get; set; } = 0.30;
        public int GapTolerance { get; set; } = 4;

        public double RightStdThr { get; set; } = 0.030;
        public double EmptyLeftRightDelta { get; set; } = 0.12;
        public double EmptyRangeThr { get; set; } = 0.12;
        public double EmptyRightBelowP50 { get; set; } = 0.05;

        public double IntensityTopFrac { get; set; } = 0.05;

        public double BaselineInit { get; set; } = 0.02;
        public double PeakInit { get; set; } = 0.12;
        public double BaselineLeakyRise { get; set; } = 0.0005;
        public double BaselineEmaAlpha { get; set; } = 0.03;

        public double StartDelta { get; set; } = 0.020;
        public double FillDoneRatio { get; set; } = 0.98;
        public int FillDoneHold { get; set; } = 2;

        public double DropFromPeak { get; set; } = 0.015;
        public int TurnoutHold { get; set; } = 2;

        // High-level macro phase tuning (for human-labeled Fill / TurnLight / Turnout)
        public int MacroFillMinFramesForLight { get; set; } = 6;
        public int MacroFillMinFramesTotal { get; set; } = 20;
        public double MacroVeryBrightDelta { get; set; } = 0.50;
        public int MacroTurnLightMinFrames { get; set; } = 2;
        public int MacroTurnLightMaxFrames { get; set; } = 90;
        public int MacroFullStableFrames { get; set; } = 2;
        public double MacroLightDrop { get; set; } = 0.08;
        public int MacroHistorySize { get; set; } = 10;

        public double Fade50Ratio { get; set; } = 0.50;
        public double Fade100Ratio { get; set; } = 0.05;
        public int EndHold { get; set; } = 1;
        public double EndDelta { get; set; } = 0.010;

        public bool UseAlphaIntensity { get; set; } = true;
        public double AlphaWeight { get; set; } = 0.50;

        public static CastbarPhaseConfig FromJson(string jsonPath)
        {
            var json = File.ReadAllText(jsonPath, Encoding.UTF8);
            var cfg = JsonSerializer.Deserialize<CastbarPhaseConfig>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (cfg == null) throw new InvalidOperationException("Cannot parse config json.");
            return cfg;
        }

        public void SaveJson(string jsonPath)
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, json, new UTF8Encoding(false));
        }
    }

    public sealed class CastbarPhaseDetector : IDisposable
    {
        private readonly CastbarPhaseConfig _cfg;

        private double _hueCenter;
        private Mat _hueLut; // 1x256 CV_8U

        private double _baseline;
        private double _peak;

        // Internal low-level state machine stage
        private CastbarStage _stage;

        // High-level (human-facing) phase classification
        private CastbarStage _macroStage;
        private int _macroFillFrames;
        private int _macroFillTotalFrames;
        private int _macroLightFrames;
        private int _macroTurnLightFrames;
        private int _macroTurnoutFrames;
        private double _localPeak;

        private double _lastMixedIntensity;
        private double _lastFadeRatio;

        private int _fillDoneCount;
        private int _turnoutCount;
        private int _endCount;

        // Sliding window history for intensity / fade (fixed size ring buffer)
        private readonly double[] _iHist;
        private readonly double[] _fadeHist;
        private readonly double[] _rHist;
        private int _histPos;
        private int _histCount;

        private int _fullStableFrames;
        private double _lastFillRatio;
        private readonly double[] _fillHist;

        public CastbarStage Stage => _macroStage;
        public CastbarStage InternalStage => _stage;
        public double Baseline => _baseline;
        public double Peak => _peak;
        public double HueCenter => _hueCenter;

        public CastbarPhaseDetector(CastbarPhaseConfig? cfg = null)
        {
            _cfg = cfg ?? new CastbarPhaseConfig();

            _hueCenter = _cfg.HueCenterInit;
            _hueLut = BuildHueLutMat(_hueCenter, _cfg.HueSigma);

            _baseline = _cfg.BaselineInit;
            _peak = Math.Max(_cfg.PeakInit, _baseline + 1e-3);

            _stage = CastbarStage.Idle;
            _macroStage = CastbarStage.Idle;
            _macroFillFrames = 0;
            _macroFillTotalFrames = 0;
            _macroLightFrames = 0;
            _macroTurnLightFrames = 0;
            _macroTurnoutFrames = 0;
            _localPeak = 0.0;
            _lastMixedIntensity = 0.0;
            _lastFadeRatio = 0.0;
            _fillDoneCount = 0;
            _turnoutCount = 0;
            _endCount = 0;
            _iHist = new double[_cfg.MacroHistorySize];
            _fadeHist = new double[_cfg.MacroHistorySize];
            _rHist = new double[_cfg.MacroHistorySize];
            _histPos = 0;
            _histCount = 0;
            _fullStableFrames = 0;
            _lastFillRatio = 0.0;
        }

        public void Reset()
        {
            _hueCenter = _cfg.HueCenterInit;
            _hueLut.Dispose();
            _hueLut = BuildHueLutMat(_hueCenter, _cfg.HueSigma);

            _baseline = _cfg.BaselineInit;
            _peak = Math.Max(_cfg.PeakInit, _baseline + 1e-3);

            _stage = CastbarStage.Idle;
            _macroStage = CastbarStage.Idle;
            _macroFillFrames = 0;
            _macroFillTotalFrames = 0;
            _macroLightFrames = 0;
            _macroTurnLightFrames = 0;
            _macroTurnoutFrames = 0;
            _localPeak = 0.0;
            _lastMixedIntensity = 0.0;
            _lastFadeRatio = 0.0;
            _fillDoneCount = 0;
            _turnoutCount = 0;
            _endCount = 0;
            Array.Clear(_iHist, 0, _iHist.Length);
            Array.Clear(_fadeHist, 0, _fadeHist.Length);
            Array.Clear(_rHist, 0, _rHist.Length);
            _histPos = 0;
            _histCount = 0;
            _fullStableFrames = 0;
            _lastFillRatio = 0.0;
        }

        public CastbarPhaseResult Analyze(Mat frame, Rect roi, double scale = 1.0)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (frame.Empty()) throw new ArgumentException("Empty frame.");
            if (scale <= 0.0 || scale > 4.0) throw new ArgumentException("scale must be in (0,4].");

            Rect r = ClipRect(roi, frame.Width, frame.Height);

            using var srcRoi = new Mat(frame, r);
            using var roiScaled = (Math.Abs(scale - 1.0) < 1e-9) ? srcRoi.Clone() : ResizeArea(srcRoi, scale);

            return AnalyzeRoi(roiScaled);
        }

        public CastbarPhaseResult Analyze(Mat roiBgrOrBgra)
        {
            if (roiBgrOrBgra == null) throw new ArgumentNullException(nameof(roiBgrOrBgra));
            if (roiBgrOrBgra.Empty()) throw new ArgumentException("Empty image.");
            return AnalyzeRoi(roiBgrOrBgra);
        }

        private CastbarPhaseResult AnalyzeRoi(Mat roiBgrOrBgra)
        {
            using var src = EnsureBgrOrBgra(roiBgrOrBgra);
            bool hasAlpha = src.Channels() == 4;

            using var bgr = hasAlpha ? src.CvtColor(ColorConversionCodes.BGRA2BGR) : src.Clone();

            Mat? alpha01 = null;
            if (hasAlpha)
            {
                using var a8 = new Mat();
                Cv2.ExtractChannel(src, a8, 3);
                alpha01 = new Mat();
                a8.ConvertTo(alpha01, MatType.CV_32F, 1.0 / 255.0);
            }

            using var hsv = new Mat();
            Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);

            if (_stage == CastbarStage.Idle)
            {
                MaybeUpdateHue(hsv, alpha01);
            }

            using var hueW = HueWeight01(hsv);
            using var s01 = Channel01(hsv, 1);
            using var v01 = Channel01(hsv, 2);

            using var score = new Mat();
            Cv2.Multiply(s01, v01, score);
            Cv2.Multiply(score, hueW, score);
            if (alpha01 != null)
                Cv2.Multiply(score, alpha01, score);

            int hh = score.Rows;
            int ww = score.Cols;

            int top = (hh >= 8) ? 2 : 0;
            int bot = (hh >= 8) ? (hh - 2) : hh;
            int midH = Math.Max(1, bot - top);

            using var scoreMidView = score.RowRange(top, bot);
            using var scoreMid = EnsureContinuous(scoreMidView);

            scoreMid.GetArray(out float[] scoreArr);

            float[] col = new float[ww];
            float[] colStd = new float[ww];
            float[] tmpCol = new float[midH];

            for (int x = 0; x < ww; x++)
            {
                double mean = 0.0;
                for (int y = 0; y < midH; y++)
                {
                    float val = scoreArr[y * ww + x];
                    tmpCol[y] = val;
                    mean += val;
                }
                mean /= midH;

                double var = 0.0;
                for (int y = 0; y < midH; y++)
                {
                    double d = tmpCol[y] - mean;
                    var += d * d;
                }
                var /= midH;

                Array.Sort(tmpCol);
                int qIdx = (int)Math.Round(_cfg.CollapseQuantile * (midH - 1));
                qIdx = ClampInt(qIdx, 0, midH - 1);

                col[x] = tmpCol[qIdx];
                colStd[x] = (float)Math.Sqrt(var);
            }

            int n = Math.Max(1, ww / 8);
            double colLeft = Mean(col, 0, n);
            double colRight = Mean(col, ww - n, n);
            double rightStd = Mean(colStd, ww - n, n);

            float[] colCopy = (float[])col.Clone();
            float p10 = Percentile(colCopy, 0.10f);
            float p50 = Percentile(colCopy, 0.50f);
            float p90 = Percentile(colCopy, 0.90f);
            double colRange = p90 - p10;

            int tipN = Math.Max(2, ww / 8);
            double rightMax = double.MinValue;
            for (int x = ww - tipN; x < ww; x++)
                if (col[x] > rightMax) rightMax = col[x];
            double tipProminence = rightMax - p90;
            bool tipActive = tipProminence >= 0.05;

            bool hasEmpty =
                (rightStd < _cfg.RightStdThr) &&
                ((colLeft - colRight) > _cfg.EmptyLeftRightDelta) &&
                (colRange > _cfg.EmptyRangeThr) &&
                (colRight < (p50 - _cfg.EmptyRightBelowP50));

            int fillPos = ww - 1;
            double fillRatio = 1.0;

            double sumV = 0.0;
            double sumVX = 0.0;
            for (int x = 0; x < ww; x++)
            {
                double v = col[x];
                sumV += v;
                sumVX += v * x;
            }

            double energyFill = fillRatio;
            if (sumV > 1e-9)
            {
                double cx = sumVX / sumV;
                energyFill = cx / Math.Max(1.0, (ww - 1));
                energyFill = Clamp01(energyFill);
            }

            if (hasEmpty)
            {
                double bg = Math.Min(colRight, p10);
                double fg = Math.Max(colLeft, p90);
                double th = bg + _cfg.EdgeTh * (fg - bg);

                int lastFilled = -1;
                int gap = 0;

                for (int x = 0; x < ww; x++)
                {
                    bool filled = col[x] > th;
                    if (filled)
                    {
                        lastFilled = x;
                        gap = 0;
                    }
                    else
                    {
                        if (lastFilled >= 0)
                        {
                            gap++;
                            if (gap > _cfg.GapTolerance) break;
                        }
                    }
                }

                fillPos = lastFilled;
                fillRatio = (fillPos >= 0) ? ((double)(fillPos + 1) / ww) : 0.0;
            }

            // When bar visually exists but empty-gap detection fails, we still track
            // a coarse fill progress using energy centroid. This keeps Fill from
            // ending too early in presence of bright moving tip highlights.
            double effectiveFill = hasEmpty ? fillRatio : energyFill;

            double intensity = TopKMean(scoreArr, _cfg.IntensityTopFrac);

            double intensityAlpha = 0.0;
            if (alpha01 != null)
            {
                using var alphaMidView = alpha01.RowRange(top, bot);
                using var alphaMid = EnsureContinuous(alphaMidView);
                alphaMid.GetArray(out float[] aArr);
                intensityAlpha = TopKMean(aArr, _cfg.IntensityTopFrac);
                alphaMid.Dispose();
            }

            alpha01?.Dispose();

            double mixedIntensity = MixIntensity(intensity, intensityAlpha);

            if (_stage == CastbarStage.Idle)
            {
                _baseline = Math.Min(_baseline + _cfg.BaselineLeakyRise, mixedIntensity);
                _baseline = (1.0 - _cfg.BaselineEmaAlpha) * _baseline + _cfg.BaselineEmaAlpha * mixedIntensity;
            }

            // 更严格的“开始填充”条件：必须看到明显的条形结构（右侧空、列范围足够大）?
            // 避免纯背景亮度抖动被误判为进度条?
            bool strongStructure = colRange > _cfg.EmptyRangeThr;
            bool startCond = hasEmpty && fillRatio < 0.999 && strongStructure &&
                             (mixedIntensity > _baseline + _cfg.StartDelta);

            if (_stage == CastbarStage.Idle && startCond)
            {
                StartFillStage(mixedIntensity);
            }

            if (_stage == CastbarStage.Fill)
            {
                bool done = (!hasEmpty) || (fillRatio >= _cfg.FillDoneRatio);
                _fillDoneCount = done ? (_fillDoneCount + 1) : 0;

                if (_fillDoneCount >= _cfg.FillDoneHold)
                {
                    _stage = CastbarStage.TurnLight;
                    _peak = Math.Max(_peak, mixedIntensity);
                }
            }

            if (_stage == CastbarStage.TurnLight)
            {
                _peak = Math.Max(_peak, mixedIntensity);

                bool drop = (_peak - mixedIntensity) >= _cfg.DropFromPeak;
                _turnoutCount = drop ? (_turnoutCount + 1) : 0;

                if (_turnoutCount >= _cfg.TurnoutHold)
                {
                    _stage = CastbarStage.Turnout;
                    _endCount = 0;
                }
            }

            double denom = Math.Max(1e-6, _peak - _baseline);
            double fadeRatio = Clamp01((mixedIntensity - _baseline) / denom);

            // Update high-level macro stage (Fill / TurnLight / Turnout) based on
            // the more detailed measurements, aiming to be robust to background changes.
            AddHistory(mixedIntensity, fadeRatio, effectiveFill);

            // hasBar: 既有明显条形结构，又比基线亮，避免背景抖动触发?
            bool hasBar = strongStructure && (mixedIntensity > _baseline + _cfg.StartDelta);
            bool isFull = ((!hasEmpty) && (fillRatio >= _cfg.FillDoneRatio) && !tipActive) ||
                          (effectiveFill >= 0.995 && !tipActive);

            UpdateMacroStage(hasBar, isFull, fillRatio, effectiveFill, mixedIntensity, fadeRatio);

            // ?Turnout 阶段中，如果再次出现明显的“空右侧 + 条形结构”，视为新一轮施法开始?
            if (_stage == CastbarStage.Turnout && startCond)
            {
                StartFillStage(mixedIntensity);

                // 重新计算淡出归一化（新的峰值），以免上一轮的峰值干扰本轮?
                denom = Math.Max(1e-6, _peak - _baseline);
                fadeRatio = Clamp01((mixedIntensity - _baseline) / denom);
            }

            if (_stage == CastbarStage.Turnout)
            {
                bool endCond = (fadeRatio <= _cfg.Fade100Ratio) || (mixedIntensity <= _baseline + _cfg.EndDelta);
                _endCount = endCond ? (_endCount + 1) : 0;

                if (_endCount >= _cfg.EndHold)
                {
                    _stage = CastbarStage.Idle;
                }
            }

            TurnoutLevel turnout = TurnoutLevel.None;
            if (_stage == CastbarStage.Turnout)
            {
                if (fadeRatio <= _cfg.Fade100Ratio) turnout = TurnoutLevel.Fade100;
                else if (fadeRatio <= _cfg.Fade50Ratio) turnout = TurnoutLevel.Fade50;
                else turnout = TurnoutLevel.Fade0;
            }

            return new CastbarPhaseResult(
                _macroStage, _stage, turnout,
                fillRatio, fillPos, hasEmpty,
                intensity, intensityAlpha,
                fadeRatio, _baseline, _peak,
                _hueCenter);
        }

        /// <summary>
        /// High-level phase classification that is meant to match human labels:
        /// Fill -> TurnLight -> Turnout, while being robust to background changes.
        /// 规则尽量只用规范化后的亮?/ 填充比例和内部状态，不依赖外部文件名?
        /// </summary>
        private void UpdateMacroStage(bool hasBar, bool isFull, double fillRatio, double fillEnergy, double intensity, double fadeRatio)
        {
            double norm = fadeRatio;
            if (double.IsNaN(norm) || double.IsInfinity(norm))
                norm = 0.0;

            double dInt = intensity - _lastMixedIntensity;
            double dFill = fillEnergy - _lastFillRatio;

            switch (_macroStage)
            {
                case CastbarStage.Idle:
                    // 有施法条出现（哪怕未满），进入填充?
                    if (hasBar)
                    {
                        _macroStage = CastbarStage.Fill;
                        _macroFillFrames = 0;
                        _macroFillTotalFrames = 0;
                        _macroLightFrames = 0;
                        _macroTurnLightFrames = 0;
                        _macroTurnoutFrames = 0;
                        _fullStableFrames = 0;
                    }
                    break;

                case CastbarStage.Fill:
                    _macroFillFrames++;
                    _macroFillTotalFrames++;

                    int shortSteps = Math.Min(4, Math.Max(1, _histCount - 1));
                    int longSteps = Math.Min(_cfg.MacroHistorySize - 1, Math.Max(2, _histCount / 2));
                    double fillDeltaShort = WindowDelta(_rHist, shortSteps);
                    double fillDeltaLong = WindowDelta(_rHist, longSteps);
                    double slopeShort = fillDeltaShort / Math.Max(1, shortSteps);
                    double slopeLong = fillDeltaLong / Math.Max(1, longSteps);

                    bool stableFull = isFull && slopeShort <= 0.0080;
                    if (stableFull)
                        _fullStableFrames++;
                    else if (!isFull)
                        _fullStableFrames = 0;

                    bool fillAlmostFull = fillEnergy >= 0.99 || (isFull && _fullStableFrames > 0);
                    bool fillSlowing = slopeShort <= 0.0005 && slopeLong <= 0.0010;
                    bool fillDone = isFull && _fullStableFrames >= _cfg.MacroFullStableFrames;

                    if (fillDone)
                    {
                        _macroStage = CastbarStage.TurnLight;
                        _macroTurnLightFrames = 0;
                        _macroTurnoutFrames = 0;
                        _localPeak = intensity;
                    }
                    break;

                case CastbarStage.TurnLight:
                    _macroTurnLightFrames++;

                    if (intensity > _localPeak + 1e-5)
                    {
                        _localPeak = intensity;
                        _macroLightFrames = 0;
                    }
                    else
                    {
                        _macroLightFrames++;
                    }

                    int iShortSteps = Math.Min(3, Math.Max(1, _histCount - 1));
                    int iLongSteps = Math.Min(6, Math.Max(1, _histCount - 1));
                    double dIShort = WindowDelta(_iHist, iShortSteps);
                    double dILong = WindowDelta(_iHist, iLongSteps);

                    double fadePeak = RecentMax(_fadeHist);
                    double fadeDrop = fadePeak - norm;
                    int fadeSteps = Math.Min(3, Math.Max(1, _histCount - 1));
                    double fadeDeltaShort = WindowDelta(_fadeHist, fadeSteps);
                    double fadeSlopeShort = fadeDeltaShort / Math.Max(1, fadeSteps);
                    double dropAbs = _localPeak - intensity;
                    double relDrop = (_localPeak > 1e-6) ? dropAbs / _localPeak : 0.0;

                    bool slopeDown = dIShort < -0.0025 && dILong < -0.0012;
                    bool fadingEnough = fadeDrop >= 0.05 || fadeSlopeShort < -0.0050;
                    bool peaked = dropAbs >= _cfg.DropFromPeak && _macroLightFrames >= 2;
                    bool dropSignal =
                        peaked &&
                        (
                            (fadingEnough && dropAbs >= _cfg.DropFromPeak) ||
                            (slopeDown && relDrop >= _cfg.MacroLightDrop)
                        );

                    if (dropSignal)
                        _macroTurnoutFrames++;
                    else
                        _macroTurnoutFrames = 0;

                    if ((_macroTurnoutFrames >= 3 && _macroTurnLightFrames >= _cfg.MacroTurnLightMinFrames) ||
                        _macroTurnLightFrames >= _cfg.MacroTurnLightMaxFrames)
                    {
                        _macroStage = CastbarStage.Turnout;
                        _macroTurnoutFrames = 0;
                    }
                    break;

                case CastbarStage.Turnout:
                    bool faded = (norm <= _cfg.Fade100Ratio) || (intensity <= _baseline + _cfg.EndDelta);
                    bool barGone = !hasBar || norm <= 0.15;
                    if (barGone && faded)
                    {
                        _macroStage = CastbarStage.Idle;
                        _macroFillFrames = 0;
                        _macroFillTotalFrames = 0;
                        _macroLightFrames = 0;
                        _macroTurnLightFrames = 0;
                        _macroTurnoutFrames = 0;
                        _fullStableFrames = 0;
                    }
                    break;

            }

            _lastMixedIntensity = intensity;
            _lastFadeRatio = norm;
            _lastFillRatio = fillEnergy;
        }

        private void AddHistory(double intensity, double fade, double fillEnergy)
        {
            _iHist[_histPos] = intensity;
            _fadeHist[_histPos] = fade;
            _rHist[_histPos] = fillEnergy;
            _histPos = (_histPos + 1) % _iHist.Length;
            if (_histCount < _iHist.Length) _histCount++;
        }

        private double RecentMax(double[] arr)
        {
            double max = double.MinValue;
            int n = _histCount;
            for (int i = 0; i < n; i++)
                if (arr[i] > max) max = arr[i];
            return n > 0 ? max : 0.0;
        }

        private double RecentMin(double[] arr)
        {
            double min = double.MaxValue;
            int n = _histCount;
            for (int i = 0; i < n; i++)
                if (arr[i] < min) min = arr[i];
            return n > 0 ? min : 0.0;
        }

        private double WindowDelta(double[] arr, int backSteps)
        {
            int n = _histCount;
            if (n < 2) return 0.0;
            int cap = arr.Length;
            if (cap <= 1) return 0.0;
            backSteps = ClampInt(backSteps, 1, Math.Min(n - 1, cap - 1));
            int lastIdx = (_histPos - 1 + cap) % cap;
            int prevIdx = (lastIdx - backSteps + cap) % cap;
            return arr[lastIdx] - arr[prevIdx];
        }

        private void StartFillStage(double mixedIntensity)
        {
            _stage = CastbarStage.Fill;
            _fillDoneCount = 0;
            _turnoutCount = 0;
            _endCount = 0;
            _peak = Math.Max(mixedIntensity, _baseline + 1e-3);
        }

        private double MixIntensity(double intensity, double intensityAlpha)
        {
            if (_cfg.UseAlphaIntensity && intensityAlpha > 1e-9 && intensityAlpha < 0.999)
                return (1.0 - _cfg.AlphaWeight) * intensity + _cfg.AlphaWeight * intensityAlpha;
            return intensity;
        }

        private void MaybeUpdateHue(Mat hsv8u, Mat? alpha01)
        {
            using var h8 = new Mat();
            using var s8 = new Mat();
            using var v8 = new Mat();

            Cv2.ExtractChannel(hsv8u, h8, 0);
            Cv2.ExtractChannel(hsv8u, s8, 1);
            Cv2.ExtractChannel(hsv8u, v8, 2);

            using var hC = EnsureContinuous(h8);
            using var sC = EnsureContinuous(s8);
            using var vC = EnsureContinuous(v8);

            hC.GetArray(out byte[] hArr);
            sC.GetArray(out byte[] sArr);
            vC.GetArray(out byte[] vArr);

            float[]? aArr = null;
            Mat? aC = null;
            bool disposeAC = false;
            if (alpha01 != null)
            {
                if (alpha01.IsContinuous())
                {
                    aC = alpha01;
                }
                else
                {
                    aC = alpha01.Clone();
                    disposeAC = true;
                }

                aC.GetArray(out float[] tmp);
                aArr = tmp;
            }

            double[] hist = new double[180];

            int count = 0;
            int n = hArr.Length;

            for (int i = 0; i < n; i++)
            {
                double ss = sArr[i] / 255.0;
                double vv = vArr[i] / 255.0;
                if (ss < _cfg.HueUpdateSThr || vv < _cfg.HueUpdateVThr) continue;
                if (aArr != null && aArr[i] < _cfg.HueUpdateAThr) continue;

                int hh = hArr[i];
                if (hh < 0 || hh >= 180) continue;

                double w = ss * vv;
                hist[hh] += w;
                count++;
            }

            if (disposeAC)
                aC?.Dispose();

            if (count < _cfg.HueUpdateMinPixels)
                return;

            int peak = 0;
            double best = hist[0];
            for (int i = 1; i < 180; i++)
            {
                if (hist[i] > best)
                {
                    best = hist[i];
                    peak = i;
                }
            }

            int win = Math.Max(1, _cfg.HueUpdateWindow);

            double sumRe = 0.0;
            double sumIm = 0.0;
            for (int d = -win; d <= win; d++)
            {
                int idx = (peak + d) % 180;
                if (idx < 0) idx += 180;

                double ww = hist[idx] + 1e-6;
                double ang = (idx / 180.0) * 2.0 * Math.PI;
                sumRe += ww * Math.Cos(ang);
                sumIm += ww * Math.Sin(ang);
            }

            double ang2 = Math.Atan2(sumIm, sumRe);
            if (ang2 < 0.0) ang2 += 2.0 * Math.PI;

            double hueNew = (ang2 / (2.0 * Math.PI)) * 180.0;

            double hueNext = CircularEma(_hueCenter, hueNew, _cfg.HueUpdateAlpha);

            if (Math.Abs(hueNext - _hueCenter) > 1e-6)
            {
                _hueCenter = hueNext;

                _hueLut.Dispose();
                _hueLut = BuildHueLutMat(_hueCenter, _cfg.HueSigma);
            }
        }

        private Mat HueWeight01(Mat hsv8u)
        {
            using var h8 = new Mat();
            Cv2.ExtractChannel(hsv8u, h8, 0);

            using var hw8 = new Mat();
            Cv2.LUT(h8, _hueLut, hw8);

            var hw01 = new Mat();
            hw8.ConvertTo(hw01, MatType.CV_32F, 1.0 / 255.0);
            return hw01;
        }

        private static Mat Channel01(Mat hsv8u, int channel)
        {
            using var ch8 = new Mat();
            Cv2.ExtractChannel(hsv8u, ch8, channel);
            var ch01 = new Mat();
            ch8.ConvertTo(ch01, MatType.CV_32F, 1.0 / 255.0);
            return ch01;
        }

        private static Mat BuildHueLutMat(double center, double sigma)
        {
            if (sigma <= 0.1) sigma = 0.1;
            double ss = sigma * sigma;

            byte[] lut = new byte[256];

            for (int i = 0; i < 256; i++)
            {
                double h = i;

                double d1 = ((h - center) % 180.0 + 180.0) % 180.0;
                double d2 = ((center - h) % 180.0 + 180.0) % 180.0;
                double d = Math.Min(d1, d2);

                double w = Math.Exp(-(d * d) / (2.0 * ss));
                int b = (int)Math.Round(w * 255.0);
                lut[i] = (byte)ClampInt(b, 0, 255);
            }

            var m = new Mat(1, 256, MatType.CV_8U);
            Marshal.Copy(lut, 0, m.Data, lut.Length);
            return m;
        }

        private static double CircularEma(double prev, double next, double alpha)
        {
            alpha = Clamp01(alpha);

            double pr = (prev / 180.0) * 2.0 * Math.PI;
            double nr = (next / 180.0) * 2.0 * Math.PI;

            double pre = Math.Cos(pr), pim = Math.Sin(pr);
            double nre = Math.Cos(nr), nim = Math.Sin(nr);

            double re = (1.0 - alpha) * pre + alpha * nre;
            double im = (1.0 - alpha) * pim + alpha * nim;

            double ang = Math.Atan2(im, re);
            if (ang < 0.0) ang += 2.0 * Math.PI;

            return (ang / (2.0 * Math.PI)) * 180.0;
        }

        private static Mat EnsureBgrOrBgra(Mat img)
        {
            int ch = img.Channels();
            if (ch == 3 || ch == 4) return img.Clone();

            if (ch == 1)
            {
                var bgr = new Mat();
                Cv2.CvtColor(img, bgr, ColorConversionCodes.GRAY2BGR);
                return bgr;
            }

            throw new ArgumentException("Unsupported image channels.");
        }

        private static Mat EnsureContinuous(Mat m)
        {
            if (m.IsContinuous()) return m;
            return m.Clone();
        }

        private static Mat ResizeArea(Mat src, double scale)
        {
            int newW = Math.Max(4, (int)Math.Round(src.Width * scale));
            int newH = Math.Max(4, (int)Math.Round(src.Height * scale));
            var dst = new Mat();
            Cv2.Resize(src, dst, new Size(newW, newH), 0, 0, InterpolationFlags.Area);
            return dst;
        }

        private static Rect ClipRect(Rect r, int width, int height)
        {
            int x = Math.Max(0, r.X);
            int y = Math.Max(0, r.Y);
            int w = Math.Min(r.Width, width - x);
            int h = Math.Min(r.Height, height - y);
            if (w <= 0 || h <= 0) throw new ArgumentException("ROI is outside image bounds.");
            return new Rect(x, y, w, h);
        }

        private static double Mean(float[] arr, int start, int len)
        {
            double sum = 0.0;
            int end = start + len;
            for (int i = start; i < end; i++)
                sum += arr[i];
            return sum / Math.Max(1, len);
        }

        private static float Percentile(float[] arr, float q01)
        {
            if (arr.Length == 0) return 0f;
            int n = arr.Length;
            int k = (int)Math.Round(q01 * (n - 1));
            k = ClampInt(k, 0, n - 1);
            return QuickSelect.SelectKth(arr, k);
        }

        private static double TopKMean(float[] data, double frac)
        {
            int n = data.Length;
            if (n <= 0) return 0.0;

            int k = (int)Math.Round(n * frac);
            if (k <= 0) k = 1;
            if (k > n) k = n;

            int kthIndex = n - k;
            var copy = (float[])data.Clone();
            float th = QuickSelect.SelectKth(copy, kthIndex);

            double sumGreater = 0.0;
            int cntGreater = 0;
            int cntEqual = 0;

            for (int i = 0; i < n; i++)
            {
                float v = data[i];
                if (v > th)
                {
                    sumGreater += v;
                    cntGreater++;
                }
                else if (v == th)
                {
                    cntEqual++;
                }
            }

            int need = k - cntGreater;
            if (need < 0) need = 0;
            if (need > cntEqual) need = cntEqual;

            double sum = sumGreater + need * th;
            return sum / k;
        }

        private static int ClampInt(int v, int lo, int hi)
        {
            if (v < lo) return lo;
            if (v > hi) return hi;
            return v;
        }

        private static double Clamp01(double x)
        {
            if (x < 0.0) return 0.0;
            if (x > 1.0) return 1.0;
            return x;
        }

        public void Dispose()
        {
            _hueLut.Dispose();
        }

        private static class QuickSelect
        {
            public static float SelectKth(float[] arr, int k)
            {
                if (k < 0 || k >= arr.Length) throw new ArgumentOutOfRangeException(nameof(k));

                int left = 0;
                int right = arr.Length - 1;

                while (true)
                {
                    if (left == right) return arr[left];

                    int pivotIndex = (left + right) >> 1;
                    pivotIndex = Partition(arr, left, right, pivotIndex);

                    if (k == pivotIndex) return arr[k];
                    if (k < pivotIndex) right = pivotIndex - 1;
                    else left = pivotIndex + 1;
                }
            }

            private static int Partition(float[] arr, int left, int right, int pivotIndex)
            {
                float pivotValue = arr[pivotIndex];
                Swap(arr, pivotIndex, right);

                int storeIndex = left;
                for (int i = left; i < right; i++)
                {
                    if (arr[i] < pivotValue)
                    {
                        Swap(arr, storeIndex, i);
                        storeIndex++;
                    }
                }

                Swap(arr, right, storeIndex);
                return storeIndex;
            }

            private static void Swap(float[] arr, int i, int j)
            {
                if (i == j) return;
                float t = arr[i];
                arr[i] = arr[j];
                arr[j] = t;
            }
        }
    }
}
