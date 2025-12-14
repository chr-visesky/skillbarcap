using System;
using System.Globalization;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SkillbarCapture
{
    internal static class FullCaptureCommand
    {
        public static void Run(string[] args)
        {
            if (args.Length < 2)
            {
                CliUsage.PrintUsage();
                return;
            }

            string hwndText = args[1];
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

            string outputFolder = args.Length >= 3 ? args[2] : Path.Combine(FindProjectRoot(), "online_test");
            Directory.CreateDirectory(outputFolder);
            string outPath = Path.Combine(outputFolder, "fullshot.png");

            CaptureInterop.CreateD3DAndDuplicationForWindow(
                hwnd,
                out var d3dDevice,
                out var d3dContext,
                out var duplication,
                out var windowRect,
                out var outputDesc);

            try
            {
                var image = CaptureWindowOnce(d3dDevice, d3dContext, duplication, windowRect, outputDesc);
                using var bmp = new Bitmap(image.Width, image.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
                    System.Drawing.Imaging.ImageLockMode.WriteOnly,
                    System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                try
                {
                    Marshal.Copy(image.Data, 0, data.Scan0, image.Data.Length);
                }
                finally
                {
                    bmp.UnlockBits(data);
                }

                bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
                Console.WriteLine($"已保存截屏：{outPath} (w={bmp.Width}, h={bmp.Height})");
            }
            catch (SharpGen.Runtime.SharpGenException ex) when ((uint)ex.HResult == 0x80070005)
            {
                Console.WriteLine("Duplication 被拒绝访问，使用 GDI 兜底截屏...");
                FallbackGdiCapture(hwnd, outPath);
            }
            finally
            {
                duplication?.Dispose();
                d3dContext?.Dispose();
                d3dDevice?.Dispose();
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

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        private static void FallbackGdiCapture(IntPtr hwnd, string outPath)
        {
            if (!GetWindowRect(hwnd, out var rect))
            {
                Console.WriteLine("获取窗口矩形失败，无法兜底截图。");
                return;
            }

            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;
            using var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
            using var g = Graphics.FromImage(bmp);
            g.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(w, h));
            bmp.Save(outPath, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine($"已保存兜底截屏：{outPath} (w={w}, h={h})");
        }
    }
}
