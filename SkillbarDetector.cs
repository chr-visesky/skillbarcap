using System;
using OpenCvSharp;

namespace VisionSystem.Effects
{
    public enum SparkState
    {
        Idle,
        Fill,
        TurnLight,
        Fade
    }

    public struct SparkResult
    {
        public SparkState State;
        public double Progress;
        public bool IsFade50;

        public bool SparkDetected;
        public int SparkIndex;
        public int BandLeft;
        public int BandRight;
    }

    internal struct FrameInfo
    {
        public bool SparkRaw;
        public int SparkIdxRaw;
        public int BandL;
        public int BandR;

        public int BandRowStart;
        public int BandRowEnd;

        public double Energy;           // V mean on band rows (peak detect)
        public double NonSparkEnergy;    // V mean on band rows excluding spark band (trend + fade baseline)
    }

    public sealed class SparkDetector : IDisposable
    {
        // Fixed settings (per your constraints)
        private const int JUMP_THRESHOLD = 18;
        private const double LEFT_SKIP_RATIO = 0.10;

        // 0.5% anti-dark jitter on V(0..255)
        private const double ENERGY_EPS = 255.0 * 0.00314; // 0.8

        // State machine
        private SparkState _state = SparkState.Idle;
        private int _maxSparkX = 0;

        // last spark frame baseline (for post-fill trend decision)
        private bool _hasLastSpark = false;
        private double _lastSparkNonSparkEnergy = 0.0;

        // No-spark baseline (first confirmed no-spark frame after Fill ends)
        private bool _hasNoSparkBaseline = false;
        private readonly Mat _noSparkBaselineGray = new Mat();
        private int _baselineRowStart = 0;
        private int _baselineRowEnd = 0;
        private double _baselineNonSparkEnergy = 0.0;

        // 1-frame latency pipeline: output "curr" when we have prev/curr/next
        private bool _hasPrev = false;
        private bool _hasCurr = false;

        private readonly Mat _prevBgr = new Mat();
        private readonly Mat _currBgr = new Mat();
        private readonly Mat _nextBgr = new Mat();

        private FrameInfo _prevInfo;
        private FrameInfo _currInfo;

        // Reused mats
        private readonly Mat _gray = new Mat();
        private readonly Mat _hsv = new Mat();
        private readonly Mat _chanS = new Mat();
        private readonly Mat _chanV = new Mat();

        private readonly Mat _jump = new Mat();
        private readonly Mat _mask = new Mat();
        private readonly Mat _reduceVotes = new Mat();

        private readonly Mat _rowMeanS = new Mat();
        private readonly Mat _colMeanV = new Mat();

        private int[] _votesBuf = Array.Empty<int>();
        private float[] _rowBuf = Array.Empty<float>();
        private float[] _colBuf = Array.Empty<float>();
        private float[] _colSort = Array.Empty<float>();

        /// <summary>
        /// Online API (single interface): feed frames sequentially.
        /// Returns true once it can output the classification for the "curr" frame (1-frame latency).
        /// </summary>
        public bool ProcessFrame(Mat roiBgrOrGray, out SparkResult resultForCurrFrame)
        {
            resultForCurrFrame = default(SparkResult);
            if (roiBgrOrGray == null || roiBgrOrGray.Empty()) return false;

            roiBgrOrGray.CopyTo(_nextBgr);
            FrameInfo nextInfo = AnalyzeFrame(_nextBgr);

            if (!_hasCurr)
            {
                _nextBgr.CopyTo(_currBgr);
                _currInfo = nextInfo;
                _hasCurr = true;
                return false;
            }

            if (!_hasPrev)
            {
                _currBgr.CopyTo(_prevBgr);
                _prevInfo = _currInfo;

                _nextBgr.CopyTo(_currBgr);
                _currInfo = nextInfo;

                _hasPrev = true;
                return false;
            }

            SparkResult r = ClassifyMiddle(_prevInfo, _currInfo, nextInfo);
            resultForCurrFrame = r;

            // Slide window
            _currBgr.CopyTo(_prevBgr);
            _prevInfo = _currInfo;

            _nextBgr.CopyTo(_currBgr);
            _currInfo = nextInfo;

            return true;
        }

