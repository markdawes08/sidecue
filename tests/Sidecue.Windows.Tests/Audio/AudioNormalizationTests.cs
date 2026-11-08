using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;
using Sidecue.Windows.Audio;
using Xunit;

namespace Sidecue.Windows.Tests.Audio;

public sealed class AudioNormalizationTests
{
    [Fact]
    public async Task SplitFloatPacketsMatchOneContinuousPacketAt44100Hz()
    {
        var format = new AudioFormat(44_100, 2, AudioSampleRepresentation.FloatingPoint32, AudioByteOrder.LittleEndian);
        var request = Request();
        var samples = Enumerable.Range(0, 4_410).Select(i => (float)(Math.Sin(i * 0.03) * 0.5)).ToArray();
        var whole = await NormalizeAsync(request, format, [(0L, 0L, samples, AudioFrameAttributes.None)]);
        var split = Enumerable.Range(0, 10).Select(i => ((long)i, i * 10_000L, samples.Skip(i * 441).Take(441).ToArray(), AudioFrameAttributes.None)).ToArray();
        var pieces = await NormalizeAsync(request, format, split);

        Assert.Equal(whole.Samples, pieces.Samples);
        Assert.InRange(whole.Samples.Length, 1_598, 1_600);
        Assert.All(pieces.Metadata, metadata =>
        {
            Assert.Equal(request.Source, metadata.Source);
            Assert.Equal(16_000, metadata.Format.SampleRate);
            Assert.Equal(1, metadata.Format.Channels);
            Assert.Equal(AudioSampleRepresentation.Pcm16, metadata.Format.Representation);
            Assert.True(metadata.Interval.End.Offset.Value <= 100_001);
        });
    }

    [Fact]
    public async Task Pcm24DownmixAndFullScaleAreClippedAndRepresentable()
    {
        var request = Request();
        var format = new AudioFormat(16_000, 2, AudioSampleRepresentation.Pcm24, AudioByteOrder.LittleEndian);
        var packets = new[]
        {
            (0L, 0L, Enumerable.Repeat(-1f, 1_600).ToArray(), AudioFrameAttributes.None),
            (1L, 100_000L, Enumerable.Repeat(1f, 1_600).ToArray(), AudioFrameAttributes.None)
        };
        var result = await NormalizeAsync(request, format, packets);
        Assert.Contains(short.MinValue, result.Samples);
        Assert.Contains(result.Samples, sample => sample > 32_000);
    }

    [Fact]
    public async Task TrustedSmallGapSplitsOutputAndBoundaryFlagAppearsOnlyOnce()
    {
        var request = Request();
        var format = new AudioFormat(16_000, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian);
        var positive = Enumerable.Repeat(0.4f, 1_600).ToArray();
        var negative = Enumerable.Repeat(-0.4f, 1_600).ToArray();
        var result = await NormalizeAsync(request, format,
        [
            (0L, 0L, positive, AudioFrameAttributes.None),
            (1L, 100_500L, negative, AudioFrameAttributes.None),
            (2L, 200_500L, negative, AudioFrameAttributes.None),
            (3L, 300_500L, negative, AudioFrameAttributes.None)
        ]);
        Assert.Contains(result.Gaps, gap => gap.Reason == GapReason.CaptureDiscontinuity &&
            gap.Interval.Start.Offset.Value <= 100_000 && gap.Interval.End.Offset.Value >= 100_500 && gap.Certainty == TimingCertainty.Unknown);
        Assert.DoesNotContain(result.Gaps, gap => gap.Interval.Start.Offset.Value <= 100_000 && gap.Interval.End.Offset.Value >= 400_000);
        var boundaryFrames = result.Metadata.Where(metadata => (metadata.Flags & AudioFrameAttributes.Discontinuity) != 0).ToArray();
        Assert.Single(boundaryFrames);
        Assert.True(boundaryFrames[0].Interval.Start.Offset.Value >= 100_500);
        Assert.Contains(result.Metadata, metadata => metadata.Interval.Start.Offset.Value >= 200_500 &&
            (metadata.Flags & AudioFrameAttributes.Discontinuity) == 0);
    }

