using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;
using Sidecue.Windows.Audio;
using Xunit;

namespace Sidecue.Windows.Tests.Audio;

public sealed class QpcSessionClockTests
{
    [Fact]
    public void PacketMappingUsesFirstSampleQpcAndSampleDuration()
    {
        var clock = new FakeCorrelatedClock();
        var mapped = QpcPacketMapper.TryMap(clock.Anchor, clock.QpcAtOrigin, clock.GetPosition(),
            110_000_000, 100_000, AudioFrameAttributes.None, null, out var interval);

        Assert.True(mapped);
        Assert.NotNull(interval);
        Assert.Equal(2_000_000, interval.Start.Offset.Value);
        Assert.Equal(2_100_000, interval.End.Offset.Value);
        Assert.Equal(clock.Anchor.Origin.TimelineEpoch, interval.TimelineEpoch);
    }

    [Fact]
    public void MappingRejectsUntrustedOutOfOrderAndFutureTimestamps()
    {
        var clock = new FakeCorrelatedClock();
        var anchor = clock.Anchor;
        var now = clock.GetPosition();
        Assert.False(QpcPacketMapper.TryMap(anchor, null, now, 110_000_000, 100_000, AudioFrameAttributes.None, null, out _));
        Assert.False(QpcPacketMapper.TryMap(anchor, new RawClockPosition(100_000_000, 1), now,
            110_000_000, 100_000, AudioFrameAttributes.None, null, out _));
        Assert.False(QpcPacketMapper.TryMap(anchor, clock.QpcAtOrigin, now,
            110_000_000, 100_000, AudioFrameAttributes.TimestampError, null, out _));
        Assert.False(QpcPacketMapper.TryMap(anchor, clock.QpcAtOrigin, now,
            99_999_999, 100_000, AudioFrameAttributes.None, null, out _));
        Assert.False(QpcPacketMapper.TryMap(anchor, clock.QpcAtOrigin, now,
            110_000_000, 100_000, AudioFrameAttributes.None, 110_000_000, out _));
        Assert.False(QpcPacketMapper.TryMap(anchor, clock.QpcAtOrigin, now,
            ulong.MaxValue, 100_000, AudioFrameAttributes.None, null, out _));
        Assert.False(QpcPacketMapper.TryMap(anchor, clock.QpcAtOrigin, now,
            139_999_990, 100_000, AudioFrameAttributes.None, null, out _));
        var wrongEpoch = new TimelinePosition(new TimelineEpochId(Guid.NewGuid()), now.Offset);
        Assert.False(QpcPacketMapper.TryMap(anchor, clock.QpcAtOrigin, wrongEpoch,
            110_000_000, 100_000, AudioFrameAttributes.None, null, out _));
    }

    [Fact]
    public void CorrelationAllowsOnlySmallEndpointRoundingTolerance()
    {
        var clock = new FakeCorrelatedClock();
        var now = clock.GetPosition();
        // The packet starts 100 ms before now; two microseconds can arise
        // from independent endpoint rounding, but more must fall back.
        const ulong firstSample = 139_000_000;
        Assert.True(QpcPacketMapper.TryMap(clock.Anchor, clock.QpcAtOrigin, now,
            firstSample, 100_002, AudioFrameAttributes.None, null, out _));
        Assert.False(QpcPacketMapper.TryMap(clock.Anchor, clock.QpcAtOrigin, now,
            firstSample, 100_003, AudioFrameAttributes.None, null, out _));
    }

