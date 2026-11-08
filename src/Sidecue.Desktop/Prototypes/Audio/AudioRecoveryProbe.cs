using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Channels;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;
using Sidecue.Windows.Audio;
using CaptureState = Sidecue.Core.Contracts.CaptureState;

namespace Sidecue.Desktop.Prototypes.Audio;

/// <summary>Local metadata-only diagnostic for manual endpoint transitions.</summary>
internal static class AudioRecoveryProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    internal static async Task<int> RunAsync(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("SIDECUE_AUDIO_PROBE_SECONDS"), out var requested)
            ? requested : 30;
        if (seconds is < 2 or > 120) { return 2; }
        var result = await Task.Run(() => ProbeAsync(seconds)).ConfigureAwait(false);
        await File.WriteAllTextAsync(Path.Combine(outputDirectory, "audio-recovery-probe.json"),
            JsonSerializer.Serialize(result, JsonOptions)).ConfigureAwait(false);
        return result.FailureType is null ? 0 : 1;
    }

    private static async Task<ProbeResult> ProbeAsync(int seconds)
    {
        var started = DateTimeOffset.UtcNow;
        try
        {
            var clock = new QpcSessionClock(new SessionId(Guid.NewGuid()), new TimelineEpochId(Guid.NewGuid()));
            using var monitor = new EndpointChangeMonitor();
            var micRequest = Request(clock, CaptureSourceKind.Microphone);
            var remoteRequest = Request(clock, CaptureSourceKind.RemotePlayback);
            var mic = new Summary(micRequest);
            var loopback = new Summary(remoteRequest);
            var source = new ObservedCaptureSource(new WasapiCaptureSource(clock, new NAudioCaptureBackendFactory()), mic, loopback);
            await using var microphone = new RecoveringAudioCapture(source, micRequest, clock, monitor);
            await using var remote = new RecoveringAudioCapture(source, remoteRequest, clock, monitor);
            using var playbackCancellation = new CancellationTokenSource();
            var playback = PlaySilenceAsync(monitor, playbackCancellation.Token);
            var micFrames = CountFramesAsync(microphone, mic);
            var remoteFrames = CountFramesAsync(remote, loopback);
            var micStatuses = CountStatusesAsync(microphone, mic);
            var remoteStatuses = CountStatusesAsync(remote, loopback);
            await Task.Delay(TimeSpan.FromSeconds(seconds)).ConfigureAwait(false);
            playbackCancellation.Cancel();
            await playback.ConfigureAwait(false);
            await Task.WhenAll(microphone.StopAsync(CaptureStopReason.End, CancellationToken.None),
                remote.StopAsync(CaptureStopReason.End, CancellationToken.None)).ConfigureAwait(false);
            await Task.WhenAll(micFrames, remoteFrames, micStatuses, remoteStatuses).ConfigureAwait(false);
            return new(started, seconds, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
                mic.Result(microphone.TerminalStatus), loopback.Result(remote.TerminalStatus), null);
        }
        catch (Exception exception)
        {
            // Native messages may contain identifying endpoint data.
            return new(started, seconds, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
                null, null, exception.GetType().Name);
        }
    }

    private static CaptureRequest Request(QpcSessionClock clock, CaptureSourceKind kind) => new(
        ContractVersion.Current, new SourceIdentity(clock.Anchor.SessionId, new SourceId(Guid.NewGuid()), new StreamEpoch(0)),
        clock.Anchor, new CaptureSourceSelection(kind, NAudioCaptureBackendFactory.SystemDefaultSelector), new AudioBufferPolicy(2_000_000, 2_000_000));

    private static async Task CountFramesAsync(RecoveringAudioCapture run, Summary summary)
    {
        await foreach (var frame in run.ReadFramesAsync(CancellationToken.None).ConfigureAwait(false))
        {
            using (frame) { summary.Frame(frame.Metadata); }
        }
    }

    private static async Task CountStatusesAsync(RecoveringAudioCapture run, Summary summary)
    {
        await foreach (var status in run.ReadStatusAsync(CancellationToken.None).ConfigureAwait(false)) { summary.Status(status); }
    }

    private static async Task PlaySilenceAsync(EndpointChangeMonitor monitor, CancellationToken ct)
    {
        var changed = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        void OnChange(CaptureSourceKind kind) { if (kind == CaptureSourceKind.RemotePlayback) { changed.Writer.TryWrite(true); } }
        monitor.DefaultEndpointChanged += OnChange;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var enumerator = new MMDeviceEnumerator();
                    using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                    await using var player = new WasapiPlayerBuilder().WithDevice(device).Build();
                    player.Init(new Silence());
                    player.Play();
                    try { await changed.Reader.ReadAsync(ct).ConfigureAwait(false); }
                    finally { player.Stop(); }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception)
                {
                    // Absence or transition of a render endpoint is represented by capture status.
                    try { await Task.Delay(TimeSpan.FromMilliseconds(250), ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                }
            }
        }
        finally { monitor.DefaultEndpointChanged -= OnChange; }
    }

    private sealed class Silence : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48_000, 2);
        public int Read(Span<byte> buffer) { buffer.Clear(); return buffer.Length; }
    }

    // Counts metadata only as the normalizer consumes raw frames. This does not
    // count packets already lost or evicted before reaching the normalizer.
    private sealed class ObservedCaptureSource(IAudioCaptureSource source, Summary microphone, Summary playback) : IAudioCaptureSource
    {
        public async Task<IAudioCaptureRun> OpenAsync(CaptureRequest request, CancellationToken ct)
        {
            var summary = request.Selection.Kind switch
            {
                CaptureSourceKind.Microphone => microphone,
                CaptureSourceKind.RemotePlayback => playback,
                _ => throw new ArgumentOutOfRangeException(nameof(request))
            };
            return new ObservedRun(await source.OpenAsync(request, ct).ConfigureAwait(false), summary);
        }
    }

    private sealed class ObservedRun(IAudioCaptureRun inner, Summary summary) : IAudioCaptureRun
    {
        public CaptureStatusEvent? TerminalStatus => inner.TerminalStatus;
        public async IAsyncEnumerable<OwnedAudioFrame> ReadFramesAsync([EnumeratorCancellation] CancellationToken ct)
        {
            await foreach (var frame in inner.ReadFramesAsync(ct).ConfigureAwait(false))
            {
                try { summary.RawFrame(frame.Metadata); }
                catch { frame.Dispose(); throw; }
                yield return frame;
            }
        }
        public IAsyncEnumerable<CaptureStatusEvent> ReadStatusAsync(CancellationToken ct) => inner.ReadStatusAsync(ct);
        public Task StopAsync(CaptureStopReason reason, CancellationToken ct) => inner.StopAsync(reason, ct);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private sealed class Summary(CaptureRequest request)
    {
        private const int MaximumEpochs = 128;
        private readonly Lock _gate = new();
        private readonly Dictionary<long, Epoch> _epochs = [];
        private int _omittedEpochEvents;
        private bool _identityValid = true;
        private bool _timelineValid = true;

        internal void RawFrame(AudioFrameMetadata metadata)
        {
            lock (_gate)
            {
                Validate(metadata.Source, metadata.Interval.TimelineEpoch);
                if (Find(metadata.Source.StreamEpoch.Value) is not { } epoch) { return; }
                var key = $"{metadata.Format.SampleRate}Hz:{metadata.Format.Channels}ch:{metadata.Format.Representation}";
                if (!epoch.RawConsumedFormats.TryGetValue(key, out var aggregate))
                {
                    aggregate = new RawAggregate();
                    epoch.RawConsumedFormats.Add(key, aggregate);
                }
                aggregate.Frames++;
                aggregate.Samples += metadata.SampleCount;
                aggregate.DurationMicroseconds += (long)((Int128)metadata.SampleCount * 1_000_000 / metadata.Format.SampleRate);
            }
        }

        internal void Frame(AudioFrameMetadata metadata)
        {
            lock (_gate)
            {
                Validate(metadata.Source, metadata.Interval.TimelineEpoch);
                if (Find(metadata.Source.StreamEpoch.Value) is not { } epoch) { return; }
                epoch.Frames++;
                epoch.Samples += metadata.SampleCount;
                epoch.Bytes += metadata.PayloadByteCount;
                epoch.OrderedSequences &= metadata.Sequence.Value > epoch.LastSequence;
                epoch.LastSequence = metadata.Sequence.Value;
                epoch.EarliestStartMicroseconds = Math.Min(epoch.EarliestStartMicroseconds ?? long.MaxValue, metadata.Interval.Start.Offset.Value);
                epoch.LatestEndMicroseconds = Math.Max(epoch.LatestEndMicroseconds ?? 0, metadata.Interval.End.Offset.Value);
                epoch.ValidFormat &= metadata.Format is { SampleRate: 16_000, Channels: 1, Representation: AudioSampleRepresentation.Pcm16 };
                if ((metadata.Flags & AudioFrameAttributes.SessionTimestampEstimated) != 0) { epoch.EstimatedFrames++; }
            }
        }

        internal void Status(CaptureStatusEvent status)
        {
            lock (_gate)
            {
                Validate(status.Source, status.Position.TimelineEpoch);
                if (Find(status.Source.StreamEpoch.Value) is not { } epoch) { return; }
                epoch.StatusEvents++;
                if (status.State == CaptureState.Failed) { epoch.Failures++; }
                if (status.Gap is { } gap)
                {
                    var key = $"{gap.Reason}:{gap.Certainty}";
                    if (!epoch.Gaps.TryGetValue(key, out var aggregate)) { aggregate = new GapAggregate(); epoch.Gaps.Add(key, aggregate); }
                    aggregate.Count++;
                    aggregate.ReportedDurationMicroseconds += gap.Interval.DurationMicroseconds;
                }
            }
        }

        internal SourceResult Result(CaptureStatusEvent? terminal)
        {
            lock (_gate)
            {
                return new SourceResult(request.Selection.Kind.ToString(), _identityValid, _timelineValid, _omittedEpochEvents,
                    terminal?.State.ToString(), terminal?.Error?.ToString(), _epochs.OrderBy(item => item.Key).Select(item => item.Value).ToArray());
            }
        }

        private void Validate(SourceIdentity source, TimelineEpochId timeline)
        {
            _identityValid &= source.SessionId == request.Source.SessionId && source.SourceId == request.Source.SourceId;
            _timelineValid &= timeline == request.ClockAnchor.Origin.TimelineEpoch;
        }

        private Epoch? Find(long number)
        {
            if (_epochs.TryGetValue(number, out var existing)) { return existing; }
            if (_epochs.Count == MaximumEpochs) { _omittedEpochEvents++; return null; }
            var epoch = new Epoch(number);
            _epochs.Add(number, epoch);
            return epoch;
        }
    }

    private sealed class Epoch(long number)
    {
        public long EpochNumber { get; } = number;
        public int Frames { get; set; }
        public long Samples { get; set; }
        public long Bytes { get; set; }
        public int EstimatedFrames { get; set; }
        public bool ValidFormat { get; set; } = true;
        public bool OrderedSequences { get; set; } = true;
        public long LastSequence { get; set; } = -1;
        public long? EarliestStartMicroseconds { get; set; }
        public long? LatestEndMicroseconds { get; set; }
        public int StatusEvents { get; set; }
        public int Failures { get; set; }
        public Dictionary<string, GapAggregate> Gaps { get; } = [];
        public Dictionary<string, RawAggregate> RawConsumedFormats { get; } = [];
    }

    private sealed class RawAggregate
    {
        public int Frames { get; set; }
        public long Samples { get; set; }
        public long DurationMicroseconds { get; set; }
    }

    private sealed class GapAggregate
    {
        public int Count { get; set; }
        public long ReportedDurationMicroseconds { get; set; }
    }

    private sealed record SourceResult(string Source, bool ValidIdentity, bool ValidTimeline, int OmittedEpochEvents,
        string? TerminalState, string? TerminalError, Epoch[] Epochs);
    private sealed record ProbeResult(DateTimeOffset RecordedUtc, int DurationSeconds, string OperatingSystem, string Runtime,
        SourceResult? Microphone, SourceResult? Loopback, string? FailureType);
}
