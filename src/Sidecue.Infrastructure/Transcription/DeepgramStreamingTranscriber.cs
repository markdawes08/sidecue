using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;

namespace Sidecue.Infrastructure.Transcription;

/// <summary>Runs one Deepgram connection at a time per transcription enumeration.</summary>
public sealed class DeepgramStreamingTranscriber : IStreamingTranscriber
{
    private const int MaximumFrameBytes = 64_000;
    private readonly DeepgramStreamingOptions options;
    private readonly Func<CancellationToken, ValueTask<string?>> getCredential;
    private readonly IDeepgramConnectionFactory connections;

    public DeepgramStreamingTranscriber(DeepgramStreamingOptions options,
        Func<CancellationToken, ValueTask<string?>> getCredential, IDeepgramConnectionFactory? connectionFactory = null)
    {
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.getCredential = getCredential ?? throw new ArgumentNullException(nameof(getCredential));
        connections = connectionFactory ?? new DeepgramWebSocketConnectionFactory();
    }

    public async IAsyncEnumerable<TranscriptEvent> TranscribeAsync(TranscriptionRequest request,
        IAsyncEnumerable<OwnedAudioFrame> frames, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(frames);
        ct.ThrowIfCancellationRequested();
        Uri endpoint = options.CreateEndpoint(request);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var input = new AudioInput(frames.GetAsyncEnumerator(lifetime.Token), request);
        try
        {
            var credential = await ResolveCredentialAsync(lifetime.Token).ConfigureAwait(false);
            for (int attempt = 0; ; attempt++)
            {
                var source = new SourceIdentity(request.Source.SessionId, request.Source.SourceId,
                    new StreamEpoch(checked(request.Source.StreamEpoch.Value + attempt)));
                var epochRequest = new TranscriptionRequest(request.Version, source, request.Language, request.Format,
                    request.ProviderConfiguration, request.StreamStart);
                var normalizer = new DeepgramEventNormalizer(epochRequest);
                if (credential is null)
                {
                    yield return normalizer.RecordStatus(TranscriptionStreamState.Failed, EventErrorCategory.ProviderAuthentication);
                    yield break;
                }

                lifetime.Token.ThrowIfCancellationRequested();
                yield return normalizer.RecordStatus(TranscriptionStreamState.Connecting);
                var opened = await OpenAsync(endpoint, credential, lifetime.Token).ConfigureAwait(false);
                var outcome = new ConnectionOutcome { Failure = opened.Failure };
                if (opened.Connection is { } connection)
                {
                    await foreach (var item in RunConnectionAsync(connection, normalizer, source, input, outcome, lifetime.Token).ConfigureAwait(false))
                        yield return item;
                }

                lifetime.Token.ThrowIfCancellationRequested();
                if (outcome.Failure is null)
                {
                    yield return normalizer.RecordStatus(TranscriptionStreamState.Stopped);
                    yield break;
                }

                var failure = outcome.Failure;
                bool canRetry = failure.Retryable && !input.IsExhausted && attempt < options.MaximumReconnects &&
                    source.StreamEpoch.Value < long.MaxValue;
                if (!canRetry && await input.TakeReadyAsync().ConfigureAwait(false) is { Frame: { } abandoned } pending)
                {
                    var interval = abandoned.Metadata.Interval;
                    abandoned.Dispose();
                    // Report the known interval of a prefetched frame we can no longer send.
                    var start = outcome.Unresolved?.Start ?? pending.Missing?.Start ?? interval.Start;
                    outcome.Unresolved = new TimelineInterval(start, interval.End);
                }
                if (outcome.Unresolved is { } unresolved)
                    yield return normalizer.RecordTransportGap(unresolved, GapReason.TransportDisconnected);
                yield return normalizer.RecordStatus(canRetry ? TranscriptionStreamState.Degraded : TranscriptionStreamState.Failed, failure.Category);
                if (!canRetry) yield break;
                await Task.Delay(options.RetryDelay, lifetime.Token).ConfigureAwait(false);
            }
        }
        finally
        {
            try { await lifetime.CancelAsync().ConfigureAwait(false); }
            finally { await input.DisposeAsync().ConfigureAwait(false); }
        }
    }