    [Fact]
    public void NativeClockExposesAStableQpcOriginAndNondecreasingSessionTime()
    {
        var clock = new QpcSessionClock(new SessionId(Guid.NewGuid()), new TimelineEpochId(Guid.NewGuid()));
        var first = clock.GetPosition();
        var second = clock.GetPosition();

        Assert.Equal(10_000_000, clock.QpcAtOrigin.TicksPerSecond);
        Assert.Equal(clock.Anchor.Origin.TimelineEpoch, second.TimelineEpoch);
        Assert.True(second.Offset.Value >= first.Offset.Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceMapsOnlyTrustedQpcAndPreservesNativeTimestampErrors(bool nativeTimestampError)
    {
        var clock = new FakeCorrelatedClock();
        var nativeFlags = nativeTimestampError ? AudioFrameAttributes.TimestampError : AudioFrameAttributes.None;
        var frame = await CaptureAsync(clock, nativeFlags, 110_000_000);

        Assert.Equal(nativeTimestampError ? 4_900_000 : 2_000_000, frame.Interval.Start.Offset.Value);
        Assert.Equal(nativeTimestampError ? 5_000_000 : 2_100_000, frame.Interval.End.Offset.Value);
        Assert.True(frame.Flags.HasFlag(AudioFrameAttributes.SessionTimestampEstimated));
        Assert.Equal(nativeTimestampError, frame.Flags.HasFlag(AudioFrameAttributes.TimestampError));
        Assert.Equal(110_000_000, frame.PerformanceCounter?.Ticks);
    }

    [Fact]
    public async Task ArbitrarySessionClockFallsBackWithoutInventingNativeTimestampError()
    {
        var frame = await CaptureAsync(new FakeUncorrelatedClock(), AudioFrameAttributes.None, 110_000_000);

        Assert.Equal(4_900_000, frame.Interval.Start.Offset.Value);
        Assert.True(frame.Flags.HasFlag(AudioFrameAttributes.SessionTimestampEstimated));
        Assert.False(frame.Flags.HasFlag(AudioFrameAttributes.TimestampError));
    }

    private static async Task<AudioFrameMetadata> CaptureAsync(ISessionClock clock, AudioFrameAttributes flags, ulong qpc)
    {
        var backend = new FakeBackend();
        var source = new WasapiCaptureSource(clock, new FakeFactory(backend));
        var request = new CaptureRequest(ContractVersion.Current,
            new SourceIdentity(clock.Anchor.SessionId, new SourceId(Guid.NewGuid()), new StreamEpoch(0)),
            clock.Anchor, new CaptureSourceSelection(CaptureSourceKind.Microphone, "synthetic"),
            new AudioBufferPolicy(2_000_000, 100_000));
        var token = TestContext.Current.CancellationToken;
        await using var run = await source.OpenAsync(request, token);
        backend.Deliver(new byte[3_200], 1_600, flags, qpc);
        await using var frames = run.ReadFramesAsync(token).GetAsyncEnumerator(token);
        Assert.True(await frames.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), token));
        using var frame = frames.Current;
        return frame.Metadata;
    }

    private class FakeUncorrelatedClock : ISessionClock
    {
        public SessionClockAnchor Anchor { get; } = new(new SessionId(Guid.NewGuid()),
            new TimelinePosition(new TimelineEpochId(Guid.NewGuid()), new SessionOffset(1_000_000)),
            new UtcTimestamp(DateTimeOffset.UnixEpoch));
        public TimelinePosition GetPosition() => new(Anchor.Origin.TimelineEpoch, new SessionOffset(5_000_000));
        public UtcTimestamp GetUtcNow() => Anchor.UtcAtOrigin;
    }

    private sealed class FakeCorrelatedClock : FakeUncorrelatedClock, IQpcSessionClock
    {
        public RawClockPosition QpcAtOrigin { get; } = new(100_000_000, 10_000_000);
    }

    private sealed class FakeFactory(FakeBackend backend) : IAudioCaptureBackendFactory
    {
        public IAudioCaptureBackend Open(CaptureSourceSelection selection) => backend;
    }

    private sealed class FakeBackend : IAudioCaptureBackend
    {
        private CapturePacketCallback? _packet;
        private Action<Exception?>? _stopped;
        public AudioFormat Format { get; } = new(16_000, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian);
        public void Start(CapturePacketCallback packet, Action<Exception?> stopped) { _packet = packet; _stopped = stopped; }
        public void RequestStop() => _stopped?.Invoke(null);
        public void Dispose() => GC.SuppressFinalize(this);
        public void Deliver(byte[] data, int count, AudioFrameAttributes flags, ulong qpc) =>
            (_packet ?? throw new InvalidOperationException())(data, count, flags, 0, qpc);
    }
}
