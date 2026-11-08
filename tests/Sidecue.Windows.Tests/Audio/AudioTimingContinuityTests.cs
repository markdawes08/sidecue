using System.Buffers;
using System.Buffers.Binary;
using Sidecue.Application.Timing;
using Sidecue.Core.Contracts;
using Sidecue.Windows.Audio;
using Xunit;

namespace Sidecue.Windows.Tests.Audio;

public sealed class AudioTimingContinuityTests
{
    private const int Rate = 44_100;
    private const int PacketSamples = 441;
    private const long Start = 1_000_000;
    private const AudioFrameAttributes Estimated = AudioFrameAttributes.SessionTimestampEstimated;
    private static readonly AudioFormat Format = new(Rate, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian);

    [Fact]
    public void ContiguousDevicePositionsPreserveSamplesThroughBatchedCallbackEstimates()
    {
        var reference = Run(Enumerable.Range(0, 40).Select(i => Packet(i, Start + i * 10_000L, i * PacketSamples)).ToArray());
        long[] jitter = [0, -9_000, -19_000, 0];
        var batched = Run(Enumerable.Range(0, 40).Select(i => Packet(i, Start + i * 10_000L + jitter[i % jitter.Length], i * PacketSamples)).ToArray());

        Assert.Equal(reference.Samples, batched.Samples);
        Assert.InRange(batched.Samples.Length, 6_398, 6_400);
        Assert.DoesNotContain(batched.Gaps, gap => gap.Reason == GapReason.CaptureFailure);
        Assert.All(batched.Metadata, metadata => Assert.True((metadata.Flags & Estimated) != 0));
        AssertOrdered(batched.Metadata);
        // WDL may retain a short filter tail. If it does, the unsupported tail
        // must remain visible rather than being silently counted as output.
        if (batched.Samples.Length < 6_400)
        {
            var representedEnd = Start + (long)((Int128)batched.Samples.Length * 1_000_000 / 16_000);
            Assert.Contains(batched.Gaps, gap => gap.Certainty == TimingCertainty.Unknown &&
                gap.Interval.Start.Offset.Value <= representedEnd && gap.Interval.End.Offset.Value >= Start + 400_000);
        }
        var first = batched.Metadata[0];
        var map = new SentSampleMap(first.Source, first.Interval.TimelineEpoch, first.Format, 40);
        foreach (var metadata in batched.Metadata) { map.RecordConfirmed(metadata, TimingCertainty.Exact); }
        var mapped = map.Map(0, batched.Samples.Length);
        Assert.Equal(TimingCertainty.Estimated, mapped.Certainty);
        Assert.False(mapped.ContainsUncertainSend);
    }

    [Fact]
    public void AccumulatedEstimatedOverlapDoesNotDiscardPendingAudioAtRealBoundary()
    {
        var packets = Enumerable.Range(0, 20).Select(i => Packet(i, Start + i * 9_500L, i * PacketSamples)).ToList();
        packets.AddRange(Enumerable.Range(0, 3).Select(i => Packet(i + 20, Start + 250_000L + i * 10_000L,
            (i + 30) * PacketSamples, Estimated | (i == 0 ? AudioFrameAttributes.Discontinuity : AudioFrameAttributes.None))));
        var result = Run(packets);

        Assert.InRange(result.Samples.Length, 3_670, 3_680);
        Assert.DoesNotContain(result.Gaps, gap => gap.Reason == GapReason.CaptureFailure);
        Assert.Contains(result.Gaps, gap => gap.Reason == GapReason.CaptureDiscontinuity &&
            gap.Certainty == TimingCertainty.Unknown && gap.Interval.End.Offset.Value >= Start + 250_000);
        Assert.Contains(result.Metadata, metadata => (metadata.Flags & AudioFrameAttributes.Discontinuity) != 0 &&
            metadata.Interval.Start.Offset.Value >= Start + 250_000);
        AssertOrdered(result.Metadata);
    }

    [Fact]
    public void DevicePositionJumpRemainsAnExplicitUnknownBoundary()
    {
        var result = Run([
            Packet(0, Start, 0), Packet(1, Start + 10_000, PacketSamples),
            Packet(2, Start + 40_000, 4 * PacketSamples), Packet(3, Start + 50_000, 5 * PacketSamples)
        ]);

        Assert.Contains(result.Gaps, gap => gap.Reason == GapReason.CaptureDiscontinuity &&
            gap.Certainty == TimingCertainty.Unknown && gap.Interval.Start.Offset.Value <= Start + 20_000 &&
            gap.Interval.End.Offset.Value >= Start + 40_000);
        Assert.Contains(result.Metadata, metadata => (metadata.Flags & AudioFrameAttributes.Discontinuity) != 0);
        AssertOrdered(result.Metadata);
    }

