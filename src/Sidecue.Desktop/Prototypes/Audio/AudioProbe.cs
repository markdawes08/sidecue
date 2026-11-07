using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;
using Sidecue.Windows.Audio;
using CaptureState = Sidecue.Core.Contracts.CaptureState;

namespace Sidecue.Desktop.Prototypes.Audio;

/// <summary>Short local capture diagnostic. Only aggregate metadata is retained.</summary>
internal static class AudioProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        // Open, capture, render and teardown away from the WPF dispatcher.
        var result = await Task.Run(ProbeAsync).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "audio-probe.json"),
            JsonSerializer.Serialize(result, JsonOptions)).ConfigureAwait(false);
        return result.Passed ? 0 : 1;
    }

    private static async Task<ProbeResult> ProbeAsync()
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            var clock = new ProbeClock();
            var capture = new WasapiCaptureSource(clock, new NAudioCaptureBackendFactory());
            var microphoneRequest = Request(clock, CaptureSourceKind.Microphone);
            var loopbackRequest = Request(clock, CaptureSourceKind.RemotePlayback);
            await using var microphone = await capture.OpenAsync(microphoneRequest, CancellationToken.None).ConfigureAwait(false);
            await using var loopback = await capture.OpenAsync(loopbackRequest, CancellationToken.None).ConfigureAwait(false);
            // Silent synthetic playback wakes an otherwise idle loopback endpoint.
            // It neither changes the user's endpoint volume nor writes any audio.
            using var enumerator = new MMDeviceEnumerator();
            using var renderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            await using var playback = new WasapiPlayerBuilder().WithDevice(renderDevice).Build();
            playback.Init(new SilentWaveProvider());
            playback.Play();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var microphoneFrames = CountAsync(microphone, microphoneRequest, timeout.Token);
            var loopbackFrames = CountAsync(loopback, loopbackRequest, timeout.Token);
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token).ConfigureAwait(false);
            playback.Stop();
            await Task.WhenAll(microphone.StopAsync(CaptureStopReason.End, CancellationToken.None),
                loopback.StopAsync(CaptureStopReason.End, CancellationToken.None)).ConfigureAwait(false);
            var mic = await microphoneFrames.ConfigureAwait(false);
            var remote = await loopbackFrames.ConfigureAwait(false);
            var passed = microphoneRequest.Source != loopbackRequest.Source && mic.Frames > 0 && remote.Frames > 0 &&
                mic.ValidIdentity && remote.ValidIdentity && mic.OrderedSequences && remote.OrderedSequences &&
                microphone.TerminalStatus?.State == CaptureState.Stopped && loopback.TerminalStatus?.State == CaptureState.Stopped;
            return new(started, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
                passed, mic, remote, microphone.TerminalStatus?.State.ToString(), loopback.TerminalStatus?.State.ToString(), null);
        }
        catch (Exception exception)
        {
            // Exception messages may contain device identifiers. Retain only the type.
            return new(started, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
                false, null, null, null, null, exception.GetType().Name);
        }
    }

    private static CaptureRequest Request(ProbeClock clock, CaptureSourceKind kind) => new(
        ContractVersion.Current,
        new SourceIdentity(clock.Anchor.SessionId, new SourceId(Guid.NewGuid()), new StreamEpoch(0)),
        clock.Anchor, new CaptureSourceSelection(kind, NAudioCaptureBackendFactory.SystemDefaultSelector), new AudioBufferPolicy(2_000_000, 2_000_000));

    private static async Task<FrameSummary> CountAsync(IAudioCaptureRun run, CaptureRequest request, CancellationToken ct)
    {
        var frames = 0;
        long bytes = 0;
        var validIdentity = true;
        var orderedSequences = true;
        long previousSequence = -1;
        var qpcFrames = 0;
        var devicePositionFrames = 0;
        var uncertainFrames = 0;
        AudioFormat? format = null;
        await foreach (var frame in run.ReadFramesAsync(ct).ConfigureAwait(false))
        {
            using (frame)
            {
                validIdentity &= frame.Metadata.Source == request.Source;
                orderedSequences &= frame.Metadata.Sequence.Value > previousSequence;
                previousSequence = frame.Metadata.Sequence.Value;
                if (frame.Metadata.PerformanceCounter is not null) { qpcFrames++; }
                if (frame.Metadata.DevicePosition is not null) { devicePositionFrames++; }
                if ((frame.Metadata.Flags & AudioFrameAttributes.TimestampError) != 0) { uncertainFrames++; }
                format ??= frame.Metadata.Format;
                frames++;
                bytes += frame.Metadata.PayloadByteCount;
            }
        }
        return new(request.Selection.Kind.ToString(), frames, bytes, validIdentity, orderedSequences,
            format?.SampleRate, format?.Channels, format?.Representation.ToString(), qpcFrames, devicePositionFrames, uncertainFrames);
    }

    private sealed class ProbeClock : ISessionClock
    {
        private readonly long _start = Stopwatch.GetTimestamp();
        public SessionClockAnchor Anchor { get; } = new(new SessionId(Guid.NewGuid()),
            new TimelinePosition(new TimelineEpochId(Guid.NewGuid()), new SessionOffset(0)),
            new UtcTimestamp(DateTimeOffset.UtcNow));

        public TimelinePosition GetPosition() => new(Anchor.Origin.TimelineEpoch,
            new SessionOffset((long)((Int128)(Stopwatch.GetTimestamp() - _start) * 1_000_000 / Stopwatch.Frequency)));
        public UtcTimestamp GetUtcNow() => new(DateTimeOffset.UtcNow);
    }

    private sealed class SilentWaveProvider : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        public int Read(Span<byte> buffer)
        {
            buffer.Clear();
            return buffer.Length;
        }

    }

    private sealed record FrameSummary(string Source, int Frames, long Bytes, bool ValidIdentity, bool OrderedSequences,
        int? SampleRate, int? Channels, string? Representation, int QpcFrames, int DevicePositionFrames, int UncertainFrames);
    private sealed record ProbeResult(DateTimeOffset RecordedUtc, string OperatingSystem, string Runtime, bool Passed,
        FrameSummary? Microphone, FrameSummary? Loopback, string? MicrophoneTerminal, string? LoopbackTerminal, string? FailureType);
}
