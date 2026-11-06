namespace Sidecue.Core.Contracts;

public enum AudioSampleRepresentation
{
    Pcm16,
    Pcm24,
    Pcm32,
    FloatingPoint32
}

public enum AudioByteOrder
{
    LittleEndian,
    BigEndian
}

/// <summary>The interleaved sample format; channel frames are distinct from bytes.</summary>
public sealed record AudioFormat
{
    public AudioFormat(int sampleRate, int channels, AudioSampleRepresentation representation, AudioByteOrder byteOrder)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        if (!Enum.IsDefined(representation))
        {
            throw new ArgumentOutOfRangeException(nameof(representation));
        }

        if (!Enum.IsDefined(byteOrder))
        {
            throw new ArgumentOutOfRangeException(nameof(byteOrder));
        }

        SampleRate = sampleRate;
        Channels = channels;
        Representation = representation;
        ByteOrder = byteOrder;
        BytesPerSample = representation switch
        {
            AudioSampleRepresentation.Pcm16 => 2,
            AudioSampleRepresentation.Pcm24 => 3,
            AudioSampleRepresentation.Pcm32 or AudioSampleRepresentation.FloatingPoint32 => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(representation))
        };
        BytesPerFrame = checked(channels * BytesPerSample);
    }

    public int SampleRate { get; }
    public int Channels { get; }
    public AudioSampleRepresentation Representation { get; }
    public AudioByteOrder ByteOrder { get; }
    public int BytesPerSample { get; }
    public int BytesPerFrame { get; }
}
