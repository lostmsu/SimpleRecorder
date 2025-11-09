// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;

using Microsoft.Extensions.Logging;

using Windows.Foundation;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Media;
using Windows.Media.Audio;
using Windows.Media.Capture;
using Windows.Media.Core;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage.Streams;

using WinRT;

namespace CaptureEncoder
{
    public sealed class Encoder : IDisposable
    {
        static int nextID;
        readonly ILogger<Encoder> log;
        public string Name { get; }
        /// <param name="sourceSize">Workaround for https://github.com/MicrosoftDocs/SimpleRecorder/issues/6</param>
        public Encoder(IDirect3DDevice device, GraphicsCaptureItem item, SizeInt32 sourceSize, string name,
                       ILogger<Encoder> log)
        {
            _device = device;
            _captureItem = item;
            _sourceSize = sourceSize;
            _isRecording = false;
            Name = name;
            this.log = log ?? throw new ArgumentNullException(nameof(log));

            CreateMediaObjects();
        }

        public event Action<object>? Stopped;
        public event Action<TimeSpan>? WaitTimeGrew;

        public Encoder(IDirect3DDevice device, GraphicsCaptureItem item, SizeInt32 sourceSize, ILogger<Encoder> log)
            : this(device, item, sourceSize, $"Encoder {Interlocked.Increment(ref nextID)}", log)
        {
        }

        private async Task CreateAudioObjects()
        {
            AudioGraphSettings settings = new AudioGraphSettings(Windows.Media.Render.AudioRenderCategory.Media);
            settings.QuantumSizeSelectionMode = QuantumSizeSelectionMode.LowestLatency;
            // create AudioGraph
            var result = await AudioGraph.CreateAsync(settings);
            if (result.Status != AudioGraphCreationStatus.Success)
            {
                log.LogError("AudioGraph creation error: {Status}", result.Status);
                return;
            }
            _audioGraph = result.Graph;

            // create device input _ a microphone
            var deviceInputResult = await _audioGraph.CreateDeviceInputNodeAsync(MediaCategory.Other);
            if (deviceInputResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                log.LogWarning("Audio Device Input unavailable: {Status}", deviceInputResult.Status);
                _audioGraph.Dispose();
                _audioGraph = null;
                return;
            }
            _deviceInputNode = deviceInputResult.DeviceInputNode;

            // create output frame 
            _frameOutputNode = _audioGraph.CreateFrameOutputNode();
            // increase volume of input
            // _deviceInputNode.OutgoingGain = 10;
            _deviceInputNode.AddOutgoingConnection(_frameOutputNode);
        }
     

        public Task EncodeAsync(IRandomAccessStream destination, uint width, uint height, MediaEncodingProfile profile,
                                CancellationToken stop)
        {
            return EncodeInternalAsync(destination, width, height, profile, stop: stop);
        }

        public Task<SystemRelativeTime> Start => this.startReadinessTask.Task;

        private async Task EncodeInternalAsync(IRandomAccessStream destination, uint width, uint height, MediaEncodingProfile encodingProfile,
                                               CancellationToken stop)
        {
            if (_isRecording)
                throw new InvalidOperationException();

            _isRecording = true;

            _frameGenerator = new CaptureFrameWait(
                _device,
                _captureItem,
                _sourceSize,
                log,
                stop: stop);

            _frameGenerator.OnWaitTimeGrowing += OnWaitTimeGrowing;

            using (_frameGenerator)
            {
                encodingProfile.Video.Width = width;
                encodingProfile.Video.Height = height;

                if (encodingProfile.Audio is not null) {
                    // create audio graph
                    if (_audioGraph == null) {
                        await CreateAudioObjects();
                    }

                    // add audio support
                    if (_audioGraph is not null)
                    {
                        _audioDescriptor = new AudioStreamDescriptor(_audioGraph.EncodingProperties);
                        _mediaStreamSource.AddStreamDescriptor(_audioDescriptor);
                    }
                }

                try
                {
                    var transcode = await _transcoder.PrepareMediaStreamSourceTranscodeAsync(_mediaStreamSource, destination, encodingProfile);
                    await transcode.TranscodeAsync();
                }
                catch (COMException e)
                {
                    if (e.Rethrow(transcoder: _transcoder, encodingProfile: encodingProfile) is null)
                        throw;
                }
                finally
                {
                    _frameGenerator.OnWaitTimeGrowing -= OnWaitTimeGrowing;
                    _deviceInputNode?.Dispose();
                    _frameOutputNode?.Dispose();
                    _audioGraph?.Dispose();
                    _audioGraph = null;
                    _isRecording = false;
                }
            }
        }


