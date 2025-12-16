using System;
using System.IO;
using System.Text.Json;
using VisionSystem.Effects;
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
        Fade50 = 1,
        Fade100 = 2
    }

    public readonly struct CastbarPhaseResult
    {
        public readonly CastbarStage MacroStage;
        public readonly CastbarStage MicroStage;
        public readonly TurnoutLevel Turnout;

        public readonly double FillRatio;
        public readonly double EnergyFill;

        public readonly bool HasEmpty;
        public readonly bool HasBar;
        public readonly bool IsFull;

        public readonly double Intensity;
        public readonly double Baseline;
        public readonly double Peak;
        public readonly double HueCenterDeg;

        public readonly int EffectiveWidth;
        public readonly int RightCapCutX;
        public readonly double FadeRatio;
        public readonly double IntensityAlpha;

        public CastbarPhaseResult(
            CastbarStage macroStage,
            CastbarStage microStage,
            TurnoutLevel turnout,
            double fillRatio,
            double energyFill,
            bool hasEmpty,
            bool hasBar,
            bool isFull,
            double intensity,
            double baseline,
            double peak,
            double hueCenterDeg,
            int effectiveWidth,
            int rightCapCutX,
            double fadeRatio,
            double intensityAlpha)
        {
            MacroStage = macroStage;
            MicroStage = microStage;
            Turnout = turnout;
            FillRatio = fillRatio;
            EnergyFill = energyFill;
            HasEmpty = hasEmpty;
            HasBar = hasBar;
            IsFull = isFull;
            Intensity = intensity;
            Baseline = baseline;
            Peak = peak;
            HueCenterDeg = hueCenterDeg;
            EffectiveWidth = effectiveWidth;
            RightCapCutX = rightCapCutX;
            FadeRatio = fadeRatio;
            IntensityAlpha = intensityAlpha;
        }

        public CastbarStage Stage => MacroStage;
        public CastbarStage InternalStage => MicroStage;
        public double HueCenter => HueCenterDeg;
    }

    public sealed class CastbarPhaseConfig
    {
        public static CastbarPhaseConfig LoadOrDefault(string path)
        {
            try
            {
                if (!File.Exists(path)) return new CastbarPhaseConfig();
                var json = File.ReadAllText(path);
                var cfg = JsonSerializer.Deserialize<CastbarPhaseConfig>(json);
                return cfg ?? new CastbarPhaseConfig();
            }
            catch
            {
                return new CastbarPhaseConfig();
            }
        }

        public static CastbarPhaseConfig FromJson(string path) => LoadOrDefault(path);

        public void Save(string path)
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(path, json);
        }
    }

    public sealed class CastbarPhaseDetector : IDisposable
    {
        private readonly SparkDetector _detector = new();

        public CastbarPhaseDetector(CastbarPhaseConfig? cfg = null)
        {
            _ = cfg;
        }

        public void Dispose()
        {
        }

        public bool Analyze(Mat frame, Rect roi, out CastbarPhaseResult result)
            => Analyze(frame, roi, 1.0, out result);

        public bool Analyze(Mat frame, Rect roi, double scale, out CastbarPhaseResult result)
        {
            if (frame == null) throw new ArgumentNullException(nameof(frame));
            if (roi.Width <= 0 || roi.Height <= 0) throw new ArgumentException("ROI invalid.");

            using var roiMat = new Mat(frame, roi);
            Mat work = roiMat;
            Mat? resized = null;
            if (Math.Abs(scale - 1.0) > 1e-6 && scale > 0.0)
            {
                int nw = Math.Max(1, (int)Math.Round(roiMat.Width * scale));
                int nh = Math.Max(1, (int)Math.Round(roiMat.Height * scale));
                resized = new Mat();
                Cv2.Resize(roiMat, resized, new OpenCvSharp.Size(nw, nh), 0, 0, InterpolationFlags.Area);
                work = resized;
            }

            SparkResult sparkResult;
            bool ready;
            try
            {
                ready = _detector.ProcessFrame(work, out sparkResult);
            }
            finally
            {
                resized?.Dispose();
            }

            if (!ready)
            {
                result = default;
                return false;
            }

            result = BuildResult(work.Width, sparkResult);
            return true;
        }

        public bool Analyze(Mat roiBgrOrBgra, out CastbarPhaseResult result)
        {
            if (roiBgrOrBgra == null) throw new ArgumentNullException(nameof(roiBgrOrBgra));
            return Analyze(roiBgrOrBgra, new Rect(0, 0, roiBgrOrBgra.Width, roiBgrOrBgra.Height), 1.0, out result);
        }

        public CastbarPhaseResult Analyze(Mat roiBgrOrBgra)
        {
            if (roiBgrOrBgra == null) throw new ArgumentNullException(nameof(roiBgrOrBgra));
            Analyze(roiBgrOrBgra, out var result);
            return result;
        }

        private CastbarPhaseResult BuildResult(int width, SparkResult spark)
        {
            CastbarStage stage = spark.State switch
            {
                SparkState.Fill => CastbarStage.Fill,
                SparkState.TurnLight => CastbarStage.TurnLight,
                SparkState.Fade => CastbarStage.Turnout,
                _ => CastbarStage.Idle
            };

            double fillRatio = Math.Clamp(spark.Progress, 0.0, 1.0);
            double fadeRatio = stage == CastbarStage.Turnout && spark.State == SparkState.Fade ? 1.0 : 0.0;
            double energyFill = fillRatio;
            bool hasBar = stage != CastbarStage.Idle;
            bool isFull = stage == CastbarStage.TurnLight || stage == CastbarStage.Turnout;
            bool hasEmpty = !hasBar || (stage == CastbarStage.Fill && fillRatio < 0.98);

            TurnoutLevel turnout = TurnoutLevel.None;
            if (stage == CastbarStage.Turnout)
            {
                turnout = spark.IsFade50 ? TurnoutLevel.Fade100 : TurnoutLevel.Fade50;
            }
            else if (spark.IsFade50)
            {
                turnout = TurnoutLevel.Fade100;
            }

            double intensity = spark.SparkDetected ? 1.0 : 0.0;
            double baseline = spark.BandRight - spark.BandLeft;
            double peak = spark.SparkIndex;

            return new CastbarPhaseResult(
                stage,
                stage,
                turnout,
                fillRatio,
                energyFill,
                hasEmpty,
                hasBar,
                isFull,
                intensity,
                baseline,
                peak,
                0.0,
                width,
                spark.SparkIndex,
                fadeRatio,
                0.0);
        }
    }
}
