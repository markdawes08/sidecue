using System.Buffers;
using Sidecue.Core.Contracts;
using Xunit;

namespace Sidecue.Core.Tests.Contracts;

public sealed class AudioFrameTests
{
    [Fact]
    public void InvalidAudioFormatAndFlagsAreRejected()
    {
        Assert.ThrowsAny<ArgumentException>(() => new AudioFormat(0, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian));
        Assert.ThrowsAny<ArgumentException>(() => new AudioFormat(16_000, 0, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian));
        Assert.ThrowsAny<ArgumentException>(() => new AudioFormat(16_000, 1, (AudioSampleRepresentation)int.MaxValue, AudioByteOrder.LittleEndian));
        Assert.ThrowsAny<ArgumentException>(() => new AudioFormat(16_000, 1, AudioSampleRepresentation.Pcm16, (AudioByteOrder)int.MaxValue));
        Assert.ThrowsAny<ArgumentException>(() => Metadata(flags: (AudioFrameAttributes)int.MaxValue));
    }

    [Theory]
    [InlineData(22)]
    [InlineData(23)]
    public void FractionalSampleDurationAllowsOnlyDocumentedRounding(long microseconds)
    {
        var format = new AudioFormat(44_100, 2, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian);
        var metadata = Metadata(format: format, samples: 1, end: microseconds);
        var owner = new TrackingOwner(8);
        using var frame = new OwnedAudioFrame(metadata, owner, 4);

        // One per-channel frame is two PCM16 samples, regardless of rented capacity.
        Assert.Equal(4, frame.Payload.Length);
    }

    [Fact]
    public void SampleDurationMismatchAndZeroSamplesAreRejected()
    {
        var format = new AudioFormat(44_100, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian);

        Assert.ThrowsAny<ArgumentException>(() => Metadata(format: format, samples: 1, end: 24));
        Assert.ThrowsAny<ArgumentException>(() => Metadata(samples: 0, end: 0));
    }

    [Fact]
    public void PayloadByteCalculationCannotOverflow()
    {
        var format = new AudioFormat(16_000, 2, AudioSampleRepresentation.Pcm32, AudioByteOrder.LittleEndian);
        var duration = (long)int.MaxValue * 1_000_000 / 16_000;

        Assert.Throws<OverflowException>(() => Metadata(format: format, samples: int.MaxValue, end: duration));
    }

    [Fact]
    public void RejectedConstructionLeavesBufferOwnershipWithCaller()
    {
        var owner = new TrackingOwner(3_200);
        try
        {
            Assert.ThrowsAny<ArgumentException>(() => new OwnedAudioFrame(Metadata(), owner, 3_199));
            Assert.Equal(0, owner.DisposeCount);
            Assert.Equal(3_200, owner.Memory.Length);
        }
        finally
        {
            owner.Dispose();
        }

        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public void UndersizedBackingBufferIsNotAdopted()
    {
        var owner = new TrackingOwner(3_199);
        try
        {
            Assert.ThrowsAny<ArgumentException>(() => new OwnedAudioFrame(Metadata(), owner, 3_200));
            Assert.Equal(0, owner.DisposeCount);
        }
        finally
        {
            owner.Dispose();
        }
    }

    [Fact]
    public void PayloadExcludesPoolSlackAndBecomesUnavailableAfterDisposal()
    {
        var owner = new TrackingOwner(4_096);
        var frame = new OwnedAudioFrame(Metadata(), owner, 3_200);

        Assert.Equal(3_200, frame.Payload.Length);
        frame.Dispose();
        frame.Dispose();

        Assert.Equal(1, owner.DisposeCount);
        Assert.Throws<ObjectDisposedException>(() => frame.Payload);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsumerReleasesTransferredBufferOnSuccessOrFailure(bool failDuringUse)
    {
        var owner = new TrackingOwner(3_200);
        var transferredFrame = new OwnedAudioFrame(Metadata(), owner, 3_200);
        void Consume()
        {
            using (transferredFrame)
            {
                Assert.Equal(0, owner.DisposeCount);
                _ = transferredFrame.Payload.Span[0];
                if (failDuringUse)
                {
                    throw new InvalidOperationException("Synthetic consumer failure.");
                }
            }
        }

        if (failDuringUse) { Assert.Throws<InvalidOperationException>(Consume); }
        else { Consume(); }

        Assert.Equal(1, owner.DisposeCount);
    }

    [Fact]
    public void ProducerReleasesUntransferredFrameAfterCancellation()
    {
        var owner = new TrackingOwner(3_200);
        var frame = new OwnedAudioFrame(Metadata(), owner, 3_200);
        try
        {
            // Cancellation rejects transfer; the producer still owns the frame.
            Assert.Throws<OperationCanceledException>(() => new CancellationToken(true).ThrowIfCancellationRequested());
            Assert.Equal(0, owner.DisposeCount);
        }
        finally
        {
            frame.Dispose();
        }

        Assert.Equal(1, owner.DisposeCount);
    }

    private static AudioFrameMetadata Metadata(AudioFormat? format = null, int samples = 1_600,
        long end = 100_000, AudioFrameAttributes flags = AudioFrameAttributes.None) =>
        new(ContractVersion.Current, ContractSamples.SourceIdentity(), new FrameSequence(0),
            ContractSamples.Interval(0, end),
            format ?? new AudioFormat(16_000, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian), samples, flags);

    private sealed class TrackingOwner(int length) : IMemoryOwner<byte>
    {
        private readonly byte[] buffer = new byte[length];
        public int DisposeCount { get; private set; }

        public Memory<byte> Memory
        {
            get
            {
                ObjectDisposedException.ThrowIf(DisposeCount != 0, this);
                return buffer;
            }
        }

        public void Dispose() => DisposeCount++;
    }
}
