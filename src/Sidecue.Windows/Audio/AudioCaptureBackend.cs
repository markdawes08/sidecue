using Sidecue.Core.Contracts;

namespace Sidecue.Windows.Audio;

/// <summary>
/// A native capture packet. Its data is borrowed and valid only until the callback returns.
/// Device position counts channel frames; the QPC value uses 100-nanosecond units.
/// </summary>
public delegate void CapturePacketCallback(ReadOnlySpan<byte> data, int sampleCount,
    AudioFrameAttributes flags, ulong devicePosition, ulong qpc100Nanoseconds);

/// <summary>One selected native audio endpoint with deterministic ownership.</summary>
/// <remarks>
/// Start is called once. Each backend serializes its packet callbacks and reports the
/// stopped callback only after its last packet. RequestStop requests termination without
/// joining. Dispose waits for callbacks to return and releases the native stream and
/// endpoint; it must not run on a capture callback thread. Callbacks must not wait for
/// consumers, a dispatcher or network/database operations.
/// </remarks>
public interface IAudioCaptureBackend : IDisposable
{
    AudioFormat Format { get; }
    void Start(CapturePacketCallback packet, Action<Exception?> stopped);
    void RequestStop();
}

public interface IAudioCaptureBackendFactory
{
    /// <summary>Resolves the requested source or fails; it never silently substitutes another.</summary>
    IAudioCaptureBackend Open(CaptureSourceSelection selection);
}