        public void Dispose()
        {
            if (_closed)
            {
                return;
            }
            _closed = true;

            if (!_isRecording)
            {
                DisposeInternal();
            }

            _isRecording = false;
            WaitTimeGrew = null;
        }

        public bool IsClosed => _closed;

        private  void DisposeInternal()
        {
            _frameGenerator.Dispose();
            lock (sharedBufferSync)
            {
                sharedBuffer = null;
                sharedBufferSize = 0;
            }
        }

        private void CreateMediaObjects()
        {
            // Describe our input: uncompressed BGRA8 buffers
            var videoProperties = VideoEncodingProperties.CreateUncompressed(MediaEncodingSubtypes.Bgra8, (uint)_sourceSize.Width, (uint)_sourceSize.Height);
            _videoDescriptor = new VideoStreamDescriptor(videoProperties);

            // Create our MediaStreamSource
            var videoSource = new MediaStreamSource(_videoDescriptor);
            _mediaStreamSource = videoSource;
            _mediaStreamSource.CanSeek = true;
            _mediaStreamSource.Paused += (sender, e) =>
            {
                log.LogDebug("Paused {Source}: {Status}", sender, e);
            };
            _mediaStreamSource.SwitchStreamsRequested += (sender, e) =>
            {
                log.LogInformation("SwitchStreamsRequested {Source}: {Status}", sender, e);
            };
            _mediaStreamSource.BufferTime = TimeSpan.FromMilliseconds(0);
            _mediaStreamSource.Starting += OnMediaStreamSourceStarting;
            _mediaStreamSource.SampleRequested += OnMediaStreamSourceSampleRequested;
            _mediaStreamSource.Closed += OnVideoClosed;

            // Create our transcoder
            _transcoder = new MediaTranscoder();
            _transcoder.HardwareAccelerationEnabled = true;

            void OnVideoClosed(MediaStreamSource sender, MediaStreamSourceClosedEventArgs args) {
                videoSource.Closed -= OnVideoClosed;
                log.LogInformation("{Name}: MediaStreamSource closed: {Reason}", Name, args?.Request?.Reason);
                _audioGraph?.Stop();
                Stopped?.Invoke(args!);
            }
        }