    [Fact]
    public void DevicePositionJumpWithTouchingEstimatesStillCreatesBoundary()
    {
        var result = Run([
            Packet(0, Start, 0), Packet(1, Start + 10_000, PacketSamples),
            Packet(2, Start + 20_000, 4 * PacketSamples), Packet(3, Start + 30_000, 5 * PacketSamples)
        ]);

        Assert.Contains(result.Gaps, gap => gap.Reason == GapReason.CaptureDiscontinuity && gap.Certainty == TimingCertainty.Unknown);
        Assert.Contains(result.Metadata, metadata => (metadata.Flags & AudioFrameAttributes.Discontinuity) != 0);
        AssertOrdered(result.Metadata);
    }

    [Fact]
    public void LargeTimestampDivergenceCannotSilentlyShiftContinuousDeviceAudio()
    {
        var packets = Enumerable.Range(0, 15).Select(i => Packet(i,
            Start + i * 10_000L + (i < 10 ? 0 : 250_000L), i * PacketSamples)).ToArray();
        Result? result = null;
        var exception = Record.Exception(() => result = Run(packets));
        if (exception is not null)
        {
            Assert.IsType<InvalidOperationException>(exception);
            return;
        }

        Assert.NotNull(result);
        Assert.Contains(result.Gaps, gap => gap.Reason == GapReason.CaptureDiscontinuity && gap.Certainty == TimingCertainty.Unknown);
        AssertOrdered(result.Metadata);
    }

    [Fact]
    public void MissingDevicePositionCannotBridgeAChangedPositionAnchor()
    {
        var result = Run([
            Packet(0, Start, 0), Packet(1, Start + 10_000, PacketSamples),
            Packet(2, Start + 20_000, null),
            // A sub-millisecond position jump after the missing packet used to
            // fit inside the timing tolerance and conceal the lost evidence.
            Packet(3, Start + 30_000, (3 * PacketSamples) + 44),
            Packet(4, Start + 40_000, (4 * PacketSamples) + 44),
            Packet(5, Start + 50_000, (5 * PacketSamples) + 44)
        ]);

        Assert.Contains(result.Gaps, gap => gap.Reason == GapReason.CaptureDiscontinuity && gap.Certainty == TimingCertainty.Unknown &&
            gap.Interval.Start.Offset.Value <= Start + 40_000 && gap.Interval.End.Offset.Value >= Start + 20_000);
        Assert.Contains(result.Metadata, metadata => (metadata.Flags & AudioFrameAttributes.Discontinuity) != 0);
        AssertOrdered(result.Metadata);
    }

    [Fact]
    public void SegmentCloseEmitsSupportedPendingPrefixAndReportsUnsupportedRemainder()
    {
        // Independently rounded, nominally trusted intervals overlap by just
        // one microsecond each. Over many packets, the supported end falls
        // behind the fixed-rate output timeline by several samples.
        var packets = Enumerable.Range(0, 300).Select(i => Packet(i,
            Start + (i * 9_999L), i * PacketSamples, AudioFrameAttributes.None)).ToList();
        var supportedEnd = Start + (299 * 9_999L) + 10_000L;
        packets.Add(Packet(300, Start + 3_100_000, 310 * PacketSamples, AudioFrameAttributes.Discontinuity));
        var result = Run(packets);

        var beforeBoundary = result.Metadata.Where(metadata => metadata.Interval.End.Offset.Value <= supportedEnd + 1).ToArray();
        Assert.InRange(beforeBoundary.Sum(metadata => metadata.SampleCount), 47_900, 47_999);
        Assert.Contains(result.Gaps, gap => gap.Reason == GapReason.CaptureFailure && gap.Certainty == TimingCertainty.Unknown &&
            gap.Interval.DurationMicroseconds > 0 && gap.Interval.Start.Offset.Value >= beforeBoundary[^1].Interval.End.Offset.Value);
        AssertOrdered(result.Metadata);
    }

    [Fact]
    public void OverlappingEstimatesWithoutDeviceEvidenceRemainExplicitlyUncertain()
    {
        var result = Run([
            Packet(0, Start, null), Packet(1, Start + 5_000, null),
            Packet(2, Start + 20_000, null), Packet(3, Start + 30_000, null)
        ]);

        Assert.Contains(result.Gaps, gap => gap.Reason == GapReason.CaptureDiscontinuity && gap.Certainty == TimingCertainty.Unknown);
        Assert.True(result.Samples.Length < 4 * 160);
        AssertOrdered(result.Metadata);
    }

