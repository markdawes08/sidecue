using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sidecue.Core.Contracts;

namespace Sidecue.Windows.Audio;

/// <summary>Opens the selected default Windows endpoint in shared capture mode.</summary>
public sealed class NAudioCaptureBackendFactory : IAudioCaptureBackendFactory
{
    /// <summary>The only selector issued by this prototype. Device IDs are never encoded here.</summary>
    public const string SystemDefaultSelector = "system-default";

    public IAudioCaptureBackend Open(CaptureSourceSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        if (!string.Equals(selection.SelectorId, SystemDefaultSelector, StringComparison.Ordinal))
        {
            throw new NotSupportedException("The requested capture selector is not available.");
        }
        if (SynchronizationContext.Current is not null)
        {
            throw new InvalidOperationException("Open the audio endpoint on a worker without a synchronization context.");
        }

        var flow = selection.Kind switch
        {
            CaptureSourceKind.Microphone => DataFlow.Capture,
            CaptureSourceKind.RemotePlayback => DataFlow.Render,
            _ => throw new ArgumentOutOfRangeException(nameof(selection))
        };
        using var enumerator = new MMDeviceEnumerator();
        // Resolve each default multimedia endpoint once; never silently re-route a running stream.
        var device = enumerator.GetDefaultAudioEndpoint(flow, Role.Multimedia);
        WasapiRecorder? recorder = null;
        try
        {
            // Read and validate the exact mix format through a separately owned client.
            // Providing it to the builder also prevents a failed format query inside
            // NAudio's constructor from orphaning its newly acquired client.
            using var formatClient = device.CreateAudioClient();
            var nativeFormat = formatClient.MixFormat;
            var format = MapFormat(nativeFormat);
            var builder = new WasapiRecorderBuilder().WithDevice(device).WithFormat(nativeFormat);
            if (selection.Kind == CaptureSourceKind.RemotePlayback) { builder.WithLoopbackCapture(); }
            recorder = builder.Build();
            return new NAudioCaptureBackend(device, recorder, format);
        }
        catch
        {
            try { recorder?.Dispose(); }
            finally { device.Dispose(); }
            throw;
        }
    }

    internal static AudioFormat MapFormat(WaveFormat nativeFormat)
    {
        ArgumentNullException.ThrowIfNull(nativeFormat);
        var encoding = nativeFormat.Encoding;
        if (nativeFormat is WaveFormatExtensible extensible)
        {
            if (extensible.ValidBitsPerSample != nativeFormat.BitsPerSample)
            {
                throw new NotSupportedException("Audio samples with valid bits smaller than their container are not supported.");
            }
            encoding = extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_PCM ? WaveFormatEncoding.Pcm
                : extensible.SubFormat == AudioMediaSubtypes.MEDIASUBTYPE_IEEE_FLOAT ? WaveFormatEncoding.IeeeFloat
                : throw new NotSupportedException("The audio endpoint's sample encoding is not supported.");
        }

        var representation = (encoding, nativeFormat.BitsPerSample) switch
        {
            (WaveFormatEncoding.Pcm, 16) => AudioSampleRepresentation.Pcm16,
            (WaveFormatEncoding.Pcm, 24) => AudioSampleRepresentation.Pcm24,
            (WaveFormatEncoding.Pcm, 32) => AudioSampleRepresentation.Pcm32,
            (WaveFormatEncoding.IeeeFloat, 32) => AudioSampleRepresentation.FloatingPoint32,
            _ => throw new NotSupportedException("The audio endpoint's sample encoding is not supported.")
        };
        var format = new AudioFormat(nativeFormat.SampleRate, nativeFormat.Channels,
            representation, AudioByteOrder.LittleEndian);
        if (format.BytesPerFrame != nativeFormat.BlockAlign)
        {
            throw new NotSupportedException("The audio endpoint's block alignment is not supported.");
        }
        return format;
    }
}

internal sealed class NAudioCaptureBackend(MMDevice device, WasapiRecorder recorder, AudioFormat format) : IAudioCaptureBackend
{
    private readonly object _lifecycle = new();
    private CapturePacketCallback? _packet;
    private Action<Exception?>? _stopped;
    private int _started;
    private int _disposed;

    public AudioFormat Format { get; } = format;

    public void Start(CapturePacketCallback packet, Action<Exception?> stopped)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ArgumentNullException.ThrowIfNull(stopped);
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            if (_started != 0) { throw new InvalidOperationException("This capture endpoint has already started."); }
            _started = 1;
            _packet = packet;
            _stopped = stopped;
            recorder.DataAvailable += OnDataAvailable;
            recorder.RecordingStopped += OnRecordingStopped;
            try { recorder.StartRecording(); }
            catch
            {
                Dispose();
                throw;
            }
        }
    }

    public void RequestStop()
    {
        if (Volatile.Read(ref _disposed) == 0) { recorder.StopRecording(); }
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) { return; }
            try
            {
                // The owner disposes from a worker, never synchronously from a callback.
                recorder.Dispose();
            }
            finally
            {
                recorder.DataAvailable -= OnDataAvailable;
                recorder.RecordingStopped -= OnRecordingStopped;
                try { device.Dispose(); }
                finally
                {
                    _packet = null;
                    _stopped = null;
                    GC.SuppressFinalize(this);
                }
            }
        }
    }

    private void OnDataAvailable(ReadOnlySpan<byte> data, AudioClientBufferFlags flags, long devicePosition, long qpcPosition)
    {
        if (data.Length == 0) { return; }
        if (data.Length % Format.BytesPerFrame != 0) { throw new InvalidDataException("Native audio packet has an invalid frame alignment."); }
        var attributes = AudioFrameAttributes.None;
        if ((flags & AudioClientBufferFlags.Silent) != 0) { attributes |= AudioFrameAttributes.Silence; }
        if ((flags & AudioClientBufferFlags.DataDiscontinuity) != 0) { attributes |= AudioFrameAttributes.Discontinuity; }
        if ((flags & AudioClientBufferFlags.TimestampError) != 0) { attributes |= AudioFrameAttributes.TimestampError; }
        _packet?.Invoke(data, data.Length / Format.BytesPerFrame, attributes,
            unchecked((ulong)devicePosition), unchecked((ulong)qpcPosition));
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs e) => _stopped?.Invoke(e.Exception);
}