        private FrameInfo AnalyzeFrame(Mat bgrOrGray)
        {
            FrameInfo info = new FrameInfo();

            EnsureGray(bgrOrGray, _gray);
            EnsureHsvAndChannels(bgrOrGray, _hsv, _chanS, _chanV);

            int bandRowStart, bandRowEnd;
            FindBandRowsByS(_chanS, out bandRowStart, out bandRowEnd);
            info.BandRowStart = bandRowStart;
            info.BandRowEnd = bandRowEnd;

            info.Energy = MeanOnRows(_chanV, bandRowStart, bandRowEnd);

            int bandL, bandR;
            int sparkIdx = DetectSparkOnBand(_gray, _chanV, bandRowStart, bandRowEnd, out bandL, out bandR);
            info.SparkRaw = (sparkIdx >= 0);
            info.SparkIdxRaw = sparkIdx;
            info.BandL = bandL;
            info.BandR = bandR;

            info.NonSparkEnergy = ComputeNonSparkEnergy(_chanV, bandRowStart, bandRowEnd, info.SparkRaw, bandL, bandR);

            return info;
        }

        // =======================
        // Minimal change core:
        // 1) Confirm no-spark by curr & next.
        // 2) Fill ends => decide TurnLight vs Fade by NonSparkEnergy trend with EPS.
        // 3) TurnLight peak detect with EPS to avoid jitter false-peak.
        // =======================
        private SparkResult ClassifyMiddle(FrameInfo prev, FrameInfo curr, FrameInfo next)
        {
            SparkResult result = new SparkResult();
            result.IsFade50 = false;

            // Confirm "curr has no spark" only if curr and next both have no spark
            bool currAbsentConfirmed = (!curr.SparkRaw) && (!next.SparkRaw);

            // Prevent single-frame dropout causing Fill->end:
            // if prev had spark and curr is not confirmed absent, treat as spark-present
            bool currSparkForState = curr.SparkRaw || (!currAbsentConfirmed && prev.SparkRaw);

            int currIdx = -1, currL = -1, currR = -1;
            if (curr.SparkRaw)
            {
                currIdx = curr.SparkIdxRaw;
                currL = curr.BandL;
                currR = curr.BandR;
            }
            else if (currSparkForState)
            {
                currIdx = prev.SparkIdxRaw;
                currL = prev.BandL;
                currR = prev.BandR;
            }

            result.SparkDetected = currSparkForState;
            result.SparkIndex = currIdx;
            result.BandLeft = currL;
            result.BandRight = currR;

            SparkState outputState = _state;
            SparkState nextState = _state;

            if (currSparkForState)
            {
                // Hard rule: spark present => Fill
                outputState = SparkState.Fill;
                nextState = SparkState.Fill;

                if (currIdx >= 0 && currIdx > _maxSparkX) _maxSparkX = currIdx;

                // Update last-spark baseline for post-fill trend
                _hasLastSpark = true;
                _lastSparkNonSparkEnergy = curr.NonSparkEnergy;

                // While spark exists, no-spark baseline should be considered not yet started
                _hasNoSparkBaseline = false;
            }
            else
            {
                // curr confirmed no spark
                if (_state == SparkState.Idle)
                {
                    outputState = SparkState.Idle;
                    nextState = SparkState.Idle;
                    ResetIfIdle();
                }
                else if (_state == SparkState.Fill)
                {
                    if (!_hasLastSpark)
                    {
                        outputState = SparkState.Idle;
                        nextState = SparkState.Idle;
                        ResetAll();
                    }
                    else
                    {
                        double lastE = _lastSparkNonSparkEnergy;
                        double currE = curr.NonSparkEnergy;
                        double nextE = next.NonSparkEnergy;

                        // 0.5% anti-dark jitter:
                        bool toTurnLight = IsNonDecreasingWithEps(lastE, currE, nextE, ENERGY_EPS);
                        bool toFade = IsStrictDecreasingWithEps(lastE, currE, nextE, ENERGY_EPS);

                        if (toTurnLight || toFade)
                        {
                            // FIRST confirmed no-spark frame => cache baseline, regardless of TurnLight or Fade
                            CacheNoSparkBaselineFromCurr(curr);

                            outputState = toTurnLight ? SparkState.TurnLight : SparkState.Fade;
                            nextState = outputState;
                        }
                        else
                        {
                            // Ambiguous: keep Fill (will resolve next tick; allowed by your 1-frame latency)
                            outputState = SparkState.Fill;
                            nextState = SparkState.Fill;
                        }
                    }
                }
                else if (_state == SparkState.TurnLight)
                {
                    // Still in TurnLight unless we confirm a real peak.
                    outputState = SparkState.TurnLight;

                    // ======= Minimal change #1: add EPS to peak detection =======
                    // Avoid jitter making (curr > next) by tiny amount become a fake peak.
                    // peak：左侧不变暗（允许抖动），右侧必须明显变暗（带EPS）
                    bool isPeak = (curr.Energy >= prev.Energy - ENERGY_EPS) && (curr.Energy > next.Energy + ENERGY_EPS);
                    nextState = isPeak ? SparkState.Fade : SparkState.TurnLight;
                    // ======= End minimal change #1 =======
                }
                else if (_state == SparkState.Fade)
                {
                    outputState = SparkState.Fade;

                    if (!_hasNoSparkBaseline)
                    {
                        CacheNoSparkBaselineFromPrevNoSpark(prev);
                    }

                    // Fade ends by scalar energy back to baseline (NonSparkEnergy)
                    if (curr.NonSparkEnergy <= _baselineNonSparkEnergy)
                    {
                        nextState = SparkState.Idle;
                        result.IsFade50 = true;
                        ResetAll();
                    }
                    else
                    {
                        nextState = SparkState.Fade;
                    }
                }
            }

            result.State = outputState;

            if (outputState == SparkState.Fill)
            {
                int w = GetWidthSafe();
                result.Progress = Math.Min(1.0, Math.Max(0.0, (double)_maxSparkX / Math.Max(1, w - 1)));
            }
            else if (outputState == SparkState.TurnLight || outputState == SparkState.Fade)
            {
                result.Progress = 1.0;
            }
            else
            {
                result.Progress = 0.0;
            }

            _state = nextState;
            return result;
        }

