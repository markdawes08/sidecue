using System.Buffers;
using Sidecue.Windows.ScreenCapture;
using Xunit;

namespace Sidecue.Windows.Tests.ScreenCapture;

public sealed class OwnedScreenshotFrameTests
{
    [Fact]
    public void ExposesPackedPixelsAndClearsThemBeforeReturningTheBuffer()
    {
        var owner = new RecordingOwner(32);
        using var frame = new OwnedScreenshotFrame(3, 2, owner);

        Assert.Equal(3, frame.Width);
        Assert.Equal(2, frame.Height);
        Assert.Equal(12, frame.Stride);
        Assert.Equal(24, frame.PixelByteCount);
        Assert.Equal(24, frame.Pixels.Length);
        Assert.Equal((byte)0xAB, frame.Pixels.Span[0]);

        frame.Dispose();
        frame.Dispose();

        Assert.Equal(1, owner.DisposeCalls);
        Assert.NotNull(owner.BufferAtDispose);
        byte[] returnedBuffer = owner.BufferAtDispose;
        Assert.All(returnedBuffer[..24], pixel => Assert.Equal((byte)0, pixel));
        Assert.All(returnedBuffer[24..], value => Assert.Equal((byte)0xAB, value));
        Assert.Throws<ObjectDisposedException>(() => frame.Pixels);
    }

    [Fact]
    public void ValidatesTheExactPixelBudgetBeforeAllocation()
    {
        Assert.Equal(64 * 1024 * 1024, OwnedScreenshotFrame.GetByteCount(8192, 2048));
        Assert.Throws<ArgumentOutOfRangeException>(() => OwnedScreenshotFrame.GetByteCount(8192, 2049));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, -1)]
    [InlineData(8193, 1)]
    [InlineData(1, int.MaxValue)]
    public void RejectsUnsupportedDimensions(int width, int height)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OwnedScreenshotFrame.GetByteCount(width, height));
    }

    [Fact]
    public void RejectedConstructionLeavesTheBufferWithTheCaller()
    {
        using var owner = new RecordingOwner(23);

        Assert.Throws<ArgumentException>(() => new OwnedScreenshotFrame(3, 2, owner));

        Assert.Equal(0, owner.DisposeCalls);
        Assert.Equal((byte)0xAB, owner.Memory.Span[0]);
    }

    [Fact]
    public void ReleasesTheOwnerIfPixelWipingThrows()
    {
        var owner = new RecordingOwner(4);
        using var frame = new OwnedScreenshotFrame(1, 1, owner);
        owner.RejectMemoryAccess = true;

        Assert.Throws<InvalidOperationException>(frame.Dispose);
        frame.Dispose();

        Assert.Equal(1, owner.DisposeCalls);
        Assert.Throws<ObjectDisposedException>(() => frame.Pixels);
    }

    private sealed class RecordingOwner : IMemoryOwner<byte>
    {
        private readonly byte[] _buffer;

        public RecordingOwner(int length)
        {
            _buffer = new byte[length];
            Array.Fill(_buffer, (byte)0xAB);
        }

        public int DisposeCalls { get; private set; }
        public byte[]? BufferAtDispose { get; private set; }
        public bool RejectMemoryAccess { get; set; }

        public Memory<byte> Memory => RejectMemoryAccess
            ? throw new InvalidOperationException("Synthetic memory access failure.")
            : _buffer;

        public void Dispose()
        {
            DisposeCalls++;
            BufferAtDispose = (byte[])_buffer.Clone();
        }
    }
}
