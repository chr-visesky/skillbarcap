using System;
using System.Globalization;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SkillbarCapture
{
    internal static class OnlineCaptureCommand
    {
        public static async Task RunAsync(string[] args)
        {
            int argBase = 0;

            if (args[0].Equals("castbar", StringComparison.OrdinalIgnoreCase))
            {
                argBase = 1;
            }

            if (args.Length - argBase < 1)
            {
                CliUsage.PrintUsage();
                return;
            }

            string hwndText = args[argBase];
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

            int parseIndex = argBase + 1;

            string outputFolder = Path.Combine(FindProjectRoot(), "castbar");
            int frameCount = 200;
            int sampleStride = 5; // 每N帧采一帧

            if (parseIndex < args.Length)
            {
                if (int.TryParse(args[parseIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFrameCount))
                {
                    frameCount = parsedFrameCount;
                    parseIndex++;
                }
                else
                {
                    outputFolder = args[parseIndex];
                    parseIndex++;

                    if (parseIndex < args.Length && int.TryParse(args[parseIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out parsedFrameCount))
                    {
                        frameCount = parsedFrameCount;
                        parseIndex++;
                    }
                }
            }

            if (parseIndex < args.Length && int.TryParse(args[parseIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedSampleStride))
            {
                sampleStride = parsedSampleStride;
                parseIndex++;
            }

            Directory.CreateDirectory(outputFolder);

            CaptureInterop.CreateD3DAndDuplicationForWindow(
                hwnd,
                out var d3dDevice,
                out var d3dContext,
                out var duplication,
                out var windowRect,
                out var outputDesc);

            ID3D11Device? d3dDeviceToDispose = d3dDevice;
            ID3D11DeviceContext? d3dContextToDispose = d3dContext;
            IDXGIOutputDuplication? duplicationToDispose = duplication;

            try
            {
                using var cts = new CancellationTokenSource();

                var inputTask = Task.Run(() =>
                {
                    Console.ReadLine();
                    cts.Cancel();
                });

                Console.WriteLine("等待施法条显示以定位（按 Enter 取消）...");

                Rectangle roiPixels = default;
                int titlebarOffset = 0;

                while (!cts.IsCancellationRequested)
                {
                    var windowImage = CaptureWindowOnce(d3dDevice, d3dContext, duplication, windowRect, outputDesc);
                    if (SkillbarLocate.TryDetectCastbar(windowImage, out var castbarPixelRect, out titlebarOffset))
                    {
                        roiPixels = castbarPixelRect;
                        break;
                    }
                }

                if (cts.IsCancellationRequested)
                {
                    Console.WriteLine("已取消。");
                    return;
                }

                string filePrefix = "castbar";

                double roiX = roiPixels.Left / (double)windowRect.Width;
                double roiY = roiPixels.Top / (double)windowRect.Height;
                double roiW = roiPixels.Width / (double)windowRect.Width;
                double roiH = roiPixels.Height / (double)windowRect.Height;

                Console.WriteLine(
                    "Castbar titlebarOffset={0}px, pixelRect L={1},T={2},W={3},H={4}",
                    titlebarOffset,
                    roiPixels.Left,
                    roiPixels.Top,
                    roiPixels.Width,
                    roiPixels.Height);

                using (var session = new SkillbarCaptureSession(
                    d3dDevice,
                    d3dContext,
                    duplication,
                    windowRect,
                    outputDesc,
                    roiPixels,
                    sampleStride,
                    outputFolder,
                    filePrefix))
                {
                    d3dDeviceToDispose = null;
                    d3dContextToDispose = null;
                    duplicationToDispose = null;

                    Console.WriteLine("开始截帧..");
                    Console.WriteLine("Window rect: L={0}, T={1}, W={2}, H={3}", windowRect.Left, windowRect.Top, windowRect.Width, windowRect.Height);
                    Console.WriteLine("ROI: X={0:F3}, Y={1:F3}, W={2:F3}, H={3:F3}", roiX, roiY, roiW, roiH);
                    Console.WriteLine("ROI(px): L={0}, T={1}, W={2}, H={3}", roiPixels.Left, roiPixels.Top, roiPixels.Width, roiPixels.Height);
                    Console.WriteLine("FrameCount={0}, SampleStride={1}", frameCount, sampleStride);
                    Console.WriteLine("按 Enter 可提前停止。");

                    var captureTask = session.RunAsync(frameCount, cts.Token);

                    await Task.WhenAny(captureTask, inputTask);

                    if (captureTask.IsFaulted)
                        throw captureTask.Exception?.InnerException ?? captureTask.Exception;

                    Console.WriteLine("截帧结束。");
                }
            }
            finally
            {
                duplicationToDispose?.Dispose();
                d3dContextToDispose?.Dispose();
                d3dDeviceToDispose?.Dispose();
            }
        }

        private static string FindProjectRoot()
        {
            const string markerFile = "skillbarcap.sln";

            string? root = TryFindUpwards(Environment.CurrentDirectory, markerFile);
            root ??= TryFindUpwards(AppContext.BaseDirectory, markerFile);
            root ??= Environment.CurrentDirectory;

            return root;
        }

        private static string? TryFindUpwards(string startDirectory, string markerFile)
        {
            if (string.IsNullOrWhiteSpace(startDirectory) || string.IsNullOrWhiteSpace(markerFile))
                return null;

            try
            {
                string? dir = startDirectory;
                while (!string.IsNullOrEmpty(dir))
                {
                    string markerPath = Path.Combine(dir, markerFile);
                    if (File.Exists(markerPath))
                        return dir;

                    dir = Directory.GetParent(dir)?.FullName;
                }
            }
            catch
            {
                return null;
            }

            return null;
        }

        private static ImageBuffer CaptureWindowOnce(
            ID3D11Device device,
            ID3D11DeviceContext context,
            IDXGIOutputDuplication duplication,
            Rectangle windowRect,
            OutputDescription outputDesc)
        {
            var desktop = outputDesc.DesktopCoordinates;
            int outputLeft = desktop.Left;
            int outputTop = desktop.Top;
            int outputWidth = desktop.Right - desktop.Left;
            int outputHeight = desktop.Bottom - desktop.Top;

            int windowLeft = windowRect.Left - outputLeft;
            int windowTop = windowRect.Top - outputTop;
            int windowWidth = windowRect.Width;
            int windowHeight = windowRect.Height;

            if (windowLeft < 0 || windowTop < 0 || windowLeft + windowWidth > outputWidth || windowTop + windowHeight > outputHeight)
                throw new InvalidOperationException("Window rect is outside of output bounds; cannot capture full window.");

            var desc = new Texture2DDescription
            {
                Width = (uint)windowWidth,
                Height = (uint)windowHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };

            using var windowTexture = device.CreateTexture2D(desc);

            while (true)
            {
                duplication.AcquireNextFrame(500, out var frameInfo, out var resource);

                try
                {
                    using var texture = resource.QueryInterface<ID3D11Texture2D>();

                    // Copy window rect from desktop texture to windowTexture.
                    context.CopySubresourceRegion(
                        windowTexture, 0,
                        0, 0, 0,
                        texture, 0,
                        new Box(windowLeft, windowTop, 0, windowLeft + windowWidth, windowTop + windowHeight, 1));

                    var mapped = context.Map(windowTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

                    try
                    {
                        int bytesPerPixel = 4;
                        int rowPitch = (int)mapped.RowPitch;
                        byte[] buffer = new byte[windowWidth * windowHeight * bytesPerPixel];

                        IntPtr srcPtr = mapped.DataPointer;
                        for (int y = 0; y < windowHeight; y++)
                        {
                            IntPtr srcRow = srcPtr + y * rowPitch;
                            int dstOffset = y * windowWidth * bytesPerPixel;
                            Marshal.Copy(srcRow, buffer, dstOffset, windowWidth * bytesPerPixel);
                        }

                        return new ImageBuffer(windowWidth, windowHeight, buffer);
                    }
                    finally
                    {
                        context.Unmap(windowTexture, 0);
                    }
                }
                finally
                {
                    duplication.ReleaseFrame();
                    resource.Dispose();
                }
            }
        }
    }
}
