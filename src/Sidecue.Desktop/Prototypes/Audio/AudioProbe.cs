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

    internal static async Task<int> RunAsync(string outputDirectory, bool normalize)
    {
        Directory.CreateDirectory(outputDirectory);
        // Open, capture, render and teardown away from the WPF dispatcher.
        var result = await Task.Run(() => ProbeAsync(normalize)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "audio-probe.json"),
            JsonSerializer.Serialize(result, JsonOptions)).ConfigureAwait(false);
        return result.Passed ? 0 : 1;
    }

    private static async Task<ProbeResult> ProbeAsync(bool normalize)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            var clock = new QpcSessionClock(new SessionId(Guid.NewGuid()), new TimelineEpochId(Guid.NewGuid()));
            var capture = new WasapiCaptureSource(clock, new NAudioCaptureBackendFactory());
            var microphoneRequest = Request(clock, CaptureSourceKind.Microphone);
            var loopbackRequest = Request(clock, CaptureSourceKind.RemotePlayback);
            await using var microphone = await OpenAsync(capture, microphoneRequest, normalize).ConfigureAwait(false);
            await using var loopback = await OpenAsync(capture, loopbackRequest, normalize).ConfigureAwait(false);
            // Silent synthetic playback wakes an otherwise idle loopback endpoint.
            // It neither changes the user's endpoint volume nor writes any audio.
            using var enumerator = new MMDeviceEnumerator();
            using var renderDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            await using var playback = new WasapiPlayerBuilder().WithDevice(renderDevice).Build();
            playback.Init(new SilentWaveProvider());
            playback.Play();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var microphoneFrames = CountAsync(microphone, microphoneRequest, clock, timeout.Token);
            var loopbackFrames = CountAsync(loopback, loopbackRequest, clock, timeout.Token);
            var microphoneStatuses = CountStatusesAsync(microphone, timeout.Token);
            var loopbackStatuses = CountStatusesAsync(loopback, timeout.Token);
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token).ConfigureAwait(false);
            playback.Stop();
            await Task.WhenAll(microphone.StopAsync(CaptureStopReason.End, CancellationToken.None),
                loopback.StopAsync(CaptureStopReason.End, CancellationToken.None)).ConfigureAwait(false);
            var mic = await microphoneFrames.ConfigureAwait(false);
            var remote = await loopbackFrames.ConfigureAwait(false);
            var micStatus = await microphoneStatuses.ConfigureAwait(false);
            var remoteStatus = await loopbackStatuses.ConfigureAwait(false);
            var validOutputFormat = !normalize || (mic.SampleRate == 16_000 && mic.Channels == 1 && mic.Representation == nameof(AudioSampleRepresentation.Pcm16) &&
                remote.SampleRate == 16_000 && remote.Channels == 1 && remote.Representation == nameof(AudioSampleRepresentation.Pcm16));
            var passed = validOutputFormat && microphoneRequest.Source != loopbackRequest.Source && mic.Frames > 0 && remote.Frames > 0 &&
                mic.ValidIdentity && remote.ValidIdentity && mic.OrderedSequences && remote.OrderedSequences &&
                mic.FormatConsistent && remote.FormatConsistent &&
                micStatus.Failures == 0 && remoteStatus.Failures == 0 &&
                microphone.TerminalStatus?.State == CaptureState.Stopped && loopback.TerminalStatus?.State == CaptureState.Stopped;
            return new(started, normalize ? "normalized" : "native", RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
                passed, !normalize, normalize ? null : mic.QpcStartMatches > 0 && remote.QpcStartMatches > 0,
                mic, remote, micStatus, remoteStatus, microphone.TerminalStatus?.State.ToString(), loopback.TerminalStatus?.State.ToString(), null);
        }
        catch (Exception exception)
        {
            // Exception messages may contain device identifiers. Retain only the type.
            return new(started, normalize ? "normalized" : "native", RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
                false, !normalize, null, null, null, null, null, null, null, exception.GetType().Name);
        }
    }

    private static CaptureRequest Request(QpcSessionClock clock, CaptureSourceKind kind) => new(
        ContractVersion.Current,
        new SourceIdentity(clock.Anchor.SessionId, new SourceId(Guid.NewGuid()), new StreamEpoch(0)),
        clock.Anchor, new CaptureSourceSelection(kind, NAudioCaptureBackendFactory.SystemDefaultSelector), new AudioBufferPolicy(2_000_000, 2_000_000));

    private static async Task<IAudioCaptureRun> OpenAsync(WasapiCaptureSource source, CaptureRequest request, bool normalize)
    {
        var raw = await source.OpenAsync(request, CancellationToken.None).ConfigureAwait(false);
        if (!normalize) { return raw; }
        try { return new AudioNormalizationRun(raw, request, request.BufferPolicy); }
        catch
        {
            await raw.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async Task<FrameSummary> CountAsync(IAudioCaptureRun run, CaptureRequest request, QpcSessionClock clock, CancellationToken ct)
    {
        var frames = 0;
        long bytes = 0;
        var validIdentity = true;
        var orderedSequences = true;
        long previousSequence = -1;
        var qpcFrames = 0;
        var devicePositionFrames = 0;
        var uncertainFrames = 0;
        var nativeTimestampErrors = 0;
        var qpcStartMatches = 0;
        var qpcBeforeOrigin = 0;
        long? minimumQpcStartDelta = null;
        long? maximumQpcStartDelta = null;
        var formatConsistent = true;
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
                if ((frame.Metadata.Flags & AudioFrameAttributes.SessionTimestampEstimated) != 0) { uncertainFrames++; }
                if ((frame.Metadata.Flags & AudioFrameAttributes.TimestampError) != 0) { nativeTimestampErrors++; }
                if (frame.Metadata.PerformanceCounter is { TicksPerSecond: 10_000_000 } observedQpc)
                {
                    if (observedQpc.Ticks < clock.QpcAtOrigin.Ticks) { qpcBeforeOrigin++; }
                    else
                    {
                        // Relative aggregate only: no raw clock or audio data is retained.
                        var delta = ((Int128)observedQpc.Ticks - clock.QpcAtOrigin.Ticks) / 10 - frame.Metadata.Interval.Start.Offset.Value;
                        if (delta >= long.MinValue && delta <= long.MaxValue)
                        {
                            var value = (long)delta;
                            minimumQpcStartDelta = minimumQpcStartDelta is null ? value : Math.Min(minimumQpcStartDelta.Value, value);
                            maximumQpcStartDelta = maximumQpcStartDelta is null ? value : Math.Max(maximumQpcStartDelta.Value, value);
                        }
                    }
                }
                if (frame.Metadata.PerformanceCounter is { TicksPerSecond: 10_000_000 } qpc &&
                    qpc.Ticks >= clock.QpcAtOrigin.Ticks &&
                    (frame.Metadata.Flags & AudioFrameAttributes.TimestampError) == 0 &&
                    frame.Metadata.Interval.Start.Offset.Value == (qpc.Ticks - clock.QpcAtOrigin.Ticks) / 10)
                {
                    qpcStartMatches++;
                }
                format ??= frame.Metadata.Format;
                formatConsistent &= format == frame.Metadata.Format;
                frames++;
                bytes += frame.Metadata.PayloadByteCount;
            }
        }
        return new(request.Selection.Kind.ToString(), frames, bytes, validIdentity, orderedSequences, formatConsistent,
            format?.SampleRate, format?.Channels, format?.Representation.ToString(), qpcFrames, devicePositionFrames,
            uncertainFrames, nativeTimestampErrors, qpcStartMatches, qpcBeforeOrigin, minimumQpcStartDelta, maximumQpcStartDelta);
    }

    private static async Task<StatusSummary> CountStatusesAsync(IAudioCaptureRun run, CancellationToken ct)
    {
        var events = 0;
        var gaps = 0;
        var uncertainGaps = 0;
        var failures = 0;
        await foreach (var status in run.ReadStatusAsync(ct).ConfigureAwait(false))
        {
            events++;
            if (status.Gap is { } gap)
            {
                gaps++;
                if (gap.Certainty != TimingCertainty.Exact) { uncertainGaps++; }
            }
            if (status.State == CaptureState.Failed) { failures++; }
        }
        return new(events, gaps, uncertainGaps, failures);
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

    private sealed record FrameSummary(string Source, int Frames, long Bytes, bool ValidIdentity, bool OrderedSequences, bool FormatConsistent,
        int? SampleRate, int? Channels, string? Representation, int QpcFrames, int DevicePositionFrames,
        int UncertainFrames, int NativeTimestampErrors, int QpcStartMatches, int QpcBeforeOrigin,
        long? MinimumQpcStartDeltaMicroseconds, long? MaximumQpcStartDeltaMicroseconds);
    private sealed record StatusSummary(int Events, int Gaps, int UncertainGaps, int Failures);
    private sealed record ProbeResult(DateTimeOffset RecordedUtc, string Mode, string OperatingSystem, string Runtime, bool Passed,
        bool RawTimingApplicable, bool? QpcStartConsistencyObserved, FrameSummary? Microphone, FrameSummary? Loopback,
        StatusSummary? MicrophoneStatuses, StatusSummary? LoopbackStatuses,
        string? MicrophoneTerminal, string? LoopbackTerminal, string? FailureType);
}