        IBuffer? sharedBuffer;
        int sharedBufferSize;
        readonly object sharedBufferSync = new();
        unsafe private void OnMediaStreamSourceSampleRequested(MediaStreamSource sender, MediaStreamSourceSampleRequestedEventArgs args)
        {
            if (_isRecording && !_closed)
            {
                try
                {

                    if (args.Request.StreamDescriptor.GetType() == typeof(VideoStreamDescriptor)) {
                        // Request Video
                        using var frame = this._frameGenerator.WaitForNewFrame();
                        
                        if (frame == null)
                        {
                            log.LogDebug("null video frame");
                            args.Request.Sample = null;
                            this.DisposeInternal();
                            return;
                        }
                        
                        var timeStamp = frame.SystemRelativeTime - this.timeOffset;
                        using var surface = frame.Surface;
                        var sample = MediaStreamSample.CreateFromDirect3D11Surface(frame.Surface, timeStamp);
                        args.Request.Sample = sample;
                    }
                    else if (args.Request.StreamDescriptor.GetType() == typeof(AudioStreamDescriptor))
                    {
                        var request = args.Request;

                        using var frame = GetNonEmptyFrame();
                        if (frame is null)
                        {
                            log.LogDebug("null audio frame");
                            args.Request.Sample = null;
                            if (_audioGraph is not null)
                                DisposeInternal();
                            return;
                        }

                        var stamp = frame.RelativeTime.GetValueOrDefault();
                        var duration = frame.Duration.GetValueOrDefault();

                        using (AudioBuffer buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
                        using (IMemoryBufferReference reference = buffer.CreateReference())
                        {
                            byte* dataInBytes;
                            uint capacityInBytes;
                            // Get the buffer from the AudioFrame
                            var byteAccess = reference.As<IMemoryBufferByteAccess>();
                            byteAccess.GetBuffer(out dataInBytes, out capacityInBytes);

                            lock (sharedBufferSync)
                            {
                                if (sharedBufferSize < capacityInBytes)
                                {
                                    sharedBufferSize = checked((int)capacityInBytes);
                                    sharedBuffer = WindowsRuntimeBuffer.Create(capacity: sharedBufferSize);
                                }
                                var span = new ReadOnlySpan<byte>(dataInBytes, (int)capacityInBytes);
                                using (var writer = sharedBuffer.AsStream())
                                    writer.Write(span);
                                sharedBuffer!.Length = capacityInBytes;

                                var sample = MediaStreamSample.CreateFromBuffer(sharedBuffer, stamp);
                                sample.Duration = duration;
                                sample.KeyFrame = true;

                                request.Sample = sample;
                            }
                        }
                    }
                }
                catch (Exception e)
                {
                    log.LogError("{Name}: Error getting sample: {Message}", Name, e.Message);
                    log.LogDebug(e, "{Name}: Error getting sample: {Message}", Name, e.Message);
                    args.Request.Sample = null;
                    DisposeInternal();
                }
            }
            else
            {
                log.LogDebug("Returning null for frame request: Not recording: rec: {IsRecording} closed: {IsClosed}",
                    _isRecording, _closed);
                args.Request.Sample = null;
                DisposeInternal();
            }
        }

        AudioFrame? GetNonEmptyFrame(int maxTries = 48000) {
            if (_frameOutputNode is null)
            {
                log.LogWarning("{Func}: No audio frame output node", nameof(GetNonEmptyFrame));
                return null;
            }
            for (int @try = 0; @try < maxTries; @try++) {
                var frame = _frameOutputNode.GetFrame();
                if (frame.Duration.GetValueOrDefault().Ticks != 0) {
                    return frame;
                }
                frame.Dispose();
            }
            log.LogWarning("unable to get a non-empty audio frame after {Tries} tries", maxTries);
            return null;
        }

        
        private void OnMediaStreamSourceStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            log.LogDebug("MediaStreamSourceStarting");
            try
            {
                MediaStreamSourceStartingRequest request = args.Request;

                using (var frame = _frameGenerator.WaitForNewFrame())
                {
                    timeOffset = frame.SystemRelativeTime;
                    //request.SetActualStartPosition(frame.SystemRelativeTime);
                }
                log.LogDebug("Got first video frame");

                _audioGraph?.Start();
                if (_audioGraph is not null && _frameOutputNode is not null)
                {
                    using var audioFrame = _frameOutputNode.GetFrame();
                    timeOffset = timeOffset + audioFrame.RelativeTime.GetValueOrDefault();
                    log.LogDebug("Got first audio frame");
                }

                this.startReadinessTask.SetResult(new() { Value = timeOffset });
            }
            catch (Exception e)
            {
                log.LogError(e, "Error during MediaStreamSourceStarting");
                this.startReadinessTask.SetException(e);
            }
        }

        void OnWaitTimeGrowing(TimeSpan waitTime)
        {
            WaitTimeGrew?.Invoke(waitTime);
        }

        readonly IDirect3DDevice _device;

        private GraphicsCaptureItem _captureItem;
        readonly SizeInt32 _sourceSize;
        private CaptureFrameWait _frameGenerator;

        private VideoStreamDescriptor _videoDescriptor;
        private AudioStreamDescriptor _audioDescriptor;
        private MediaStreamSource _mediaStreamSource;
        private MediaTranscoder _transcoder;
        private bool _isRecording;
        private bool _closed = false;

        // audio graph and nodes
        private AudioGraph? _audioGraph;
        private AudioDeviceInputNode? _deviceInputNode;
        private AudioFrameOutputNode? _frameOutputNode;
        private TimeSpan timeOffset = new TimeSpan();
        TaskCompletionSource<SystemRelativeTime> startReadinessTask = new();

    }
}
