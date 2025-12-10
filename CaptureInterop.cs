using System;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace SkillbarCapture
{
    internal static class CaptureInterop
    {
        // WinRT 工厂相关
        [DllImport("combase.dll", CharSet = CharSet.Unicode)]
        private static extern int WindowsCreateString(
            string source,
            int length,
            out IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [DllImport("combase.dll")]
        private static extern int RoGetActivationFactory(
            IntPtr hstring,
            ref Guid iid,
            out IntPtr factory);

        // DXGI -> WinRT 设备
        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice")]
        private static extern int CreateDirect3D11DeviceFromDXGIDevice(
            IntPtr dxgiDevice,
            out IntPtr graphicsDevice);

        // GraphicsCaptureItem 工厂接口
        [ComImport]
        [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            int CreateForWindow(
                IntPtr window,
                [In] ref Guid iid,
                [MarshalAs(UnmanagedType.IUnknown)] out object result);

            int CreateForMonitor(
                IntPtr monitor,
                [In] ref Guid iid,
                [MarshalAs(UnmanagedType.IUnknown)] out object result);
        }

        public static void CreateD3DAndWinRTDevice(
            out ID3D11Device d3dDevice,
            out ID3D11DeviceContext d3dContext,
            out IDirect3DDevice winrtDevice)
        {
            // 创建 D3D11 设备（Vortice 封装）
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

            // 拿到 DXGI 设备指针
            using (IDXGIDevice dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>())
            {
                IntPtr winrtDevicePtr;
                int hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out winrtDevicePtr);
                if (hr < 0)
                    throw new InvalidOperationException("CreateDirect3D11DeviceFromDXGIDevice failed: 0x" + hr.ToString("X8"));

                winrtDevice = (IDirect3DDevice)Marshal.GetObjectForIUnknown(winrtDevicePtr);
                Marshal.Release(winrtDevicePtr);
            }
        }

        public static GraphicsCaptureItem CreateItemForWindow(IntPtr hwnd)
        {
            string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
            IntPtr hstring;
            int hr = WindowsCreateString(className, className.Length, out hstring);
            if (hr < 0)
                throw new InvalidOperationException("WindowsCreateString failed: 0x" + hr.ToString("X8"));

            IntPtr factoryPtr;
            Guid interopId = typeof(IGraphicsCaptureItemInterop).GUID;
            hr = RoGetActivationFactory(hstring, ref interopId, out factoryPtr);
            WindowsDeleteString(hstring);

            if (hr < 0)
                throw new InvalidOperationException("RoGetActivationFactory failed: 0x" + hr.ToString("X8"));

            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);

            Guid itemId = typeof(GraphicsCaptureItem).GUID;
            object itemUnknown;
            hr = interop.CreateForWindow(hwnd, ref itemId, out itemUnknown);
            if (hr < 0)
                throw new InvalidOperationException("CreateForWindow failed: 0x" + hr.ToString("X8"));

            return (GraphicsCaptureItem)itemUnknown;
        }
    }
}
