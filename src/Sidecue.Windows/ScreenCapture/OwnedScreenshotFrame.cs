using System.Buffers;
using System.Security.Cryptography;

namespace Sidecue.Windows.ScreenCapture;

/// <summary>A single-owner in-memory screenshot of tightly packed BGRA32 pixels.</summary>
/// <remarks>
/// Ownership transfers only after successful construction. The caller keeps the
/// buffer if validation fails. Pixel access must finish before disposal, including
/// use of any cached memory view.
/// </remarks>
public sealed class OwnedScreenshotFrame : IDisposable
{
    private const int BytesPerPixel = 4;
    private const int MaximumDimension = 8192;
    private const int MaximumPixelBytes = 64 * 1024 * 1024;
    private IMemoryOwner<byte>? _owner;

    public OwnedScreenshotFrame(int width, int height, IMemoryOwner<byte> owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        int pixelByteCount = GetByteCount(width, height);
        if (owner.Memory.Length < pixelByteCount)
        {
            throw new ArgumentException("The owned buffer is shorter than the screenshot pixels.", nameof(owner));
        }

        Width = width;
        Height = height;
        Stride = width * BytesPerPixel;
        PixelByteCount = pixelByteCount;
        _owner = owner;
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public int PixelByteCount { get; }

    /// <summary>Gets the image pixels within the frame's lifetime.</summary>
    public ReadOnlyMemory<byte> Pixels
    {
        get
        {
            var owner = Volatile.Read(ref _owner) ?? throw new ObjectDisposedException(nameof(OwnedScreenshotFrame));
            return owner.Memory[..PixelByteCount];
        }
    }

    /// <summary>Returns the packed byte count for an image up to 8192 pixels per side and 64 MiB.</summary>
    public static int GetByteCount(int width, int height)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(width, MaximumDimension);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(height, MaximumDimension);

        int byteCount = width * height * BytesPerPixel;
        if (byteCount > MaximumPixelBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(height), "The screenshot dimensions exceed the 64 MiB pixel budget.");
        }

        return byteCount;
    }

    public void Dispose()
    {
        var owner = Interlocked.Exchange(ref _owner, null);
        if (owner is null)
        {
            return;
        }

        try
        {
            CryptographicOperations.ZeroMemory(owner.Memory.Span[..PixelByteCount]);
        }
        finally
        {
            owner.Dispose();
        }

        GC.SuppressFinalize(this);
    }
}