    [Fact]
    public async Task NativeTimestampErrorDoesNotPoisonLaterHealthyPackets()
    {
        var request = Request();
        var format = new AudioFormat(16_000, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian);
        var data = Enumerable.Repeat(0.25f, 1_600).ToArray();
        var result = await NormalizeAsync(request, format,
        [
            (0L, 0L, data, AudioFrameAttributes.TimestampError),
            (1L, 100_000L, data, AudioFrameAttributes.None),
            (2L, 200_000L, data, AudioFrameAttributes.None)
        ]);
        Assert.Contains(result.Metadata, metadata => (metadata.Flags & AudioFrameAttributes.TimestampError) != 0);
        Assert.Contains(result.Metadata, metadata => metadata.Interval.Start.Offset.Value >= 100_000 &&
            (metadata.Flags & AudioFrameAttributes.TimestampError) == 0);
    }

    [Fact]
    public async Task SlowConsumerSeesBoundedOutputAndCoalescedGap()
    {
        var request = Request();
        var source = new FakeRun(request);
        await using var run = new AudioNormalizationRun(source, request, new AudioBufferPolicy(100_000, 3_200));
        var format = new AudioFormat(16_000, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian);
        for (var i = 0; i < 10; i++)
        {
            source.Enqueue(Frame(request, format, i, i * 100_000L, Enumerable.Repeat(0.1f, 1_600).ToArray(), AudioFrameAttributes.None));
        }
        source.Complete();
        await run.StopAsync(CaptureStopReason.End, TestContext.Current.CancellationToken);
        var output = await CollectAsync(run);
        Assert.True(output.Metadata.Sum(item => item.PayloadByteCount) <= 3_200);
        Assert.Contains(output.Gaps, gap => gap.Reason == GapReason.QueueOverflow && gap.Source == request.Source);
        Assert.True(output.Gaps.Count(gap => gap.Reason == GapReason.QueueOverflow) < 9);
        Assert.Equal(source.OwnersCreated, source.OwnersDisposed);
    }

    [Fact]
    public async Task BadStatusStopsLiveSourceAndRetainsFailureWithoutAnotherFrame()
    {
        var request = Request();
        var source = new FakeRun(request);
        await using var run = new AudioNormalizationRun(source, request, new AudioBufferPolicy(200_000, 6_400));
        var other = new SourceIdentity(request.Source.SessionId, new SourceId(Guid.NewGuid()), request.Source.StreamEpoch);
        source.SendStatus(new CaptureStatusEvent(ContractVersion.Current, other, request.ClockAnchor.Origin, CaptureState.Live));
        await source.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(CaptureState.Failed, run.TerminalStatus?.State);
        Assert.Equal(GapReason.CaptureFailure, run.TerminalStatus?.Gap?.Reason);
        Assert.Equal(TimingCertainty.Unknown, run.TerminalStatus?.Gap?.Certainty);
    }

    [Fact]
    public async Task FullStatusPathStopsCaptureAndRetainsTheTriggeringLoss()
    {
        var request = Request();
        var source = new FakeRun(request);
        await using var run = new AudioNormalizationRun(source, request, new AudioBufferPolicy(200_000, 6_400));
        for (var i = 0; i < 17; i++)
        {
            var end = new TimelinePosition(request.ClockAnchor.Origin.TimelineEpoch, new SessionOffset((i + 1) * 1_000));
            var gap = new CaptureGap(request.Version, request.Source, new TimelineInterval(end, end),
                i % 2 == 0 ? GapReason.QueueOverflow : GapReason.CaptureDiscontinuity, TimingCertainty.Unknown);
            source.SendStatus(new CaptureStatusEvent(request.Version, request.Source, end, CaptureState.Degraded, gap: gap));
        }
        await source.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(CaptureState.Failed, run.TerminalStatus?.State);
        Assert.Equal(TimingCertainty.Unknown, run.TerminalStatus?.Gap?.Certainty);
        Assert.True(run.TerminalStatus?.Gap?.Interval.End.Offset.Value >= 17_000);
        await run.StopAsync(CaptureStopReason.End, TestContext.Current.CancellationToken);
        var statuses = new List<CaptureStatusEvent>();
        await foreach (var status in run.ReadStatusAsync(TestContext.Current.CancellationToken))
        {
            statuses.Add(status);
        }
        Assert.Equal(CaptureState.Failed, statuses[^1].State);
        Assert.True(statuses[^1].Gap?.Interval.End.Offset.Value >= 17_000);
    }

    [Fact]
    public async Task InvalidFloatReportsItsKnownIntervalAsUncertainFailure()
    {
        var request = Request();
        var source = new FakeRun(request);
        await using var run = new AudioNormalizationRun(source, request, new AudioBufferPolicy(200_000, 6_400));
        var format = new AudioFormat(16_000, 1, AudioSampleRepresentation.FloatingPoint32, AudioByteOrder.LittleEndian);
        source.Enqueue(Frame(request, format, 0, 50_000, [float.NaN], AudioFrameAttributes.None));
        await source.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(CaptureState.Failed, run.TerminalStatus?.State);
        Assert.Equal(GapReason.CaptureFailure, run.TerminalStatus?.Gap?.Reason);
        Assert.True(run.TerminalStatus?.Gap?.Interval.End.Offset.Value >= 50_062);
    }