    private async IAsyncEnumerable<TranscriptEvent> RunConnectionAsync(IDeepgramConnection connection,
        DeepgramEventNormalizer normalizer, SourceIdentity source, AudioInput input, ConnectionOutcome outcome,
        [EnumeratorCancellation] CancellationToken ct)
    {
        Task<Incoming>? reading = null;
        var keepAlive = new Stopwatch();
        long? drainStarted = null;
        bool metadataReceived = false;
        TimelinePosition? firstSent = null, lastSent = null, lastFinal = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            yield return normalizer.RecordStatus(TranscriptionStreamState.Live);
            keepAlive.Start();
            while (outcome.Failure is null)
            {
                ct.ThrowIfCancellationRequested();
                reading ??= ReceiveAsync(connection, ct);
                if (drainStarted is { } drain)
                {
                    TimeSpan remaining = options.DrainTimeout - Stopwatch.GetElapsedTime(drain);
                    if (remaining <= TimeSpan.Zero || (!reading.IsCompleted && !await ReadyWithinAsync(reading, remaining, ct).ConfigureAwait(false)))
                    {
                        outcome.Failure = Unavailable();
                        break;
                    }
                }
                else if (!reading.IsCompleted)
                {
                    Task<FrameRead> next = input.ReadNext(ct);
                    TimeSpan idle = options.KeepAliveInterval - keepAlive.Elapsed;
                    if (!next.IsCompleted && idle > TimeSpan.Zero)
                    {
                        using var wake = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        var timer = Task.Delay(idle, wake.Token);
                        await Task.WhenAny(reading, next, timer).ConfigureAwait(false);
                        await wake.CancelAsync().ConfigureAwait(false);
                        ct.ThrowIfCancellationRequested();
                    }

                    if (!reading.IsCompleted)
                    {
                        if (!next.IsCompleted)
                        {
                            outcome.Failure = await SendControlAsync(connection, close: false, options.SendTimeout, ct).ConfigureAwait(false);
                            keepAlive.Restart();
                            continue;
                        }

                        FrameRead frameRead = await input.TakeAsync().ConfigureAwait(false);
                        if (frameRead.Error is not null)
                        {
                            outcome.Failure = frameRead.Error;
                            break;
                        }
                        if (frameRead.Frame is null)
                        {
                            drainStarted = Stopwatch.GetTimestamp();
                            outcome.Failure = await SendControlAsync(connection, close: true,
                                Min(options.SendTimeout, options.DrainTimeout), ct).ConfigureAwait(false);
                            if (outcome.Failure is null)
                                yield return normalizer.RecordStatus(TranscriptionStreamState.Draining);
                            continue;
                        }

                        var frame = frameRead.Frame;
                        AudioFrameMetadata frameMetadata = frame.Metadata;
                        var metadata = new AudioFrameMetadata(frameMetadata.Version, source, frameMetadata.Sequence,
                            frameMetadata.Interval, frameMetadata.Format, frameMetadata.SampleCount, frameMetadata.Flags,
                            frameMetadata.DevicePosition, frameMetadata.PerformanceCounter);
                        var sent = await SendFrameAsync(connection, frame, options.SendTimeout, ct).ConfigureAwait(false);
                        keepAlive.Restart();
                        if (sent is null)
                        {
                            firstSent ??= metadata.Interval.Start;
                            lastSent = metadata.Interval.End;
                        }
                        if (frameRead.Missing is { } missing)
                        {
                            TranscriptGap? gap = TryRecordCaptureGap(normalizer, metadata, missing);
                            if (gap is not null) yield return gap;
                            else outcome.Failure = ProtocolFailure();
                        }
                        if (sent is not null)
                        {
                            outcome.Failure ??= sent;
                            yield return normalizer.RecordUncertain(metadata);
                            // A response already received may still describe earlier confirmed audio.
                            if (!reading.IsCompletedSuccessfully || reading.Result.Message is null) break;
                        }
                        else
                        {
                            if (!TryRecordConfirmed(normalizer, metadata)) outcome.Failure = ProtocolFailure();
                            continue;
                        }
                    }
                }

                Incoming incoming = await reading.ConfigureAwait(false);
                reading = null;
                if (incoming.Failure is not null)
                {
                    outcome.Failure ??= incoming.Failure;
                    break;
                }
                if (incoming.Message is null)
                {
                    if (drainStarted is null || !metadataReceived) outcome.Failure ??= Unavailable();
                    break;
                }

                var translated = Translate(normalizer, incoming.Message, drainStarted is not null);
                if (translated.Failure is not null)
                {
                    outcome.Failure ??= translated.Failure;
                    break;
                }
                metadataReceived |= translated.Metadata;
                foreach (var item in translated.Events)
                {
                    if (item is FinalSegment final &&
                        (lastFinal is null || final.Intervals[^1].End.Offset.Value > lastFinal.Offset.Value))
                        lastFinal = final.Intervals[^1].End;
                    yield return item;
                }
            }
        }
        finally
        {
            connection.Abort();
            connection.Dispose();
            if (reading is not null) _ = ObserveAsync(reading);
        }

