using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SkillbarCapture
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            try
            {
                RunAsync(args).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error: " + ex);
            }
        }

        private static async Task RunAsync(string[] args)
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return;
            }

            if (args[0].Equals("analyze", StringComparison.OrdinalIgnoreCase))
            {
                RunAnalyze(args);
                return;
            }

            if (args.Length < 2)
            {
                PrintUsage();
                return;
            }

            string hwndText = args[0];
            IntPtr hwnd;

            if (long.TryParse(hwndText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var hwndValue))
            {
                hwnd = (IntPtr)hwndValue;
            }
            else
            {
                hwnd = CaptureInterop.FindWindowByProcessName(hwndText);
                if (hwnd == IntPtr.Zero)
                {
                    Console.WriteLine($"找不到进程窗口：{hwndText}");
                    return;
                }

                Console.WriteLine($"已找到进程 {hwndText} 的窗口句柄：0x{hwnd.ToInt64():X}");
            }

            string outputFolder = args[1];

            int frameCount = args.Length >= 3 ? int.Parse(args[2]) : 200;
            int sampleStride = args.Length >= 4 ? int.Parse(args[3]) : 5; // 每 N 帧采一帧

            Directory.CreateDirectory(outputFolder);

            CaptureInterop.CreateD3DAndDuplicationForWindow(
                hwnd,
                out var d3dDevice,
                out var d3dContext,
                out var duplication,
                out var windowRect,
                out var outputDesc);

            // 拟合技能条 ROI（包含标题栏；如需裁掉标题栏可将 removeTitleBar 置 true）
            var roi = NormalizedRect.FitSkillbar(windowRect.Width, windowRect.Height, removeTitleBar: false, titleBarPixels: 37.0);

            using (var cts = new CancellationTokenSource())
            using (var session = new SkillbarCaptureSession(
                d3dDevice,
                d3dContext,
                duplication,
                windowRect,
                outputDesc,
                roi,
                sampleStride,
                outputFolder))
            {
                Console.WriteLine("开始截帧...");
                Console.WriteLine("Window rect: L={0}, T={1}, W={2}, H={3}", windowRect.Left, windowRect.Top, windowRect.Width, windowRect.Height);
                Console.WriteLine("ROI: X={0:F3}, Y={1:F3}, W={2:F3}, H={3:F3}", roi.X, roi.Y, roi.Width, roi.Height);
                Console.WriteLine("FrameCount={0}, SampleStride={1}", frameCount, sampleStride);
                Console.WriteLine("按 Enter 可提前停止。");

                var captureTask = session.RunAsync(frameCount, cts.Token);

                var inputTask = Task.Run(() =>
                {
                    Console.ReadLine();
                    cts.Cancel();
                });

                await Task.WhenAny(captureTask, inputTask);

                if (captureTask.IsFaulted)
                    throw captureTask.Exception?.InnerException ?? captureTask.Exception;

                Console.WriteLine("截帧结束。");
            }
        }

        private static void RunAnalyze(string[] args)
        {
            if (args.Length < 2)
            {
                Console.WriteLine("用法（离线分析模式）：");
                Console.WriteLine("  SkillbarCapture.exe analyze <folder>");
                Console.WriteLine("  folder  : 帧所在目录，例如 Q:\\temp\\1");
                return;
            }

            string folder = args[1];

            if (!Directory.Exists(folder))
            {
                Console.WriteLine($"目录不存在：{folder}");
                return;
            }

            PhaseAnalysis.AnalyzeWithTemplate(folder);
        }

        private static void PrintUsage()
        {
            Console.WriteLine("用法（实时截帧）：");
            Console.WriteLine("  SkillbarCapture.exe <hwnd_hex|process_name> <output_folder> [frameCount] [sampleStride]");
            Console.WriteLine("示例：");
            Console.WriteLine("  SkillbarCapture.exe 0000000001230042 Q:\\Temp\\Skillbar 200 5");
            Console.WriteLine("  SkillbarCapture.exe Gw2-64.exe Q:\\Temp\\Skillbar 200 5");
            Console.WriteLine();
            Console.WriteLine("用法（离线分析）：");
            Console.WriteLine("  SkillbarCapture.exe analyze <folder>");
            Console.WriteLine("示例：");
            Console.WriteLine("  SkillbarCapture.exe analyze Q:\\temp\\1");
        }
    }
}