        // ======= Minimal change #2: shared trend helpers (used by Fill-end decision) =======
        private static bool IsNonDecreasingWithEps(double lastE, double currE, double nextE, double eps)
        {
            // "没有变暗就算还在变亮": allow small dark jitter within eps.
            return (currE >= lastE - eps) && (nextE >= currE - eps);
        }

        private static bool IsStrictDecreasingWithEps(double lastE, double currE, double nextE, double eps)
        {
            return (currE < lastE - eps) && (nextE < currE - eps);
        }
        // ======= End minimal change #2 =======

        private void CacheNoSparkBaselineFromCurr(FrameInfo currInfo)
        {
            EnsureGray(_currBgr, _gray);
            _gray.CopyTo(_noSparkBaselineGray);
            _baselineRowStart = currInfo.BandRowStart;
            _baselineRowEnd = currInfo.BandRowEnd;
            _baselineNonSparkEnergy = currInfo.NonSparkEnergy;
            _hasNoSparkBaseline = true;
        }

        private void CacheNoSparkBaselineFromPrevNoSpark(FrameInfo prevInfo)
        {
            EnsureGray(_prevBgr, _gray);
            _gray.CopyTo(_noSparkBaselineGray);
            _baselineRowStart = prevInfo.BandRowStart;
            _baselineRowEnd = prevInfo.BandRowEnd;
            _baselineNonSparkEnergy = prevInfo.NonSparkEnergy;
            _hasNoSparkBaseline = true;
        }

        private int GetWidthSafe()
        {
            return _currBgr.Empty() ? 1 : _currBgr.Cols;
        }

        private void EnsureGray(Mat src, Mat dstGray)
        {
            int ch = src.Channels();
            if (ch == 1) { src.CopyTo(dstGray); return; }
            if (ch == 4) { Cv2.CvtColor(src, dstGray, ColorConversionCodes.BGRA2GRAY); return; }
            Cv2.CvtColor(src, dstGray, ColorConversionCodes.BGR2GRAY);
        }

        private void EnsureHsvAndChannels(Mat src, Mat hsv, Mat chanS, Mat chanV)
        {
            int ch = src.Channels();
            if (ch == 1)
            {
                using (Mat bgr = new Mat())
                {
                    Cv2.CvtColor(src, bgr, ColorConversionCodes.GRAY2BGR);
                    Cv2.CvtColor(bgr, hsv, ColorConversionCodes.BGR2HSV);
                }
            }
            else
            {
                Cv2.CvtColor(src, hsv, ColorConversionCodes.BGR2HSV);
            }

            Cv2.ExtractChannel(hsv, chanS, 1);
            Cv2.ExtractChannel(hsv, chanV, 2);
        }

