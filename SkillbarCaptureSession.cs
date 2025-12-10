using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SkillbarCapture
{
    internal sealed class SkillbarCaptureSession : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly IDirect3DDevice _winrtDevice;
        private readonly GraphicsCaptureItem _item;
        private readonly NormalizedRect _roi;
        private readonly int _sampleStride;
        private readonly string _outputFolder;

        private Direct3D11CaptureFramePool _framePool;
        private GraphicsCaptureSession _session;
        private SizeInt32 _lastSize;

        private ID3D11Texture2D _roiTexture;
        private int _roiWidth;
        private int _roiHeight;

        private int _frameIndex;
        private int _targetFrameCount;
        private int _frameCounter;
        private bool _running;
        private TaskCompletionSource<bool> _tcs;
        private readonly object _lock = new object();

        [ComImport]
        [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            IntPtr GetInterface([In] ref Guid iid);
        }

        public SkillbarCaptureSession(
            ID3D11Device device,
            ID3D11DeviceContext context,
            IDirect3DDevice winrtDevice,
            GraphicsCaptureItem item,
            NormalizedRect roi,
            int sampleStride,
            string outputFolder)
        {
            if (sampleStride <= 0) throw new ArgumentOutOfRangeException(nameof(sampleStride));

            _device = device ?? throw new ArgumentNullException(nameof(device));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _winrtDevice = winrtDevice ?? throw new ArgumentNullException(nameof(winrtDevice));
            _item = item ?? throw new ArgumentNullException(nameof(item));
            _roi = roi;
            _sampleStride = sampleStride;
            _outputFolder = outputFolder ?? throw new ArgumentNullException(nameof(outputFolder));
        }

        public Task RunAsync(int frameCount, CancellationToken cancellationToken)
        {
            if (frameCount <= 0) throw new ArgumentOutOfRangeException(nameof(frameCount));

            _targetFrameCount = frameCount;
            _frameIndex = 0;
            _frameCounter = 0;
            _running = true;
            _tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            _lastSize = _item.Size;

            _framePool = Direct3D11CaptureFramePool.Create(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                _lastSize);

            _framePool.FrameArrived += FramePool_FrameArrived;

            _session = _framePool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;
            _session.GetType().GetProperty("IsBorderRequired")?.SetValue(_session, false);

            cancellationToken.Register(Stop);

            _session.StartCapture();

            return _tcs.Task;
        }

        private async void FramePool_FrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            if (!_running)
                return;

            Direct3D11CaptureFrame frame = null;

            try
            {
                frame = sender.TryGetNextFrame();
                if (frame == null)
                    return;

                // 甯ц鏁帮紝鐢ㄤ簬姣?N 甯ч噰鏍蜂竴娆?
                int counter = Interlocked.Increment(ref _frameCounter);
                if (counter % _sampleStride != 0)
                {
                    frame.Dispose();
                    return;
                }

                var contentSize = frame.ContentSize;
                if (contentSize.Width != _lastSize.Width ||
                    contentSize.Height != _lastSize.Height)
                {
                    _lastSize = contentSize;
                    sender.Recreate(
                        _winrtDevice,
                        DirectXPixelFormat.B8G8R8A8UIntNormalized,
                        2,
                        _lastSize);

                    DisposeRoiTexture();
                    frame.Dispose();
                    return;
                }

                using (frame)
                {
                    // 浠?WinRT Surface 鎷垮埌搴曞眰 ID3D11Texture2D
                    var access = (IDirect3DDxgiInterfaceAccess)frame.Surface;
                    Guid texGuid = new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c"); // IID_ID3D11Texture2D
                    IntPtr texPtr = access.GetInterface(ref texGuid);

                    using (var fullTexture = new ID3D11Texture2D(texPtr))
                    {
                        int fullWidth = _lastSize.Width;
                        int fullHeight = _lastSize.Height;

                        int roiLeft = (int)Math.Round(_roi.X * fullWidth);
                        int roiTop = (int)Math.Round(_roi.Y * fullHeight);
                        int roiWidth = (int)Math.Round(_roi.Width * fullWidth);
                        int roiHeight = (int)Math.Round(_roi.Height * fullHeight);

                        if (roiLeft < 0) roiLeft = 0;
                        if (roiTop < 0) roiTop = 0;
                        if (roiLeft + roiWidth > fullWidth) roiWidth = fullWidth - roiLeft;
                        if (roiTop + roiHeight > fullHeight) roiHeight = fullHeight - roiTop;

                        if (roiWidth <= 0 || roiHeight <= 0)
                            return;

                        EnsureRoiTexture(roiWidth, roiHeight);

                        // 鍦?GPU 鍐呰鍓?ROI 鍒?staging 绾圭悊
                        var box = new Box(
                            roiLeft,
                            roiTop,
                            0,
                            roiLeft + roiWidth,
                            roiTop + roiHeight,
                            1);

                        _context.CopySubresourceRegion(
                            _roiTexture, 0,
                            0, 0, 0,
                            fullTexture, 0,
                            box);

                        // 浠?staging 绾圭悊璇诲洖 CPU
                        var mapped = _context.Map(
                            _roiTexture,
                            0,
                            MapMode.Read,
                            Vortice.Direct3D11.MapFlags.None);

                        try
                        {
                            int bytesPerPixel = 4;
                            int rowPitch = (int)mapped.RowPitch;
                            byte[] buffer = new byte[roiWidth * roiHeight * bytesPerPixel];

                            IntPtr srcPtr = mapped.DataPointer;

                            for (int y = 0; y < roiHeight; y++)
                            {
                                IntPtr srcRow = srcPtr + y * rowPitch;
                                int dstOffset = y * roiWidth * bytesPerPixel;
                                Marshal.Copy(srcRow, buffer, dstOffset, roiWidth * bytesPerPixel);
                            }

                            var roiBuffer = new ImageBuffer(roiWidth, roiHeight, buffer);

                            int index;
                            bool finishNow = false;

                            lock (_lock)
                            {
                                if (!_running)
                                    return;

                                index = _frameIndex;
                                _frameIndex++;

                                if (_frameIndex >= _targetFrameCount)
                                {
                                    _running = false;
                                    finishNow = true;
                                }
                            }

                            string fileName = Path.Combine(
                                _outputFolder,
                                $"skillbar_{index:D4}.png");

                            ImageIO.SavePng(roiBuffer, fileName);

                            if (finishNow)
                            {
                                Complete();
                            }
                        }
                        finally
                        {
                            _context.Unmap(_roiTexture, 0);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                lock (_lock)
                {
                    if (_running)
                    {
                        _running = false;
                        _tcs.TrySetException(ex);
                    }
                }

                DisposeInternal();
            }
        }

        private void EnsureRoiTexture(int width, int height)
        {
            if (_roiTexture != null &&
                width == _roiWidth &&
                height == _roiHeight)
            {
                return;
            }

            DisposeRoiTexture();

            _roiWidth = width;
            _roiHeight = height;

            var desc = new Texture2DDescription
            {
                Width = (uint)width,
                Height = (uint)height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read,
                MiscFlags = ResourceOptionFlags.None
            };

            _roiTexture = _device.CreateTexture2D(desc);
        }

        private void DisposeRoiTexture()
        {
            if (_roiTexture != null)
            {
                _roiTexture.Dispose();
                _roiTexture = null;
                _roiWidth = 0;
                _roiHeight = 0;
            }
        }

        private void Complete()
        {
            lock (_lock)
            {
                if (_tcs != null && !_tcs.Task.IsCompleted)
                    _tcs.TrySetResult(true);
            }

            DisposeInternal();
        }

        public void Stop()
        {
            lock (_lock)
            {
                if (!_running)
                    return;

                _running = false;
            }

            Complete();
        }

        private void DisposeInternal()
        {
            DisposeRoiTexture();

            if (_session != null)
            {
                _session.Dispose();
                _session = null;
            }

            if (_framePool != null)
            {
                _framePool.FrameArrived -= FramePool_FrameArrived;
                _framePool.Dispose();
                _framePool = null;
            }
        }

        public void Dispose()
        {
            DisposeInternal();

            _context?.Dispose();
            _device?.Dispose();

            if (_winrtDevice is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }
}

