using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using WinRT;

namespace SkillbarCapture
{
    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            ComWrappersSupport.InitializeComWrappers();
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
            if (args.Length < 2)
            {
                Console.WriteLine("用法：");
                Console.WriteLine("  SkillbarCapture.exe <hwnd_hex|process_name> <output_folder> [frameCount] [sampleStride]");
                Console.WriteLine();
                Console.WriteLine("示例：");
                Console.WriteLine("  SkillbarCapture.exe 0000000001230042 C:\\Temp\\Skillbar 200 5");
                Console.WriteLine("  SkillbarCapture.exe Gw2-64.exe C:\\Temp\\Skillbar 200 5");
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

            int frameCount = 200;
            if (args.Length >= 3)
                frameCount = int.Parse(args[2]);

            int sampleStride = 5; // 每5帧采一帧
            if (args.Length >= 4)
                sampleStride = int.Parse(args[3]);

            Directory.CreateDirectory(outputFolder);

            CaptureInterop.CreateD3DAndDuplicationForWindow(
                hwnd,
                out var d3dDevice,
                out var d3dContext,
                out var duplication,
                out var windowRect,
                out var outputDesc);

            var roi = NormalizedRect.DefaultSkillbar;

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
                Console.WriteLine("Start capture...");
                Console.WriteLine("Window rect: L={0}, T={1}, W={2}, H={3}", windowRect.Left, windowRect.Top, windowRect.Width, windowRect.Height);
                Console.WriteLine("ROI: X={0:F3}, Y={1:F3}, W={2:F3}, H={3:F3}", roi.X, roi.Y, roi.Width, roi.Height);
                Console.WriteLine("FrameCount={0}, SampleStride={1}", frameCount, sampleStride);
                Console.WriteLine("Press Enter to stop early.");

                var captureTask = session.RunAsync(frameCount, cts.Token);

                var inputTask = Task.Run(() =>
                {
                    Console.ReadLine();
                    cts.Cancel();
                });

                await Task.WhenAny(captureTask, inputTask);

                if (captureTask.IsFaulted)
                    throw captureTask.Exception.InnerException;

                Console.WriteLine("Capture finished.");
            }
        }
    }
}
