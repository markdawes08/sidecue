using System.Buffers;
using System.Buffers.Text;
using System.Runtime.CompilerServices;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;
using Sidecue.Infrastructure.Transcription;

namespace Sidecue.DeepgramProbe;

internal static class Program
{
    private const int SampleRate = 16_000;
    private const int BytesPerSample = 2;
    private const int SamplesPerFrame = SampleRate / 10;
    private const int MaximumSourceBytes = 8 * SampleRate * BytesPerSample;
    private const int MaximumEncodedLineBytes = ((MaximumSourceBytes + 2) / 3) * 4;
    private const int MaximumInputBytes = 2 * (MaximumEncodedLineBytes + 2);
    private const string ConfigurationVersion = "sc-009c-smoke-v1";
    private const string Language = "en-US";
    private const int EndpointingMilliseconds = 300;
    private static readonly AudioFormat Format =
        new(SampleRate, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian);

    private static async Task<int> Main(string[] args)
    {
        if (args.Length != 1 || args[0] is not ("--preflight" or "--live"))
        {
            Console.Error.WriteLine("Choose --preflight or --live via tools/probe-deepgram.ps1.");
            return 2;
        }

        bool live = args[0] == "--live";
        var config = live ? ReadConfiguration() : null;
        if (live && config is null) return 2;

        if (!Console.IsInputRedirected)
        {
            Console.Error.WriteLine("The smoke probe expects two generated speech buffers on redirected input.");
            return 2;
        }

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(35));
        ConsoleCancelEventHandler cancel = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            deadline.Cancel();
        };
        Console.CancelKeyPress += cancel;
        try
        {
            using Stream standardInput = Console.OpenStandardInput();
            var buffers = await ReadInputAsync(standardInput, deadline.Token).ConfigureAwait(false);
            if (buffers is not { } input)
            {
                Console.Error.WriteLine("Expected two distinct, non-silent 16 kHz mono PCM16 buffers of at most eight seconds each.");
                return 1;
            }

            var session = new SessionId(Guid.NewGuid());
            var timeline = new TimelineEpochId(Guid.NewGuid());
            var microphone = Request(session, timeline, startMicroseconds: 0);
            var remote = Request(session, timeline, startMicroseconds: 400_000);
            Console.WriteLine(FormattableString.Invariant($"mode={(live ? "live" : "preflight")} configuration={ConfigurationVersion} language={Language} format=pcm16-le/16000/mono endpointingMs={EndpointingMilliseconds}"));

            if (config is null)
            {
                bool microphoneValid = await PreflightAsync("microphone", microphone, input.Microphone, deadline.Token).ConfigureAwait(false);
                bool remoteValid = await PreflightAsync("remote", remote, input.Remote, deadline.Token).ConfigureAwait(false);
                return microphoneValid && remoteValid ? 0 : 1;
            }

            Console.WriteLine($"provider=deepgram requestedModel={config.Options.Model} requestedVersion={config.Options.ModelVersion}");
            IStreamingTranscriber transcriber = new DeepgramStreamingTranscriber(
                config.Options, _ => ValueTask.FromResult<string?>(config.Key));
            var results = await Task.WhenAll(
                ObserveAsync(transcriber, "microphone", microphone, input.Microphone, deadline.Token),
                ObserveAsync(transcriber, "remote", remote, input.Remote, deadline.Token)).ConfigureAwait(false);
            return results.All(static success => success) ? 0 : 1;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("The Deepgram smoke probe timed out or was cancelled.");
            return 1;
        }
        catch (Exception exception) when (exception is not (OutOfMemoryException or StackOverflowException or AccessViolationException))
        {
            Console.Error.WriteLine("The Deepgram smoke probe could not complete.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= cancel;
        }
    }

    private static LiveConfiguration? ReadConfiguration()
    {
        string? key = Environment.GetEnvironmentVariable("SIDECUE_DEEPGRAM_TEST_KEY");
        string? model = Environment.GetEnvironmentVariable("SIDECUE_DEEPGRAM_MODEL");
        string? version = Environment.GetEnvironmentVariable("SIDECUE_DEEPGRAM_MODEL_VERSION");
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(version))
        {
            Console.Error.WriteLine("Live mode requires SIDECUE_DEEPGRAM_TEST_KEY, SIDECUE_DEEPGRAM_MODEL and SIDECUE_DEEPGRAM_MODEL_VERSION.");
            return null;
        }

        if (key.Length > 4096 || !key.All(static character => character is > ' ' and <= '~') ||
            model.Contains(key, StringComparison.Ordinal) || version.Contains(key, StringComparison.Ordinal))
        {
            Console.Error.WriteLine("The live Deepgram credential or model settings are invalid.");
            return null;
        }

        try
        {
            return new LiveConfiguration(key, new DeepgramStreamingOptions(
                ConfigurationVersion, model, version, EndpointingMilliseconds, maximumReconnects: 0));
        }
        catch (ArgumentException)
        {
            Console.Error.WriteLine("Live mode requires valid model settings and a specific model version.");
            return null;
        }
    }

    private static async Task<(byte[] Microphone, byte[] Remote)?> ReadInputAsync(Stream input, CancellationToken cancellationToken)
    {
        // Read one extra byte so even an input stream without newlines has a hard limit.
        byte[] encoded = new byte[MaximumInputBytes + 1];
        int length = 0;
        while (length < encoded.Length)
        {
            int count = await input.ReadAsync(encoded.AsMemory(length), cancellationToken)
                .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            length += count;
        }

        return DecodeInput(encoded.AsSpan(0, length));
    }

    private static (byte[] Microphone, byte[] Remote)? DecodeInput(ReadOnlySpan<byte> input)
    {
        if (input.Length > MaximumInputBytes) return null;
        int split = input.IndexOf((byte)'\n');
        if (split < 0) return null;
        var firstLine = input[..split];
        var secondLine = input[(split + 1)..];
        if (secondLine.EndsWith("\n"u8)) secondLine = secondLine[..^1];
        var microphone = DecodePcm(firstLine);
        var remote = DecodePcm(secondLine);
        return microphone is not null && remote is not null && !microphone.AsSpan().SequenceEqual(remote)
            ? (microphone, remote) : null;
    }

    private static byte[]? DecodePcm(ReadOnlySpan<byte> line)
    {
        if (line.EndsWith("\r"u8)) line = line[..^1];
        if (line.Length == 0 || line.Length > MaximumEncodedLineBytes ||
            line.IndexOfAny((byte)'\r', (byte)'\n', (byte)' ') >= 0)
            return null;

        byte[] decoded = new byte[Base64.GetMaxDecodedFromUtf8Length(line.Length)];
        OperationStatus result = Base64.DecodeFromUtf8(line, decoded, out int consumed, out int written);
        if (result != OperationStatus.Done || consumed != line.Length || written == 0 ||
            written > MaximumSourceBytes || written % BytesPerSample != 0 ||
            decoded.AsSpan(0, written).StartsWith("RIFF"u8) ||
            decoded.AsSpan(0, written).IndexOfAnyExcept((byte)0) < 0)
            return null;

        Array.Resize(ref decoded, written);
        return decoded;
    }

    private static TranscriptionRequest Request(SessionId session, TimelineEpochId timeline, long startMicroseconds) =>
        new(ContractVersion.Current, new SourceIdentity(session, new SourceId(Guid.NewGuid()), new StreamEpoch(0)),
            Language, Format, new ProviderConfigurationReference(DeepgramStreamingOptions.ProviderId, ConfigurationVersion),
            new TimelinePosition(timeline, new SessionOffset(startMicroseconds)));

    private static async IAsyncEnumerable<OwnedAudioFrame> FramesAsync(TranscriptionRequest request,
        byte[] pcm, bool pace, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        int samples = pcm.Length / BytesPerSample;
        long sequence = 0;
        for (int offset = 0; offset < samples; offset += SamplesPerFrame)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pace && offset > 0)
                await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken).ConfigureAwait(false);

            int count = Math.Min(SamplesPerFrame, samples - offset);
            var interval = new TimelineInterval(Position(request, offset), Position(request, offset + count));
            var metadata = new AudioFrameMetadata(request.Version, request.Source, new FrameSequence(sequence++),
                interval, request.Format, count);
            yield return CreateFrame(metadata, pcm.AsSpan(offset * BytesPerSample, metadata.PayloadByteCount));
        }
    }

    private static OwnedAudioFrame CreateFrame(AudioFrameMetadata metadata, ReadOnlySpan<byte> pcm)
    {
        IMemoryOwner<byte> owner = MemoryPool<byte>.Shared.Rent(pcm.Length);
        try
        {
            pcm.CopyTo(owner.Memory.Span);
            return new OwnedAudioFrame(metadata, owner, pcm.Length);
        }
        catch
        {
            owner.Dispose();
            throw;
        }
    }

    private static async Task<bool> PreflightAsync(string label, TranscriptionRequest request, byte[] pcm,
        CancellationToken cancellationToken)
    {
        int frameCount = 0;
        int byteCount = 0;
        var previousEnd = request.StreamStart;
        await foreach (var frame in FramesAsync(request, pcm, pace: false, cancellationToken).ConfigureAwait(false))
        {
            using (frame)
            {
                var metadata = frame.Metadata;
                if (metadata.Source != request.Source || metadata.Format != request.Format ||
                    metadata.Sequence.Value != frameCount || metadata.Interval.Start != previousEnd ||
                    !frame.Payload.Span.SequenceEqual(pcm.AsSpan(byteCount, metadata.PayloadByteCount)))
                {
                    Console.Error.WriteLine($"{label} frame validation failed.");
                    return false;
                }
                frameCount++;
                byteCount += metadata.PayloadByteCount;
                previousEnd = metadata.Interval.End;
            }
        }

        Console.WriteLine(FormattableString.Invariant(
            $"{label} frames={frameCount} samples={byteCount / BytesPerSample} startUs={request.StreamStart.Offset.Value} endUs={previousEnd.Offset.Value}"));
        return frameCount > 0 && byteCount == pcm.Length && previousEnd == Position(request, pcm.Length / BytesPerSample);
    }

    private static async Task<bool> ObserveAsync(IStreamingTranscriber transcriber, string label, TranscriptionRequest request,
        byte[] pcm, CancellationToken cancellationToken)
    {
        var lastPosition = Position(request, pcm.Length / BytesPerSample);
        bool valid = true, live = false, draining = false, stopped = false;
        int partials = 0, finals = 0, boundaries = 0, gaps = 0, degraded = 0;
        EventErrorCategory? error = null;
        var frames = FramesAsync(request, pcm, pace: true, cancellationToken);
        await foreach (var item in transcriber.TranscribeAsync(request, frames, cancellationToken).ConfigureAwait(false))
        {
            valid &= item.Header.Source == request.Source && item.Header.Version == request.Version && !stopped;
            switch (item)
            {
                case StreamStatus { State: TranscriptionStreamState.Live }:
                    live = true;
                    break;
                case StreamStatus { State: TranscriptionStreamState.Draining }:
                    draining = live;
                    break;
                case StreamStatus { State: TranscriptionStreamState.Stopped }:
                    stopped = true;
                    break;
                case StreamStatus { State: TranscriptionStreamState.Failed } failed:
                    error = failed.Error;
                    valid = false;
                    break;
                case StreamStatus { State: TranscriptionStreamState.Degraded }:
                    degraded++;
                    break;
                case PartialSegment partial:
                    valid &= live && InRange(partial.Interval.Start) && InRange(partial.Interval.End);
                    partials++;
                    break;
                case FinalSegment final:
                    valid &= live && !string.IsNullOrWhiteSpace(final.Text) &&
                        final.Intervals.All(interval => InRange(interval.Start) && InRange(interval.End));
                    finals++;
                    break;
                case UtteranceBoundary boundary:
                    valid &= live && InRange(boundary.Position);
                    boundaries++;
                    break;
                case TranscriptGap:
                    gaps++;
                    break;
                case FinalAmendment:
                    valid = false;
                    break;
            }
        }

        bool success = valid && live && draining && stopped && finals > 0 && gaps == 0;
        Console.WriteLine(FormattableString.Invariant(
            $"{label} result={(success ? "pass" : "fail")} live={live} draining={draining} stopped={stopped} partials={partials} finals={finals} boundaries={boundaries} gaps={gaps} degraded={degraded} error={error?.ToString() ?? "none"}"));
        return success;

        bool InRange(TimelinePosition position) => position.TimelineEpoch == request.StreamStart.TimelineEpoch &&
            position.Offset.Value >= request.StreamStart.Offset.Value && position.Offset.Value <= lastPosition.Offset.Value;
    }

    private static TimelinePosition Position(TranscriptionRequest request, int sampleOffset) =>
        new(request.StreamStart.TimelineEpoch,
            new SessionOffset(request.StreamStart.Offset.Value + sampleOffset * 1_000_000L / SampleRate));

    private sealed class LiveConfiguration(string key, DeepgramStreamingOptions options)
    {
        public string Key { get; } = key;
        public DeepgramStreamingOptions Options { get; } = options;
    }
}