        private void FindBandRowsByS(Mat sChannel, out int bandStart, out int bandEnd)
        {
            int h = sChannel.Rows;
            bandStart = 0;
            bandEnd = h - 1;

            _rowMeanS.Create(rows: h, cols: 1, type: MatType.CV_32F);
            Cv2.Reduce(sChannel, _rowMeanS, ReduceDimension.Column, ReduceTypes.Avg, MatType.CV_32F);

            EnsureRowBuffers(h);
            var idx = _rowMeanS.GetGenericIndexer<float>();

            float minV = float.MaxValue;
            float maxV = float.MinValue;
            for (int y = 0; y < h; y++)
            {
                float v = idx[y, 0];
                _rowBuf[y] = v;
                if (v < minV) minV = v;
                if (v > maxV) maxV = v;
            }

            float thr = (minV + maxV) * 0.5f;

            int bestS = 0, bestE = h - 1, bestLen = 0;
            int i = 0;
            while (i < h)
            {
                if (_rowBuf[i] < thr) { i++; continue; }
                int s = i;
                while (i < h && _rowBuf[i] >= thr) i++;
                int e = i - 1;
                int len = e - s + 1;
                if (len > bestLen)
                {
                    bestLen = len;
                    bestS = s;
                    bestE = e;
                }
            }

            if (bestLen >= 3)
            {
                bandStart = bestS;
                bandEnd = bestE;
            }
        }

        private int DetectSparkOnBand(Mat gray, Mat vChannel, int bandRowStart, int bandRowEnd, out int bandLeft, out int bandRight)
        {
            bandLeft = -1;
            bandRight = -1;

            int h = gray.Rows;
            int w = gray.Cols;
            if (w < 2 || h < 2) return -1;

            int bandH = bandRowEnd - bandRowStart + 1;
            if (bandH < 3) { bandRowStart = 0; bandRowEnd = h - 1; bandH = h; }

            int needVotes = (bandH / 2) + 1;

            int skipCol = Math.Max(1, (int)Math.Floor(w * LEFT_SKIP_RATIO));
            int ignoreDiff = Math.Max(0, skipCol - 1);

            _jump.Create(rows: h, cols: w - 1, type: MatType.CV_8U);
            using (Mat leftCols = gray.ColRange(0, w - 1))
            using (Mat rightCols = gray.ColRange(1, w))
            {
                Cv2.Subtract(rightCols, leftCols, _jump);
            }

            _mask.Create(rows: h, cols: w - 1, type: MatType.CV_8U);
            Cv2.Threshold(_jump, _mask, JUMP_THRESHOLD - 1, 255, ThresholdTypes.Binary);

            if (ignoreDiff > 0)
            {
                using (Mat ig = _mask.ColRange(0, ignoreDiff))
                {
                    ig.SetTo(0);
                }
            }

            using (Mat bandMask = _mask.RowRange(bandRowStart, bandRowEnd + 1))
            {
                _reduceVotes.Create(rows: 1, cols: w - 1, type: MatType.CV_32S);
                Cv2.Reduce(bandMask, _reduceVotes, ReduceDimension.Row, ReduceTypes.Sum, MatType.CV_32S);
            }

            EnsureVoteBuffers(w - 1);
            var vIdx = _reduceVotes.GetGenericIndexer<int>();
            for (int x = 0; x < w - 1; x++)
            {
                _votesBuf[x] = vIdx[0, x] / 255;
            }

            // strong-jump cluster must be unique
            int mergeGap = Math.Max(2, (int)Math.Round(w * 0.02));

            int clusterCount = 0;
            int clusterEnd = -1;

            int xPos = 0;
            while (xPos < w - 1)
            {
                if (_votesBuf[xPos] < needVotes) { xPos++; continue; }

                int s = xPos;
                while (xPos < w - 1 && _votesBuf[xPos] >= needVotes) xPos++;
                int e = xPos - 1;

                if (clusterCount == 0)
                {
                    clusterCount = 1;
                    clusterEnd = e;
                }
                else
                {
                    // merge close segments; otherwise it's multiple -> reject
                    if (s - clusterEnd <= mergeGap)
                    {
                        if (e > clusterEnd) clusterEnd = e;
                    }
                    else
                    {
                        clusterCount++;
                        break;
                    }
                }
            }

            if (clusterCount != 1) return -1;

            int seedCol = clusterEnd + 1;
            if (seedCol < skipCol) return -1;
            if (seedCol >= w) seedCol = w - 1;

            // Expand by bright band in V (robust for semi-transparent background)
            _colMeanV.Create(rows: 1, cols: w, type: MatType.CV_32F);
            using (Mat vBand = vChannel.RowRange(bandRowStart, bandRowEnd + 1))
            {
                Cv2.Reduce(vBand, _colMeanV, ReduceDimension.Row, ReduceTypes.Avg, MatType.CV_32F);
            }

            EnsureColBuffers(w);
            var cIdx = _colMeanV.GetGenericIndexer<float>();
            for (int i = 0; i < w; i++)
            {
                float vv = cIdx[0, i];
                _colBuf[i] = vv;
                _colSort[i] = vv;
            }

            float q97 = QuantileInPlace(_colSort, w, 0.97);

            int L = seedCol;
            int R = seedCol;

            while (L - 1 >= skipCol && _colBuf[L - 1] >= q97) L--;
            while (R + 1 < w && _colBuf[R + 1] >= q97) R++;

            if (R - L + 1 < 2) return -1;

            bandLeft = L;
            bandRight = R;
            return R;
        }

