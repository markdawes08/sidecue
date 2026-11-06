using System.Buffers;

namespace Sidecue.Core.Contracts;

/// <summary>A single-owner audio lease with immutable metadata and bounded payload access.</summary>
/// <remarks>
/// Ownership transfers only after successful construction. On rejection, the caller
/// still owns the buffer. After construction, transfer this frame between producer
/// and consumer; dispose it on rejection, dropping, cancellation or completed use.
/// Payload access and disposal must not race. A cached memory view must not outlive
/// the frame, including while an asynchronous send is still using that view.
/// </remarks>
public sealed class OwnedAudioFrame : IDisposable
{
    private IMemoryOwner<byte>? _owner;

    public OwnedAudioFrame(AudioFrameMetadata metadata, IMemoryOwner<byte> owner, int payloadByteCount)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(owner);
        if (payloadByteCount != metadata.PayloadByteCount)
        {
            throw new ArgumentException("Payload byte count must match the sample count and format.", nameof(payloadByteCount));
        }

        if (owner.Memory.Length < payloadByteCount)
        {
            throw new ArgumentException("The owned buffer is shorter than the declared payload.", nameof(owner));
        }

        Metadata = metadata;
        _owner = owner;
    }

    public AudioFrameMetadata Metadata { get; }

    /// <summary>Only the declared payload, not unused capacity of a rented buffer.</summary>
    public ReadOnlyMemory<byte> Payload
    {
        get
        {
            var owner = Volatile.Read(ref _owner) ?? throw new ObjectDisposedException(nameof(OwnedAudioFrame));
            return owner.Memory[..Metadata.PayloadByteCount];
        }
    }

    public void Dispose()
    {
        // Null the lease before releasing it, so repeated cleanup cannot return
        // the same buffer twice, even if the owner's Dispose implementation fails.
        Interlocked.Exchange(ref _owner, null)?.Dispose();
        GC.SuppressFinalize(this);
    }
}
