using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Drawing;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SkillbarCapture
{
    internal static class CaptureInterop
    {
        // DXGI -> WinRT 设备
        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        // Win32 获取顶层窗口
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

        private const uint GA_ROOT = 2;
        private const uint GA_ROOTOWNER = 3;

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        public static void CreateD3DAndDuplicationForWindow(
            IntPtr hwnd,
            out ID3D11Device d3dDevice,
            out ID3D11DeviceContext d3dContext,
            out IDXGIOutputDuplication duplication,
            out Rectangle windowRect,
            out OutputDescription outputDesc)
        {
            var featureLevels = new[]
            {
                FeatureLevel.Level_11_1,
                FeatureLevel.Level_11_0,
                FeatureLevel.Level_10_1,
                FeatureLevel.Level_10_0
            };

            var result = D3D11.D3D11CreateDevice(
                IntPtr.Zero,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                featureLevels,
                out d3dDevice,
                out _,
                out d3dContext);

            if (result.Failure)
                throw new InvalidOperationException("D3D11CreateDevice failed: " + result.Code);

            // 找到窗口所在的输出（显示器）
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero)
                throw new InvalidOperationException("MonitorFromWindow failed.");

            using (IDXGIDevice dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>())
            using (IDXGIAdapter adapter = dxgiDevice.GetParent<IDXGIAdapter>())
            using (IDXGIFactory1 factory = adapter.GetParent<IDXGIFactory1>())
            {
                IDXGIOutput1 targetOutput = null;
                outputDesc = default;

                uint adapterIndex = 0;
                while (factory.EnumAdapters1(adapterIndex, out IDXGIAdapter1? enumAdapter).Success && enumAdapter != null)
                {
                    uint outputIndex = 0;
                    while (enumAdapter.EnumOutputs(outputIndex, out IDXGIOutput? output).Success && output != null)
                    {
                        var output1 = output.QueryInterfaceOrNull<IDXGIOutput1>();
                        if (output1 != null)
                        {
                            var desc = output1.Description;
                            if (desc.Monitor == monitor)
                            {
                                targetOutput = output1;
                                outputDesc = desc;
                                output.Dispose();
                                break;
                            }
                            output1.Dispose();
                        }

                        output.Dispose();
                        outputIndex++;
                    }

                    if (targetOutput != null)
                    {
                        enumAdapter.Dispose();
                        break;
                    }

                    enumAdapter.Dispose();
                    adapterIndex++;
                }

                if (targetOutput == null)
                    throw new InvalidOperationException("未找到对应显示器的 DXGI Output。");

                duplication = targetOutput.DuplicateOutput(d3dDevice);
                targetOutput.Dispose();
            }

            if (!GetWindowRect(hwnd, out RECT rect))
                throw new InvalidOperationException("GetWindowRect failed.");

            windowRect = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
        }

        public static IntPtr FindWindowByProcessName(string processName)
        {
            if (string.IsNullOrWhiteSpace(processName))
                return IntPtr.Zero;

            string target = Path.GetFileNameWithoutExtension(processName).Trim();
            IntPtr found = IntPtr.Zero;

            bool Callback(IntPtr hWnd, IntPtr lParam)
            {
                if (found != IntPtr.Zero)
                    return false;

                if (!IsWindowVisible(hWnd))
                    return true;

                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid == 0)
                    return true;

                try
                {
                    using var proc = Process.GetProcessById((int)pid);
                    string name = Path.GetFileNameWithoutExtension(proc.ProcessName);
                    if (string.Equals(name, target, StringComparison.OrdinalIgnoreCase))
                    {
                        found = hWnd;
                        return false;
                    }
                }
                catch
                {
                }

                return true;
            }

            EnumWindows(Callback, IntPtr.Zero);
            return found;
        }
    }
}