        private double ComputeNonSparkEnergy(Mat vChannel, int rowStart, int rowEnd, bool hasSpark, int bandL, int bandR)
        {
            if (rowEnd < rowStart) return Cv2.Mean(vChannel).Val0;

            using (Mat vBand = vChannel.RowRange(rowStart, rowEnd + 1))
            {
                if (!hasSpark || bandL < 0 || bandR < 0 || bandR < bandL)
                {
                    return Cv2.Mean(vBand).Val0;
                }

                int w = vBand.Cols;
                int leftW = Math.Max(0, Math.Min(w, bandL));
                int rightX = Math.Max(0, Math.Min(w, bandR + 1));
                int rightW = Math.Max(0, w - rightX);

                double sum = 0.0;
                int count = 0;

                if (leftW > 0)
                {
                    using (Mat left = vBand.ColRange(0, leftW))
                    {
                        sum += Cv2.Mean(left).Val0 * leftW;
                        count += leftW;
                    }
                }

                if (rightW > 0)
                {
                    using (Mat right = vBand.ColRange(rightX, w))
                    {
                        sum += Cv2.Mean(right).Val0 * rightW;
                        count += rightW;
                    }
                }

                if (count <= 0) return Cv2.Mean(vBand).Val0;
                return sum / count;
            }
        }

        private double MeanOnRows(Mat singleChannel, int rowStart, int rowEnd)
        {
            if (rowEnd < rowStart) return Cv2.Mean(singleChannel).Val0;
            using (Mat roi = singleChannel.RowRange(rowStart, rowEnd + 1))
            {
                return Cv2.Mean(roi).Val0;
            }
        }

        private static float QuantileInPlace(float[] data, int n, double q)
        {
            Array.Sort(data, 0, n);
            int idx = (int)Math.Floor(q * (n - 1));
            if (idx < 0) idx = 0;
            if (idx >= n) idx = n - 1;
            return data[idx];
        }

        private void EnsureVoteBuffers(int n)
        {
            if (_votesBuf.Length != n) _votesBuf = new int[n];
        }

        private void EnsureRowBuffers(int n)
        {
            if (_rowBuf.Length != n) _rowBuf = new float[n];
        }

        private void EnsureColBuffers(int n)
        {
            if (_colBuf.Length != n) _colBuf = new float[n];
            if (_colSort.Length != n) _colSort = new float[n];
        }

        private void ResetIfIdle()
        {
            _maxSparkX = 0;
            _hasLastSpark = false;
            _lastSparkNonSparkEnergy = 0.0;

            _hasNoSparkBaseline = false;
            _baselineRowStart = 0;
            _baselineRowEnd = 0;
            _baselineNonSparkEnergy = 0.0;
        }

        private void ResetAll()
        {
            _maxSparkX = 0;
            _hasLastSpark = false;
            _lastSparkNonSparkEnergy = 0.0;

            _hasNoSparkBaseline = false;
            _baselineRowStart = 0;
            _baselineRowEnd = 0;
            _baselineNonSparkEnergy = 0.0;
        }

        public void Dispose()
        {
            _noSparkBaselineGray?.Dispose();

            _prevBgr?.Dispose();
            _currBgr?.Dispose();
            _nextBgr?.Dispose();

            _gray?.Dispose();
            _hsv?.Dispose();
            _chanS?.Dispose();
            _chanV?.Dispose();

            _jump?.Dispose();
            _mask?.Dispose();
            _reduceVotes?.Dispose();

            _rowMeanS?.Dispose();
            _colMeanV?.Dispose();
        }
    }
}
