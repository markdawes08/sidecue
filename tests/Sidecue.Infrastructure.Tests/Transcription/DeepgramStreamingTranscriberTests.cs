using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sidecue.Core.Contracts;
using Sidecue.Infrastructure.Transcription;
using Xunit;

namespace Sidecue.Infrastructure.Tests.Transcription;

public sealed class DeepgramStreamingTranscriberTests
{
    private const string ConfigurationVersion = "synthetic-config-v1";
    private const string SyntheticCredential = "synthetic-test-token";
    private const string Metadata = """{"type":"Metadata"}""";
    private const string Final = """{"type":"Results","channel_index":[0,1],"start":0,"duration":1,"is_final":true,"channel":{"alternatives":[{"transcript":"Synthetic final"}]}}""";
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);
    private static readonly AudioFormat Format = new(16_000, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian);
    private static readonly SessionId Session = new(Guid.Parse("10000000-0000-0000-0000-000000000001"));
    private static readonly SourceId Microphone = new(Guid.Parse("20000000-0000-0000-0000-000000000001"));
    private static readonly SourceId Playback = new(Guid.Parse("20000000-0000-0000-0000-000000000002"));
    private static readonly TimelineEpochId Timeline = new(Guid.Parse("30000000-0000-0000-0000-000000000001"));

    [Fact]
    public async Task SeparateSourcesUseSeparateSocketsAndKeepFinalsThroughDrain()
    {
        var ct = TestContext.Current.CancellationToken;
        var first = new FakeConnection();
        var second = new FakeConnection();
        var sockets = new FakeFactory(first, second);
        var transcriber = CreateTranscriber(sockets);
        var micRequest = Request(Microphone);
        var playbackRequest = Request(Playback, start: 2_000_000);
        var micFrames = new FrameSource(micRequest, [new(0, 17)]);
        var playbackFrames = new FrameSource(playbackRequest, [new(2_000_000, 34)]);

        var mic = CollectAsync(transcriber.TranscribeAsync(micRequest, micFrames.ReadAsync(ct), ct), ct);
        var playback = CollectAsync(transcriber.TranscribeAsync(playbackRequest, playbackFrames.ReadAsync(ct), ct), ct);
        var results = await Task.WhenAll(mic, playback).WaitAsync(Watchdog, ct);

        var micFinal = Assert.Single(results[0].OfType<FinalSegment>());
        var playbackFinal = Assert.Single(results[1].OfType<FinalSegment>());
        Assert.Equal(micRequest.Source, micFinal.Header.Source);
        Assert.Equal(playbackRequest.Source, playbackFinal.Header.Source);
        Assert.Equal(0, Assert.Single(micFinal.Intervals).Start.Offset.Value);
        Assert.Equal(2_000_000, Assert.Single(playbackFinal.Intervals).Start.Offset.Value);
        Assert.NotEqual(micFinal.SegmentId, playbackFinal.SegmentId);
        Assert.All(results, events =>
        {
            Assert.Contains(events.OfType<StreamStatus>(), status => status.State == TranscriptionStreamState.Draining);
            Assert.Equal(TranscriptionStreamState.Stopped, Assert.IsType<StreamStatus>(events[^1]).State);
        });
        Assert.Equal(new byte[] { 17, 34 }, new[] { Assert.Single(first.Sent), Assert.Single(second.Sent) }.Order());
        Assert.Equal(1, first.CloseCalls);
        Assert.Equal(1, second.CloseCalls);
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
        Assert.All(sockets.OpenRequests, uri =>
        {
            Assert.Equal("wss", uri.Scheme);
            Assert.Equal("api.deepgram.com", uri.Host);
            Assert.Equal("/v1/listen", uri.AbsolutePath);
            Assert.Contains("encoding=linear16", uri.Query, StringComparison.Ordinal);
            Assert.Contains("sample_rate=16000", uri.Query, StringComparison.Ordinal);
            Assert.Contains("endpointing=300", uri.Query, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticCredential, uri.AbsoluteUri, StringComparison.Ordinal);
        });
        micFrames.AssertReleased();
        playbackFrames.AssertReleased();
    }

    [Fact]
    public async Task ReconnectStartsANewEpochAndMapsZeroBackToTheNextCapturedFrame()
    {
        var ct = TestContext.Current.CancellationToken;
        var nextFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeConnection();
        first.OnAudio = (_, _) =>
        {
            first.Complete();
            return ValueTask.CompletedTask;
        };
        var second = new FakeConnection();
        var sockets = new FakeFactory(first, second);
        var request = Request(Microphone, epoch: 4);
        var frames = new FrameSource(request, [new(0, 17), new(4_000_000, 34)], nextFrame.Task);
        var transcriber = CreateTranscriber(sockets, maximumReconnects: 1);

        var events = await CollectAsync(transcriber.TranscribeAsync(request, frames.ReadAsync(ct), ct), ct, item =>
        {
            if (item is StreamStatus { State: TranscriptionStreamState.Degraded })
                nextFrame.TrySetResult();
        }).WaitAsync(Watchdog, ct);

        Assert.Contains(events.OfType<StreamStatus>(), status => status.State == TranscriptionStreamState.Degraded &&
            status.Error == EventErrorCategory.ProviderUnavailable);
        var interrupted = Assert.Single(events.OfType<TranscriptGap>(), gap => gap.Reason == GapReason.TransportDisconnected);
        Assert.Equal(request.Source, interrupted.Header.Source);
        Assert.Equal(0, interrupted.Interval.Start.Offset.Value);
        Assert.Equal(1_000_000, interrupted.Interval.End.Offset.Value);
        var final = Assert.Single(events.OfType<FinalSegment>());
        Assert.Equal(request.Source.SourceId, final.Header.Source.SourceId);
        Assert.Equal(5, final.Header.Source.StreamEpoch.Value);
        Assert.Equal(4_000_000, Assert.Single(final.Intervals).Start.Offset.Value);
        Assert.Equal((byte)17, Assert.Single(first.Sent));
        Assert.Equal((byte)34, Assert.Single(second.Sent));
        Assert.Equal(2, sockets.OpenRequests.Count);
        frames.AssertReleased();
    }

    [Fact]
    public async Task SendTimeoutReportsUncertainAudioWithoutReplayingIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var nextFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = new FakeConnection();
        first.OnAudio = (_, cancellationToken) => new ValueTask(first.Aborted.WaitAsync(cancellationToken));
        var second = new FakeConnection();
        var sockets = new FakeFactory(first, second);
        var request = Request(Microphone);
        var frames = new FrameSource(request, [new(0, 17), new(1_000_000, 34)], nextFrame.Task);
        var transcriber = CreateTranscriber(sockets, maximumReconnects: 1, sendTimeout: TimeSpan.FromMilliseconds(100));

        var events = await CollectAsync(transcriber.TranscribeAsync(request, frames.ReadAsync(ct), ct), ct, item =>
        {
            if (item is StreamStatus { State: TranscriptionStreamState.Degraded })
                nextFrame.TrySetResult();
        }).WaitAsync(Watchdog, ct);

        var uncertain = Assert.Single(events.OfType<TranscriptGap>(), gap => gap.Reason == GapReason.UnknownSendCompletion);
        Assert.Equal(request.Source, uncertain.Header.Source);
        Assert.Equal(TimingCertainty.Unknown, uncertain.Certainty);
        Assert.Equal(0, uncertain.Interval.Start.Offset.Value);
        Assert.Equal(1_000_000, uncertain.Interval.End.Offset.Value);
        Assert.Equal((byte)17, Assert.Single(first.Sent));
        Assert.Equal((byte)34, Assert.Single(second.Sent));
        Assert.Equal(1, Assert.Single(events.OfType<FinalSegment>()).Header.Source.StreamEpoch.Value);
        await frames.AwaitDisposalAsync(ct);
        frames.AssertReleased();
    }

    [Fact]
    public async Task AuthenticationFailureIsTerminalAndDoesNotExposeTheCredential()
    {
        var ct = TestContext.Current.CancellationToken;
        var sockets = new FakeFactory(new DeepgramTransportException(EventErrorCategory.ProviderAuthentication, false, HttpStatusCode.Unauthorized));
        var request = Request(Microphone);
        var frames = new FrameSource(request, [new(0, 17)]);
        var transcriber = CreateTranscriber(sockets, maximumReconnects: 2);

        var events = await CollectAsync(transcriber.TranscribeAsync(request, frames.ReadAsync(ct), ct), ct).WaitAsync(Watchdog, ct);

        Assert.Single(sockets.OpenRequests);
        var failed = Assert.Single(events.OfType<StreamStatus>(), status => status.State == TranscriptionStreamState.Failed);
        Assert.Equal(EventErrorCategory.ProviderAuthentication, failed.Error);
        Assert.All(events, item => Assert.DoesNotContain(SyntheticCredential, item.ToString(), StringComparison.Ordinal));
        Assert.Empty(events.OfType<FinalSegment>());
        frames.AssertReleased();
    }

    [Fact]
    public async Task TransientOpenFailuresStopAtConfiguredRetryLimit()
    {
        var ct = TestContext.Current.CancellationToken;
        var sockets = new FakeFactory(new DeepgramTransportException(EventErrorCategory.ProviderRateLimit, true),
            new DeepgramTransportException(EventErrorCategory.ProviderRateLimit, true));
        var request = Request(Microphone);
        var frames = new FrameSource(request, [new(0, 17)]);
        var transcriber = CreateTranscriber(sockets, maximumReconnects: 1);

        var events = await CollectAsync(transcriber.TranscribeAsync(request, frames.ReadAsync(ct), ct), ct).WaitAsync(Watchdog, ct);

        Assert.Equal(2, sockets.OpenRequests.Count);
        Assert.Contains(events.OfType<StreamStatus>(), status => status.State == TranscriptionStreamState.Degraded &&
            status.Error == EventErrorCategory.ProviderRateLimit);
        var terminal = Assert.IsType<StreamStatus>(events[^1]);
        Assert.Equal(TranscriptionStreamState.Failed, terminal.State);
        Assert.Equal(EventErrorCategory.ProviderRateLimit, terminal.Error);
        frames.AssertReleased();
    }

    [Fact]
    public async Task ProductionFactoryRefusesOtherHostsAndInvalidCredentials()
    {
        var ct = TestContext.Current.CancellationToken;
        var factory = new DeepgramWebSocketConnectionFactory();
        var foreign = await Assert.ThrowsAsync<DeepgramTransportException>(async () =>
        {
            using var unexpected = await factory.OpenAsync(new Uri("wss://other.invalid/v1/listen"), SyntheticCredential, ct);
        });
        Assert.Equal(EventErrorCategory.UnsupportedCapability, foreign.Category);
        Assert.False(foreign.Retryable);

        var invalidKey = await Assert.ThrowsAsync<DeepgramTransportException>(async () =>
        {
            using var unexpected = await factory.OpenAsync(new Uri("wss://api.deepgram.com/v1/listen"), SyntheticCredential + "\r\n", ct);
        });
        Assert.Equal(EventErrorCategory.ProviderAuthentication, invalidKey.Category);
        Assert.False(invalidKey.Retryable);
        Assert.DoesNotContain(SyntheticCredential, foreign.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticCredential, invalidKey.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task MalformedResultFailsVisiblyWithoutExposingProviderContentOrRetrying()
    {
        var ct = TestContext.Current.CancellationToken;
        const string privateMarker = "Synthetic private marker";
        var connection = new FakeConnection { OnClose = socket => socket.Enqueue("{\"type\":\"Results\",\"transcript\":\"" + privateMarker + "\"}") };
        var sockets = new FakeFactory(connection);
        var request = Request(Microphone);
        var frames = new FrameSource(request, [new(0, 17)]);
        var transcriber = CreateTranscriber(sockets, maximumReconnects: 2);

        var events = await CollectAsync(transcriber.TranscribeAsync(request, frames.ReadAsync(ct), ct), ct).WaitAsync(Watchdog, ct);

        Assert.Single(sockets.OpenRequests);
        var failed = Assert.Single(events.OfType<StreamStatus>(), status => status.State == TranscriptionStreamState.Failed);
        Assert.Equal(EventErrorCategory.ProtocolConflict, failed.Error);
        Assert.All(events, item => Assert.DoesNotContain(privateMarker, item.ToString(), StringComparison.Ordinal));
        Assert.Empty(events.OfType<FinalSegment>());
        frames.AssertReleased();
    }

    [Fact]
    public async Task StalledDrainReportsFailureAndReleasesTheSocket()
    {
        var ct = TestContext.Current.CancellationToken;
        var connection = new FakeConnection { OnClose = _ => { } };
        var sockets = new FakeFactory(connection);
        var request = Request(Microphone);
        var frames = new FrameSource(request, [new(0, 17)]);
        var transcriber = CreateTranscriber(sockets, drainTimeout: TimeSpan.FromMilliseconds(100));

        var events = await CollectAsync(transcriber.TranscribeAsync(request, frames.ReadAsync(ct), ct), ct).WaitAsync(Watchdog, ct);

        Assert.Equal(1, connection.CloseCalls);
        Assert.True(connection.Disposed);
        Assert.Contains(events.OfType<StreamStatus>(), status => status.State == TranscriptionStreamState.Draining);
        Assert.Contains(events.OfType<StreamStatus>(), status => status.State == TranscriptionStreamState.Failed);
        Assert.DoesNotContain(events.OfType<StreamStatus>(), status => status.State == TranscriptionStreamState.Stopped);
        frames.AssertReleased();
    }

    [Fact]
    public async Task CancellationStopsUploadsAndReleasesTransferredFramesAfterSendSettles()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var request = Request(Microphone);
        var frames = new FrameSource(request, Enumerable.Range(0, 32).Select(index => new Sample(index * 1_000_000L, (byte)index)).ToArray());
        var sending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sendSawLiveOwner = false;
        var connection = new FakeConnection();
        connection.OnAudio = async (_, cancellationToken) =>
        {
            sending.TrySetResult();
            try { await connection.Aborted.WaitAsync(cancellationToken); }
            finally { sendSawLiveOwner = frames.Owners.First().DisposeCount == 0; }
        };
        var sockets = new FakeFactory(connection);
        var transcriber = CreateTranscriber(sockets);

        var collecting = CollectUntilCancelledAsync(transcriber.TranscribeAsync(request, frames.ReadAsync(cancelled.Token), cancelled.Token), cancelled.Token);
        try
        {
            await sending.Task.WaitAsync(Watchdog, ct);
            Assert.InRange(frames.Owners.Count, 1, 2);
        }
        finally
        {
            await cancelled.CancelAsync();
        }
        await collecting.WaitAsync(Watchdog, ct);
        await frames.AwaitDisposalAsync(ct);

        Assert.True(sendSawLiveOwner);
        Assert.Single(connection.Sent);
        Assert.True(connection.Disposed);
        frames.AssertReleased();
    }

    private static DeepgramStreamingTranscriber CreateTranscriber(FakeFactory sockets, int maximumReconnects = 0,
        TimeSpan? sendTimeout = null, TimeSpan? drainTimeout = null) =>
        new(new DeepgramStreamingOptions(ConfigurationVersion, "nova-3", "2026-01-27.9249",
            maximumReconnects: maximumReconnects, sendTimeout: sendTimeout, drainTimeout: drainTimeout,
            retryDelay: TimeSpan.FromMilliseconds(1)), _ => ValueTask.FromResult<string?>(SyntheticCredential), sockets);

    private static TranscriptionRequest Request(SourceId source, long epoch = 0, long start = 0) =>
        new(ContractVersion.Current, new SourceIdentity(Session, source, new StreamEpoch(epoch)), "en-US", Format,
            new ProviderConfigurationReference("deepgram", ConfigurationVersion), Position(start));

    private static TimelinePosition Position(long offset) => new(Timeline, new SessionOffset(offset));

    private static async Task<List<TranscriptEvent>> CollectAsync(IAsyncEnumerable<TranscriptEvent> stream, CancellationToken ct,
        Action<TranscriptEvent>? onEvent = null)
    {
        var events = new List<TranscriptEvent>();
        await foreach (var item in stream.WithCancellation(ct))
        {
            events.Add(item);
            onEvent?.Invoke(item);
        }
        return events;
    }

    private static async Task CollectUntilCancelledAsync(IAsyncEnumerable<TranscriptEvent> stream, CancellationToken ct)
    {
        try { await CollectAsync(stream, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private sealed class FakeFactory(params object[] opens) : IDeepgramConnectionFactory
    {
        private readonly ConcurrentQueue<object> _pending = new(opens);

        public ConcurrentQueue<Uri> OpenRequests { get; } = new();

        public ValueTask<IDeepgramConnection> OpenAsync(Uri endpoint, string apiKey, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OpenRequests.Enqueue(endpoint);
            if (!_pending.TryDequeue(out var next))
                throw new InvalidOperationException("Unexpected additional synthetic connection.");
            return next is Exception error ? ValueTask.FromException<IDeepgramConnection>(error) : ValueTask.FromResult((IDeepgramConnection)next);
        }
    }

    private sealed class FakeConnection : IDeepgramConnection
    {
        private readonly Channel<string> _incoming = Channel.CreateUnbounded<string>();
        private readonly TaskCompletionSource _abort = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _closeCalls;
        private int _disposed;

        public ConcurrentQueue<byte> Sent { get; } = new();
        public Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask>? OnAudio { get; set; }
        public Action<FakeConnection> OnClose { get; init; } = socket =>
        {
            socket.Enqueue(Final);
            socket.Enqueue(Metadata);
            socket.Complete();
        };
        public int CloseCalls => Volatile.Read(ref _closeCalls);
        public bool Disposed => Volatile.Read(ref _disposed) != 0;
        public Task Aborted => _abort.Task;

        public void Enqueue(string message) => _incoming.Writer.TryWrite(message);
        public void Complete() => _incoming.Writer.TryComplete();

        public ValueTask SendAudioAsync(ReadOnlyMemory<byte> audio, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Sent.Enqueue(audio.Span[0]);
            return OnAudio?.Invoke(audio, cancellationToken) ?? ValueTask.CompletedTask;
        }

        public ValueTask SendCloseStreamAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _closeCalls);
            OnClose(this);
            return ValueTask.CompletedTask;
        }

        public ValueTask SendKeepAliveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public async ValueTask<string?> ReceiveAsync(CancellationToken cancellationToken)
        {
            try { return await _incoming.Reader.ReadAsync(cancellationToken); }
            catch (ChannelClosedException) { return null; }
        }

        public void Abort()
        {
            _abort.TrySetResult();
            Complete();
        }

        public void Dispose()
        {
            Interlocked.Exchange(ref _disposed, 1);
            Abort();
        }
    }

    private sealed record Sample(long Start, byte Marker);

    private sealed class FrameSource(TranscriptionRequest request, Sample[] samples, Task? beforeSecond = null)
    {
        public ConcurrentQueue<ProbeOwner> Owners { get; } = new();

        public async IAsyncEnumerable<OwnedAudioFrame> ReadAsync([EnumeratorCancellation] CancellationToken ct)
        {
            for (int i = 0; i < samples.Length; i++)
            {
                if (i == 1 && beforeSecond is not null) await beforeSecond.WaitAsync(ct);
                ct.ThrowIfCancellationRequested();
                var sample = samples[i];
                var interval = new TimelineInterval(Position(sample.Start), Position(sample.Start + 1_000_000));
                var metadata = new AudioFrameMetadata(ContractVersion.Current, request.Source, new FrameSequence(i), interval, Format, 16_000);
                var owner = new ProbeOwner(metadata.PayloadByteCount);
                owner.Memory.Span[0] = sample.Marker;
                Owners.Enqueue(owner);
                yield return new OwnedAudioFrame(metadata, owner, metadata.PayloadByteCount);
            }
        }

        public void AssertReleased() => Assert.All(Owners, owner => Assert.Equal(1, owner.DisposeCount));

        public Task AwaitDisposalAsync(CancellationToken ct) => Task.WhenAll(Owners.Select(owner => owner.Disposed)).WaitAsync(Watchdog, ct);
    }

    private sealed class ProbeOwner(int length) : IMemoryOwner<byte>
    {
        private int _disposeCount;
        private readonly TaskCompletionSource _disposed = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Memory<byte> Memory { get; } = new byte[length];
        public int DisposeCount => Volatile.Read(ref _disposeCount);
        public Task Disposed => _disposed.Task;

        public void Dispose()
        {
            Interlocked.Increment(ref _disposeCount);
            _disposed.TrySetResult();
        }
    }
}