    [Fact]
    public async Task OverlappingSourceIntervalsFailWithoutEmittingAnOverlappingTimeline()
    {
        var request = Request();
        var source = new FakeRun(request);
        await using var run = new AudioNormalizationRun(source, request, new AudioBufferPolicy(300_000, 9_600));
        var format = new AudioFormat(16_000, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian);
        source.Enqueue(Frame(request, format, 0, 0, Enumerable.Repeat(0.1f, 1_600).ToArray(), AudioFrameAttributes.None));
        source.Enqueue(Frame(request, format, 1, 50_000, Enumerable.Repeat(0.2f, 1_600).ToArray(), AudioFrameAttributes.None));
        await source.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        Assert.Equal(CaptureState.Failed, run.TerminalStatus?.State);
        Assert.Equal(TimingCertainty.Unknown, run.TerminalStatus?.Gap?.Certainty);
        Assert.True(run.TerminalStatus?.Gap?.Interval.End.Offset.Value >= 150_000);
        var result = await CollectAsync(run);
        Assert.All(result.Metadata, metadata => Assert.True(metadata.Interval.End.Offset.Value <= 100_001));
    }

    [Fact]
    public async Task OverlappingCallbackEstimatesReportLossAndRecoverWithOrderedOutput()
    {
        var request = Request();
        var format = new AudioFormat(16_000, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian);
        var data = Enumerable.Repeat(0.25f, 1_600).ToArray();
        var result = await NormalizeAsync(request, format,
        [
            (0L, 0L, data, AudioFrameAttributes.SessionTimestampEstimated),
            (1L, 90_000L, data, AudioFrameAttributes.SessionTimestampEstimated),
            (2L, 190_000L, data, AudioFrameAttributes.SessionTimestampEstimated),
            (3L, 290_000L, data, AudioFrameAttributes.SessionTimestampEstimated)
        ]);
        Assert.Contains(result.Gaps, gap => gap.Reason == GapReason.CaptureDiscontinuity &&
            gap.Certainty == TimingCertainty.Unknown && gap.Interval.Start.Offset.Value <= 90_000 &&
            gap.Interval.End.Offset.Value >= 190_000);
        Assert.Contains(result.Metadata, metadata => metadata.Interval.Start.Offset.Value >= 190_000 &&
            (metadata.Flags & AudioFrameAttributes.Discontinuity) != 0);
        Assert.All(result.Metadata.Zip(result.Metadata.Skip(1)), pair =>
            Assert.True(pair.First.Interval.End.Offset.Value <= pair.Second.Interval.Start.Offset.Value));
    }

    private static async Task<Normalized> NormalizeAsync(CaptureRequest request, AudioFormat format,
        IReadOnlyList<(long Sequence, long Start, float[] Samples, AudioFrameAttributes Flags)> packets)
    {
        var source = new FakeRun(request);
        await using var run = new AudioNormalizationRun(source, request, new AudioBufferPolicy(3_000_000, 100_000));
        foreach (var packet in packets) { source.Enqueue(Frame(request, format, packet.Sequence, packet.Start, packet.Samples, packet.Flags)); }
        source.Complete();
        await run.StopAsync(CaptureStopReason.End, TestContext.Current.CancellationToken);
        var result = await CollectAsync(run);
        Assert.Equal(source.OwnersCreated, source.OwnersDisposed);
        return result;
    }

