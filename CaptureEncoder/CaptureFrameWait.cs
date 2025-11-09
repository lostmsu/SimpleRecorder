// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

using SharpDX.Direct3D11;

using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace CaptureEncoder
{
    sealed class CaptureFrameWait : IDisposable
    {
        public CaptureFrameWait(
            IDirect3DDevice device,
            GraphicsCaptureItem item,
            SizeInt32 size,
            ILogger log,
            CancellationToken stop)
        {
            _device = device ?? throw new ArgumentNullException(nameof(device));
            _item = item ?? throw new ArgumentNullException(nameof(item));
            _d3dDevice = Direct3D11Helpers.CreateSharpDXDevice(device);
            try
            {
                _multithread = _d3dDevice.QueryInterface<Multithread>();
                _multithread.SetMultithreadProtected(true);
                _frameEvent = new ManualResetEvent(false);
                _closedEvent = new ManualResetEvent(false);
                _events = new[] { _closedEvent, _frameEvent };
                this.log = log ?? throw new ArgumentNullException(nameof(log));
                _stop = stop;

                _blankTexture = InitializeBlankTexture(size);
                InitializeCapture(size, out _framePool, out _session);
            } catch
            {
                _blankTexture?.Dispose();
                _closedEvent?.Dispose();
                _frameEvent?.Dispose();
                _multithread?.Dispose();
                _d3dDevice.Dispose();
                throw;
            }
        }

        public Action<TimeSpan>? OnWaitTimeGrowing;

        private void InitializeCapture(SizeInt32 size,
                                       out Direct3D11CaptureFramePool framePool,
                                       out GraphicsCaptureSession session)
        {
            _item.Closed += OnClosed;
            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                1,
                size);
            framePool.FrameArrived += OnFrameArrived;
            _synchronizationContext = SynchronizationContext.Current;
            try
            {
                session = framePool.CreateCaptureSession(_item);
                session.StartCapture();
            } catch
            {
                framePool.FrameArrived -= OnFrameArrived;
                framePool.Dispose();
                throw;
            }
        }

        private Texture2D InitializeBlankTexture(SizeInt32 size)
        {
            var description = new SharpDX.Direct3D11.Texture2DDescription
            {
                Width = size.Width,
                Height = size.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = SharpDX.DXGI.Format.B8G8R8A8_UNorm,
                SampleDescription = new SharpDX.DXGI.SampleDescription()
                {
                    Count = 1,
                    Quality = 0
                },
                Usage = SharpDX.Direct3D11.ResourceUsage.Default,
                BindFlags = SharpDX.Direct3D11.BindFlags.ShaderResource | SharpDX.Direct3D11.BindFlags.RenderTarget,
                CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags.None,
                OptionFlags = SharpDX.Direct3D11.ResourceOptionFlags.None
            };
            var texture = new Texture2D(_d3dDevice, description);

            using (var renderTargetView = new SharpDX.Direct3D11.RenderTargetView(_d3dDevice, texture)) {
                _d3dDevice.ImmediateContext.ClearRenderTargetView(renderTargetView, new SharpDX.Mathematics.Interop.RawColor4(0, 0, 0, 1));
            }
            return texture;
        }

        private void Stop()
        {
            _framePool.FrameArrived -= OnFrameArrived;
            _closedEvent.Set();
        }

        private void OnClosed(GraphicsCaptureItem sender, object args)
        {
            log.LogDebug("Capture item closed");
            Stop();
        }

        private void Cleanup()
        {
            OnWaitTimeGrowing = null;
            _item?.Closed -= OnClosed;
            lock (_framePool)
            {
                _framePool.Dispose();
                _framePoolDisposed = true;
            }
            if (_synchronizationContext is not null) {
                _synchronizationContext.Send(_ => _session.Dispose(), null);
            } else {
                _session.Dispose();
            }
            _device = null!;
            _d3dDevice = null!;
            _blankTexture?.Dispose();
            _blankTexture = null!;
            _copyTexture?.Dispose();
            _copyTexture = null;
            _currentFrame?.Dispose();
        }

        Texture2D? _copyTexture;
        Texture2DDescription _copyDescription;
        private void OnFrameArrived(Direct3D11CaptureFramePool sender, object args)
        {
            lock (_framePool)
            {
                if (_framePoolDisposed)
                    return;

                _currentFrame?.Dispose();
                _currentFrame = sender.TryGetNextFrame();
            }
            _frameEvent.Set();
        }

        public SurfaceWithInfo? WaitForNewFrame()
        {
            // Let's get a fresh one.
            lock (_framePool)
            {
                _currentFrame?.Dispose();
                _currentFrame = null;

                if (_framePoolDisposed)
                    return null;
            }
            _frameEvent.Reset();

            int secondsSpent = 0;
        check_stop:
            if (_stop.IsCancellationRequested)
            {
                Stop();
                Cleanup();
                return null;
            }

            int ready = WaitHandle.WaitAny(_events, TimeSpan.FromSeconds(1));
            if (ready == WaitHandle.WaitTimeout)
            {
                secondsSpent++;
                if (secondsSpent % 10 == 0)
                    log.LogWarning("No frames for {Seconds} seconds", secondsSpent);
                OnWaitTimeGrowing?.Invoke(TimeSpan.FromSeconds(secondsSpent));
                goto check_stop;
            }
            if (secondsSpent >= 10)
            {
                log.LogInformation("Frame arrived after delay");
            }
            var signaledEvent = _events[ready];
            if (signaledEvent == _closedEvent)
            {
                Cleanup();
                return null;
            }

            Direct3D11CaptureFrame? frame;
            lock (_framePool)
            {
                frame = _currentFrame;
                if (frame is null)
                    goto check_stop;
                _currentFrame = null;
            }

            using var __ = frame;
            var result = new SurfaceWithInfo
            {
                SystemRelativeTime = frame.SystemRelativeTime
            };

            using var _ = new MultithreadLock(_multithread);
            using var surface = frame.Surface;
            using var sourceTexture = Direct3D11Helpers.CreateSharpDXTexture2D(surface);

            var description = sourceTexture.Description;
            description.Usage = SharpDX.Direct3D11.ResourceUsage.Default;
            description.BindFlags = SharpDX.Direct3D11.BindFlags.ShaderResource | SharpDX.Direct3D11.BindFlags.RenderTarget;
            description.CpuAccessFlags = SharpDX.Direct3D11.CpuAccessFlags.None;
            description.OptionFlags = SharpDX.Direct3D11.ResourceOptionFlags.None;

            if (_copyDescription.Width != description.Width
                || _copyDescription.Height != description.Height
                || _copyDescription.Format != description.Format)
            {
                _copyTexture?.Dispose();
                _copyTexture = new Texture2D(_d3dDevice, description);
            }

            var width = Math.Clamp(frame.ContentSize.Width, 0, surface.Description.Width);
            var height = Math.Clamp(frame.ContentSize.Height, 0, surface.Description.Height);

            var region = new SharpDX.Direct3D11.ResourceRegion(0, 0, 0, width, height, 1);

            _d3dDevice.ImmediateContext.CopyResource(_blankTexture, _copyTexture);
            _d3dDevice.ImmediateContext.CopySubresourceRegion(sourceTexture, 0, region, _copyTexture, 0);
            return new()
            {
                Surface = Direct3D11Helpers.CreateDirect3DSurfaceFromSharpDXTexture(_copyTexture!),
                SystemRelativeTime = frame.SystemRelativeTime,
            };
        }

        public void Dispose()
        {
            Stop();
            Cleanup();
        }

        readonly CancellationToken _stop;
        private IDirect3DDevice _device;
        private SharpDX.Direct3D11.Device _d3dDevice;
        readonly Multithread _multithread;
        private SharpDX.Direct3D11.Texture2D _blankTexture;

        private ManualResetEvent[] _events;
        private ManualResetEvent _frameEvent;
        private ManualResetEvent _closedEvent;
        private Direct3D11CaptureFrame? _currentFrame;

        readonly GraphicsCaptureItem _item;
        readonly GraphicsCaptureSession _session;
        private SynchronizationContext? _synchronizationContext;
        readonly Direct3D11CaptureFramePool _framePool;
        bool _framePoolDisposed;
        readonly ILogger log;

        public sealed class SurfaceWithInfo : IDisposable
        {
            public IDirect3DSurface Surface { get; internal set; }
            public TimeSpan SystemRelativeTime { get; internal set; }

            public void Dispose()
            {
                this.Surface?.Dispose();
                this.Surface = null!;
            }
        }

        class MultithreadLock : IDisposable
        {
            public MultithreadLock(Multithread multithread)
            {
                _multithread = multithread ?? throw new ArgumentNullException(nameof(multithread));
                _multithread.Enter();
            }

            public void Dispose()
            {
                lock (_multithread)
                {
                    if (_disposed)
                        return;

                    _disposed = true;
                    _multithread.Leave();
                }
            }

            bool _disposed;
            readonly Multithread _multithread;
        }
    }
}
