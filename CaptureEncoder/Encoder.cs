// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
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
        public string Name { get; }
        /// <param name="sourceSize">Workaround for https://github.com/MicrosoftDocs/SimpleRecorder/issues/6</param>
        public Encoder(IDirect3DDevice device, GraphicsCaptureItem item, SizeInt32 sourceSize, string name)
        {
            _device = device;
            _captureItem = item;
            _sourceSize = sourceSize;
            _isRecording = false;
            Name = name;

            CreateMediaObjects();
        }

        public Encoder(IDirect3DDevice device, GraphicsCaptureItem item, SizeInt32 sourceSize)
            : this(device, item, sourceSize, $"Encoder {Interlocked.Increment(ref nextID)}")
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
                Debug.WriteLine("AudioGraph creation error: " + result.Status.ToString());
                return;
            }
            _audioGraph = result.Graph;

            // create device input _ a microphone
            var deviceInputResult = await _audioGraph.CreateDeviceInputNodeAsync(MediaCategory.Other);
            if (deviceInputResult.Status != AudioDeviceNodeCreationStatus.Success)
            {
                Debug.WriteLine($"Audio Device Input unavailable because {deviceInputResult.Status.ToString()}");

                return;
            }
            _deviceInputNode = deviceInputResult.DeviceInputNode;

            // create output frame 
            _frameOutputNode = _audioGraph.CreateFrameOutputNode();
            // increase volume of input
            // _deviceInputNode.OutgoingGain = 10;
            _deviceInputNode.AddOutgoingConnection(_frameOutputNode);

        }
     

        public IAsyncAction EncodeAsync(IRandomAccessStream destination, uint width, uint height, MediaEncodingProfile profile)
        {
            return EncodeInternalAsync(destination, width, height, profile).AsAsyncAction();
        }

        public Task<SystemRelativeTime> Start => this.startReadinessTask.Task;

        private async Task EncodeInternalAsync(IRandomAccessStream destination, uint width, uint height, MediaEncodingProfile encodingProfile)
        {
            if (_isRecording)
                throw new InvalidOperationException();

            _isRecording = true;

            _frameGenerator = new CaptureFrameWait(
                _device,
                _captureItem,
                _sourceSize);

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
                    _audioDescriptor = new AudioStreamDescriptor(_audioGraph.EncodingProperties);
                    _mediaStreamSource.AddStreamDescriptor(_audioDescriptor);
                }

                var transcode = await _transcoder.PrepareMediaStreamSourceTranscodeAsync(_mediaStreamSource, destination, encodingProfile);
                await transcode.TranscodeAsync();
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
        }

        public bool IsClosed => _closed;

        private  void DisposeInternal()
        {
            _frameGenerator.Dispose();
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
            _mediaStreamSource.BufferTime = TimeSpan.FromMilliseconds(0);
            _mediaStreamSource.Starting += OnMediaStreamSourceStarting;
            _mediaStreamSource.SampleRequested += OnMediaStreamSourceSampleRequested;
            _mediaStreamSource.Closed += OnVideoClosed;

            // Create our transcoder
            _transcoder = new MediaTranscoder();
            _transcoder.HardwareAccelerationEnabled = true;

            void OnVideoClosed(MediaStreamSource sender, MediaStreamSourceClosedEventArgs args) {
                videoSource.Closed -= OnVideoClosed;
                videoSource.SampleRequested -= OnMediaStreamSourceSampleRequested;
                Debug.WriteLine($"{Name}: MediaStreamSource closed: {args?.Request?.Reason}");
                _audioGraph?.Stop();
            }
        }


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
                            Debug.WriteLine("null video frame");
                            args.Request.Sample = null;
                            this.DisposeInternal();
                            return;
                        }
                        
                        var timeStamp = frame.SystemRelativeTime - this.timeOffset;
                        var sample = MediaStreamSample.CreateFromDirect3D11Surface(frame.Surface, timeStamp);
                        args.Request.Sample = sample;
                    }
                    else if (args.Request.StreamDescriptor.GetType() == typeof(AudioStreamDescriptor))
                    {
                        var request = args.Request;

                        using var frame = GetNonEmptyFrame();
                        if (frame is null)
                        {
                            Debug.WriteLine("null audio frame");
                            args.Request.Sample = null;
                            DisposeInternal();
                            return;
                        }
                        using (AudioBuffer buffer = frame.LockBuffer(AudioBufferAccessMode.Write))
                        using (IMemoryBufferReference reference = buffer.CreateReference())
                        {
                            byte* dataInBytes;
                            uint capacityInBytes;
                            // Get the buffer from the AudioFrame
                            var byteAccess = reference.As<IMemoryBufferByteAccess>();
                            byteAccess.GetBuffer(out dataInBytes, out capacityInBytes);
                            byte[] bytes = new byte[capacityInBytes];
                            Marshal.Copy((IntPtr)dataInBytes, bytes, 0, (int)capacityInBytes);
                            var data_buffer = WindowsRuntimeBufferExtensions.AsBuffer(bytes, 0, (int)capacityInBytes);

                            var stamp = frame.RelativeTime.GetValueOrDefault();
                            var duration = frame.Duration.GetValueOrDefault();

                            var sample = MediaStreamSample.CreateFromBuffer(data_buffer, stamp);
                            sample.Duration = duration;
                            sample.KeyFrame = true;

                            request.Sample = sample;
                        }
                    }

                }
                catch (Exception e)
                {
                    Debug.WriteLine(e.Message);
                    Debug.WriteLine(e.StackTrace);
                    Debug.WriteLine(e);
                    args.Request.Sample = null;
                    DisposeInternal();
                }
            }
            else
            {
                Debug.WriteLine($"Not recording: rec: {_isRecording} closed: {_closed}");
                args.Request.Sample = null;
                DisposeInternal();
            }
        }

        AudioFrame? GetNonEmptyFrame(int maxTries = 48000) {
            for (int @try = 0; @try < maxTries; @try++) {
                var frame = _frameOutputNode.GetFrame();
                if (frame.Duration.GetValueOrDefault().Ticks != 0) {
                    return frame;
                }
                frame.Dispose();
            }
            Debug.WriteLine("unable to get a non-empty audio frame");
            return null;
        }

        
        private void OnMediaStreamSourceStarting(MediaStreamSource sender, MediaStreamSourceStartingEventArgs args)
        {
            MediaStreamSourceStartingRequest request = args.Request;

            using (var frame = _frameGenerator.WaitForNewFrame())
            {
                timeOffset = frame.SystemRelativeTime;
                //request.SetActualStartPosition(frame.SystemRelativeTime);
            }
            _audioGraph?.Start();
            using (var audioFrame = _frameOutputNode.GetFrame())
            {
                timeOffset = timeOffset + audioFrame.RelativeTime.GetValueOrDefault();
            }

            this.startReadinessTask.SetResult(new() { Value = timeOffset });
        }

        private IDirect3DDevice _device;

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
        private AudioGraph _audioGraph;
        private AudioDeviceInputNode _deviceInputNode;
        private AudioFrameOutputNode _frameOutputNode;
        private TimeSpan timeOffset = new TimeSpan();
        TaskCompletionSource<SystemRelativeTime> startReadinessTask = new();

    }
}