    private static async Task<Normalized> CollectAsync(AudioNormalizationRun run)
    {
        var samples = new List<short>();
        var metadata = new List<AudioFrameMetadata>();
        var gaps = new List<CaptureGap>();
        await foreach (var frame in run.ReadFramesAsync(TestContext.Current.CancellationToken))
        {
            using (frame)
            {
                metadata.Add(frame.Metadata);
                var bytes = frame.Payload.ToArray();
                for (var i = 0; i < bytes.Length; i += 2) { samples.Add(BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(i, 2))); }
            }
        }
        await foreach (var status in run.ReadStatusAsync(TestContext.Current.CancellationToken))
        {
            if (status.Gap is not null) { gaps.Add(status.Gap); }
        }
        return new(samples.ToArray(), metadata, gaps);
    }

    private static CaptureRequest Request()
    {
        var session = new SessionId(Guid.NewGuid());
        var source = new SourceIdentity(session, new SourceId(Guid.NewGuid()), new StreamEpoch(0));
        var anchor = new SessionClockAnchor(session, new TimelinePosition(new TimelineEpochId(Guid.NewGuid()), new SessionOffset(0)),
            new UtcTimestamp(DateTimeOffset.UnixEpoch));
        return new(ContractVersion.Current, source, anchor, new CaptureSourceSelection(CaptureSourceKind.Microphone, "synthetic"),
            new AudioBufferPolicy(2_000_000, 2_000_000));
    }

    private static OwnedAudioFrame Frame(CaptureRequest request, AudioFormat format, long sequence, long start, float[] samples, AudioFrameAttributes flags)
    {
        var bytes = new byte[samples.Length * format.BytesPerFrame];
        for (var frame = 0; frame < samples.Length; frame++)
        {
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var offset = frame * format.BytesPerFrame + channel * format.BytesPerSample;
                switch (format.Representation)
                {
                    case AudioSampleRepresentation.Pcm16:
                        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(offset, 2), samples[frame] <= -1 ? short.MinValue : (short)(samples[frame] * short.MaxValue));
                        break;
                    case AudioSampleRepresentation.Pcm24:
                        int value = samples[frame] <= -1 ? -8_388_608 : (int)(samples[frame] * 8_388_607);
                        bytes[offset] = (byte)value; bytes[offset + 1] = (byte)(value >> 8); bytes[offset + 2] = (byte)(value >> 16);
                        break;
                    case AudioSampleRepresentation.FloatingPoint32:
                        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(offset, 4), BitConverter.SingleToInt32Bits(samples[frame]));
                        break;
                    default: throw new NotSupportedException();
                }
            }
        }
        var end = start + (long)((Int128)samples.Length * 1_000_000 / format.SampleRate);
        var interval = new TimelineInterval(new TimelinePosition(request.ClockAnchor.Origin.TimelineEpoch, new SessionOffset(start)),
            new TimelinePosition(request.ClockAnchor.Origin.TimelineEpoch, new SessionOffset(end)));
        return new(new AudioFrameMetadata(ContractVersion.Current, request.Source, new FrameSequence(sequence), interval, format, samples.Length, flags),
            new TestOwner(bytes), bytes.Length);
    }

    private sealed record Normalized(short[] Samples, List<AudioFrameMetadata> Metadata, List<CaptureGap> Gaps);

    private sealed class TestOwner(byte[] bytes) : IMemoryOwner<byte>
    {
        private byte[]? _bytes = bytes;
        public Memory<byte> Memory => _bytes ?? throw new ObjectDisposedException(nameof(TestOwner));
        public void Dispose() { var value = Interlocked.Exchange(ref _bytes, null); if (value is not null) { Array.Clear(value); } GC.SuppressFinalize(this); }
    }

    private sealed class FakeRun(CaptureRequest request) : IAudioCaptureRun
    {
        private readonly Channel<OwnedAudioFrame> _frames = Channel.CreateUnbounded<OwnedAudioFrame>();
        private readonly Channel<CaptureStatusEvent> _statuses = Channel.CreateUnbounded<CaptureStatusEvent>();
        public TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CaptureStatusEvent? TerminalStatus { get; private set; }
        public int OwnersCreated { get; private set; }
        public int OwnersDisposed { get; private set; }
        public void Enqueue(OwnedAudioFrame frame) { OwnersCreated++; _frames.Writer.TryWrite(frame); }
        public void SendStatus(CaptureStatusEvent status) => _statuses.Writer.TryWrite(status);
        public void Complete()
        {
            TerminalStatus ??= new CaptureStatusEvent(request.Version, request.Source, request.ClockAnchor.Origin, CaptureState.Stopped);
            _frames.Writer.TryComplete(); _statuses.Writer.TryComplete(); Stopped.TrySetResult();
        }
        public async IAsyncEnumerable<OwnedAudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(ct))
            {
                try { yield return frame; }
                finally { OwnersDisposed++; }
            }
        }
        public IAsyncEnumerable<CaptureStatusEvent> ReadStatusAsync(CancellationToken ct) => _statuses.Reader.ReadAllAsync(ct);
        public Task StopAsync(CaptureStopReason reason, CancellationToken ct) { Complete(); return Task.CompletedTask; }
        public ValueTask DisposeAsync() { while (_frames.Reader.TryRead(out var frame)) { frame.Dispose(); OwnersDisposed++; } Complete(); return ValueTask.CompletedTask; }
    }
}
