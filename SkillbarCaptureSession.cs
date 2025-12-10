using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;

namespace SkillbarCapture
{
    internal sealed class SkillbarCaptureSession : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly IDXGIOutputDuplication _duplication;
        private readonly Rectangle _windowRect;
        private readonly OutputDescription _outputDesc;
        private readonly NormalizedRect _roi;
        private readonly int _sampleStride;
        private readonly string _outputFolder;

        private ID3D11Texture2D _roiTexture;
        private int _roiWidth;
        private int _roiHeight;

        private int _frameIndex;
        private int _targetFrameCount;
        private int _frameCounter;
        private bool _running;
        private TaskCompletionSource<bool> _tcs;
        private readonly object _lock = new object();

        public SkillbarCaptureSession(
            ID3D11Device device,
            ID3D11DeviceContext context,
            IDXGIOutputDuplication duplication,
            Rectangle windowRect,
            OutputDescription outputDesc,
            NormalizedRect roi,
            int sampleStride,
            string outputFolder)
        {
            if (sampleStride <= 0) throw new ArgumentOutOfRangeException(nameof(sampleStride));

            _device = device ?? throw new ArgumentNullException(nameof(device));
            _context = context ?? throw new ArgumentNullException(nameof(context));
            _duplication = duplication ?? throw new ArgumentNullException(nameof(duplication));
            _windowRect = windowRect;
            _outputDesc = outputDesc;
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

            cancellationToken.Register(Stop);

            Task.Run(CaptureLoop);

            return _tcs.Task;
        }

        private void CaptureLoop()
        {
            try
            {
                var desktop = _outputDesc.DesktopCoordinates;
                int outputLeft = desktop.Left;
                int outputTop = desktop.Top;
                int outputWidth = desktop.Right - desktop.Left;
                int outputHeight = desktop.Bottom - desktop.Top;

                int windowLeft = _windowRect.Left - outputLeft;
                int windowTop = _windowRect.Top - outputTop;
                int windowWidth = _windowRect.Width;
                int windowHeight = _windowRect.Height;

                while (_running)
                {
                    var result = _duplication.AcquireNextFrame(1000, out Vortice.DXGI.OutduplFrameInfo frameInfo, out IDXGIResource frameResource);

                    if (result == Vortice.DXGI.ResultCode.WaitTimeout)
                    {
                        continue;
                    }

                    if (result.Failure || frameResource == null)
                    {
                        throw new InvalidOperationException("AcquireNextFrame failed: " + result.Code);
                    }

                    using (frameResource)
                    using (var fullTexture = frameResource.QueryInterface<ID3D11Texture2D>())
                    {
                        int roiLeft = windowLeft + (int)Math.Round(_roi.X * windowWidth);
                        int roiTop = windowTop + (int)Math.Round(_roi.Y * windowHeight);
                        int roiWidth = (int)Math.Round(_roi.Width * windowWidth);
                        int roiHeight = (int)Math.Round(_roi.Height * windowHeight);

                        if (roiLeft < 0) roiLeft = 0;
                        if (roiTop < 0) roiTop = 0;
                        if (roiLeft + roiWidth > outputWidth) roiWidth = outputWidth - roiLeft;
                        if (roiTop + roiHeight > outputHeight) roiHeight = outputHeight - roiTop;

                        if (roiWidth <= 0 || roiHeight <= 0)
                        {
                            _duplication.ReleaseFrame();
                            continue;
                        }

                        EnsureRoiTexture(roiWidth, roiHeight);

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

                        var mapped = _context.Map(_roiTexture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);

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

                            string fileName = Path.Combine(_outputFolder, $"skillbar_{index:D4}.png");
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

                    _duplication.ReleaseFrame();

                    int counter = Interlocked.Increment(ref _frameCounter);
                    if (!_running || counter % _sampleStride != 0)
                    {
                        continue;
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

                Dispose();
            }
        }

        private void EnsureRoiTexture(int width, int height)
        {
            if (_roiTexture != null && width == _roiWidth && height == _roiHeight)
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

            Dispose();
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

        public void Dispose()
        {
            DisposeRoiTexture();
            _duplication?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
        }
    }
}