        var unresolved = lastFinal ?? firstSent;
        if (outcome.Failure is not null && unresolved is not null && lastSent is not null && unresolved.Offset.Value < lastSent.Offset.Value)
            outcome.Unresolved = new TimelineInterval(unresolved, lastSent);
    }

    private async Task<string?> ResolveCredentialAsync(CancellationToken ct)
    {
        Task<string?>? resolving = null;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(options.ConnectTimeout);
            resolving = getCredential(deadline.Token).AsTask();
            string? key = await resolving.WaitAsync(deadline.Token).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(key) ? null : key;
        }
        catch (Exception exception) when (IsOperational(exception))
        {
            if (resolving is not null) _ = ObserveAsync(resolving);
            ct.ThrowIfCancellationRequested();
            return null;
        }
    }

    private async Task<OpenResult> OpenAsync(Uri endpoint, string credential, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(options.ConnectTimeout);
        Task<IDeepgramConnection>? opening = null;
        try
        {
            opening = connections.OpenAsync(endpoint, credential, deadline.Token).AsTask();
            return new(await opening.WaitAsync(deadline.Token).ConfigureAwait(false), null);
        }
        catch (Exception exception) when (IsOperational(exception))
        {
            if (opening is not null) _ = DisposeLateConnectionAsync(opening);
            ct.ThrowIfCancellationRequested();
            return new(null, AsFailure(exception));
        }
    }

    private static async Task<Incoming> ReceiveAsync(IDeepgramConnection connection, CancellationToken ct)
    {
        try { return new(await connection.ReceiveAsync(ct).ConfigureAwait(false), null); }
        catch (Exception exception) when (IsOperational(exception)) { return new(null, AsFailure(exception)); }
    }

    private static async Task<Failure?> SendControlAsync(IDeepgramConnection connection, bool close, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        Task? sending = null;
        try
        {
            sending = close ? connection.SendCloseStreamAsync(deadline.Token).AsTask() : connection.SendKeepAliveAsync(deadline.Token).AsTask();
            await sending.WaitAsync(deadline.Token).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (IsOperational(exception))
        {
            connection.Abort();
            if (sending is not null) _ = ObserveAsync(sending);
            ct.ThrowIfCancellationRequested();
            return AsFailure(exception);
        }
    }

    // Keep the buffer leased until the send finishes even if cancellation wins the wait.
    private static async Task<Failure?> SendFrameAsync(IDeepgramConnection connection, OwnedAudioFrame frame, TimeSpan timeout, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout);
        Task? sending = null;
        try
        {
            sending = connection.SendAudioAsync(frame.Payload, deadline.Token).AsTask();
            await sending.WaitAsync(deadline.Token).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (IsOperational(exception))
        {
            connection.Abort();
            ct.ThrowIfCancellationRequested();
            return AsFailure(exception);
        }
        finally
        {
            if (sending is { IsCompleted: false }) _ = DisposeLateFrameAsync(sending, frame);
            else
            {
                if (sending is not null) _ = sending.Exception;
                frame.Dispose();
            }
        }
    }

    private static Translation Translate(DeepgramEventNormalizer normalizer, string message, bool draining)
    {
        try
        {
            var events = normalizer.Normalize(message);
            if (!draining || events.Count != 0) return new(events, false, null);
            using var json = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 32 });
            return new(events, json.RootElement.GetProperty("type").ValueEquals("Metadata"), null);
        }
        catch (DeepgramProtocolException)
        {
            return new([], false, ProtocolFailure());
        }
    }

    private static TranscriptGap? TryRecordCaptureGap(DeepgramEventNormalizer normalizer, AudioFrameMetadata metadata, TimelineInterval missing)
    {
        var gap = new CaptureGap(metadata.Version, metadata.Source, missing, GapReason.CaptureDiscontinuity, TimingCertainty.Unknown);
        try { return normalizer.RecordGap(gap, new EventId(Guid.NewGuid())); }
        catch (DeepgramProtocolException) { return null; }
    }

    private static bool TryRecordConfirmed(DeepgramEventNormalizer normalizer, AudioFrameMetadata metadata)
    {
        try { normalizer.RecordConfirmed(metadata, TimingCertainty.Exact); return true; }
        catch (InvalidOperationException) { return false; }
    }

    private static async Task<bool> ReadyWithinAsync(Task task, TimeSpan timeout, CancellationToken ct)
    {
        try { await task.WaitAsync(timeout, ct).ConfigureAwait(false); return true; }
        catch (TimeoutException) { return false; }
    }

    private static async Task ObserveAsync(Task operation)
    {
        try { await operation.ConfigureAwait(false); }
        catch (Exception exception) when (IsOperational(exception)) { }
    }

    private static async Task DisposeLateConnectionAsync(Task<IDeepgramConnection> operation)
    {
        try
        {
            using var connection = await operation.ConfigureAwait(false);
            connection.Abort();
        }
        catch (Exception exception) when (IsOperational(exception)) { }
    }

    private static async Task DisposeLateFrameAsync(Task operation, OwnedAudioFrame frame)
    {
        using (frame) await ObserveAsync(operation).ConfigureAwait(false);
    }

    private static Failure Unavailable() => new(EventErrorCategory.ProviderUnavailable, true);
    private static Failure ProtocolFailure() => new(EventErrorCategory.ProtocolConflict, false);
    private static Failure AsFailure(Exception exception) => exception is DeepgramTransportException transport
        ? new(transport.Category, transport.Retryable) : Unavailable();
    private static bool IsOperational(Exception exception) =>
        exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException);
    private static TimeSpan Min(TimeSpan first, TimeSpan second) => first < second ? first : second;

    private sealed class ConnectionOutcome
    {
        public Failure? Failure { get; set; }
        public TimelineInterval? Unresolved { get; set; }
    }
    private sealed record Failure(EventErrorCategory Category, bool Retryable);
    private sealed record Incoming(string? Message, Failure? Failure);
    private sealed record OpenResult(IDeepgramConnection? Connection, Failure? Failure);
    private sealed record Translation(IReadOnlyList<TranscriptEvent> Events, bool Metadata, Failure? Failure);
    private sealed record FrameRead(OwnedAudioFrame? Frame, TimelineInterval? Missing = null, Failure? Error = null);

    private sealed class AudioInput(IAsyncEnumerator<OwnedAudioFrame> pipe, TranscriptionRequest request) : IAsyncDisposable
    {
        private Task<FrameRead>? pending;
        private AudioFrameMetadata? previous;
        private bool exhausted;
        public bool IsExhausted => exhausted || pending is { IsCompletedSuccessfully: true } && pending.Result.Frame is null;

        public Task<FrameRead> ReadNext(CancellationToken ct) => pending ??= ReadAsync(ct);

        public async Task<FrameRead> TakeAsync()
        {
            var read = await pending!.ConfigureAwait(false);
            pending = null;
            exhausted = read.Frame is null;
            return read;
        }

        public async Task<FrameRead?> TakeReadyAsync() => pending is { IsCompletedSuccessfully: true }
            ? await TakeAsync().ConfigureAwait(false) : null;

        private async Task<FrameRead> ReadAsync(CancellationToken ct)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!await pipe.MoveNextAsync().ConfigureAwait(false)) return new(null);
                var frame = pipe.Current;
                var metadata = frame.Metadata;
                var priorEnd = previous?.Interval.End ?? request.StreamStart;
                if (metadata.Source != request.Source || metadata.Format != request.Format ||
                    metadata.Interval.TimelineEpoch != request.StreamStart.TimelineEpoch ||
                    metadata.Interval.Start.Offset.Value < priorEnd.Offset.Value || metadata.PayloadByteCount > MaximumFrameBytes ||
                    (previous is not null && metadata.Sequence.Value <= previous.Sequence.Value))
                {
                    frame.Dispose();
                    return new(null, Error: new(EventErrorCategory.ProtocolConflict, false));
                }

                bool skipped = previous is not null && metadata.Sequence.Value - previous.Sequence.Value > 1;
                TimelineInterval? missing = metadata.Interval.Start.Offset.Value > priorEnd.Offset.Value || skipped ||
                    (metadata.Flags & AudioFrameAttributes.Discontinuity) != 0
                    ? new TimelineInterval(priorEnd, metadata.Interval.Start) : null;
                previous = metadata;
                return new(frame, missing);
            }
            catch (Exception exception) when (IsOperational(exception))
            {
                return new(null, Error: new(EventErrorCategory.Unknown, false));
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (pending is { IsCompleted: false })
            {
                // Async iterators cannot be disposed during MoveNext. A late transfer
                // remains ours to dispose, even if an upstream source ignores cancellation.
                _ = ObserveAsync(DisposeInputAsync(pending));
                return;
            }
            await DisposeInputAsync(pending).ConfigureAwait(false);
        }

        private async Task DisposeInputAsync(Task<FrameRead>? read)
        {
            try { if (read is not null) (await read.ConfigureAwait(false)).Frame?.Dispose(); }
            finally { await pipe.DisposeAsync().ConfigureAwait(false); }
        }
    }
}
