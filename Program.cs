using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Capture;

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
            if (!GraphicsCaptureSession.IsSupported())
            {
                Console.WriteLine("Windows.Graphics.Capture is not supported on this system.");
                return;
            }

            if (args.Length < 2)
            {
                Console.WriteLine("用法：");
                Console.WriteLine("  SkillbarCapture.exe <hwnd_hex> <output_folder> [frameCount] [sampleStride]");
                Console.WriteLine();
                Console.WriteLine("示例：");
                Console.WriteLine("  SkillbarCapture.exe 0000000001230042 C:\\Temp\\Skillbar 200 5");
                return;
            }

            string hwndText = args[0];
            IntPtr hwnd = (IntPtr)Convert.ToInt64(hwndText, 16);
            string outputFolder = args[1];

            int frameCount = 200;
            if (args.Length >= 3)
                frameCount = int.Parse(args[2]);

            int sampleStride = 5; // 每 5 帧采一帧
            if (args.Length >= 4)
                sampleStride = int.Parse(args[3]);

            Directory.CreateDirectory(outputFolder);

            CaptureInterop.CreateD3DAndWinRTDevice(
                out var d3dDevice,
                out var d3dContext,
                out var winrtDevice);

            var item = CaptureInterop.CreateItemForWindow(hwnd);

            var roi = NormalizedRect.DefaultSkillbar;

            using (var cts = new CancellationTokenSource())
            using (var session = new SkillbarCaptureSession(
                d3dDevice,
                d3dContext,
                winrtDevice,
                item,
                roi,
                sampleStride,
                outputFolder))
            {
                Console.WriteLine("Start capture...");
                Console.WriteLine("Window size: {0}x{1}", item.Size.Width, item.Size.Height);
                Console.WriteLine("ROI: X={0:F3}, Y={1:F3}, W={2:F3}, H={3:F3}",
                    roi.X, roi.Y, roi.Width, roi.Height);
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