    [Fact]
    public void NativeTimestampErrorDoesNotBecomeTrustedDeviceContinuity()
    {
        var result = Run([
            Packet(0, Start, 0),
            Packet(1, Start + 10_000, PacketSamples, Estimated | AudioFrameAttributes.TimestampError),
            Packet(2, Start + 20_000, 2 * PacketSamples), Packet(3, Start + 30_000, 3 * PacketSamples)
        ]);

        Assert.Contains(result.Metadata, metadata => (metadata.Flags & AudioFrameAttributes.TimestampError) != 0);
        Assert.Contains(result.Metadata, metadata => metadata.Interval.Start.Offset.Value >= Start + 20_000 &&
            (metadata.Flags & AudioFrameAttributes.TimestampError) == 0);
        Assert.Contains(result.Gaps, gap => gap.Certainty == TimingCertainty.Unknown);
        AssertOrdered(result.Metadata);
    }

    private static void AssertOrdered(IReadOnlyList<AudioFrameMetadata> frames)
    {
        Assert.NotEmpty(frames);
        Assert.All(frames.Zip(frames.Skip(1)), pair => Assert.True(pair.First.Interval.End.Offset.Value <= pair.Second.Interval.Start.Offset.Value));
    }

    private static Result Run(IReadOnlyList<InputPacket> packets)
    {
        var session = new SessionId(Guid.NewGuid());
        var source = new SourceIdentity(session, new SourceId(Guid.NewGuid()), new StreamEpoch(0));
        var origin = new TimelinePosition(new TimelineEpochId(Guid.NewGuid()), new SessionOffset(0));
        var request = new CaptureRequest(ContractVersion.Current, source, new SessionClockAnchor(session, origin, new UtcTimestamp(DateTimeOffset.UnixEpoch)),
            new CaptureSourceSelection(CaptureSourceKind.RemotePlayback, "synthetic"), new AudioBufferPolicy(2_000_000, 2_000_000));
        var samples = new List<short>();
        var metadata = new List<AudioFrameMetadata>();
        var gaps = new List<CaptureGap>();
        using var normalizer = new StreamingAudioNormalizer(request, 1_600, frame =>
        {
            using (frame)
            {
                metadata.Add(frame.Metadata);
                var bytes = frame.Payload.Span;
                for (var i = 0; i < bytes.Length; i += 2) { samples.Add(BinaryPrimitives.ReadInt16LittleEndian(bytes.Slice(i, 2))); }
            }
        }, gaps.Add);
        foreach (var packet in packets)
        {
            var data = new byte[PacketSamples * 2];
            for (var i = 0; i < PacketSamples; i++)
            {
                short value = (short)(Math.Sin(((packet.Sequence * PacketSamples) + i) * 0.03) * 20_000);
                BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(i * 2, 2), value);
            }
            var interval = new TimelineInterval(new TimelinePosition(origin.TimelineEpoch, new SessionOffset(packet.Start)),
                new TimelinePosition(origin.TimelineEpoch, new SessionOffset(packet.Start + 10_000)));
            var info = new AudioFrameMetadata(request.Version, source, new FrameSequence(packet.Sequence), interval, Format,
                PacketSamples, packet.Flags, packet.DevicePosition is { } position ? new RawClockPosition(position, Rate) : null);
            using var frame = new OwnedAudioFrame(info, new Owner(data), data.Length);
            normalizer.Process(frame);
        }
        normalizer.Complete();
        return new Result(samples.ToArray(), metadata, gaps);
    }

    private static InputPacket Packet(long sequence, long start, long? devicePosition, AudioFrameAttributes flags = Estimated) => new(sequence, start, devicePosition, flags);
    private sealed record InputPacket(long Sequence, long Start, long? DevicePosition, AudioFrameAttributes Flags);
    private sealed record Result(short[] Samples, List<AudioFrameMetadata> Metadata, List<CaptureGap> Gaps);

    private sealed class Owner(byte[] data) : IMemoryOwner<byte>
    {
        private byte[]? _data = data;
        public Memory<byte> Memory => _data ?? throw new ObjectDisposedException(nameof(Owner));
        public void Dispose() { var value = Interlocked.Exchange(ref _data, null); if (value is not null) { Array.Clear(value); } GC.SuppressFinalize(this); }
    }
}
