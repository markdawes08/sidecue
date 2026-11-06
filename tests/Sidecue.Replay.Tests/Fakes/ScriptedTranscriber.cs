using System.Runtime.CompilerServices;
using Sidecue.Application.Abstractions;
using Sidecue.Core.Contracts;

namespace Sidecue.Replay.Tests.Fakes;

/// <summary>Consumes synthetic audio, then emits fixture events in delivery order.</summary>
internal sealed class ScriptedTranscriber : IStreamingTranscriber
{
    private readonly TranscriptEvent[] _events;

    public ScriptedTranscriber(IEnumerable<TranscriptEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        _events = events.ToArray();
        foreach (var item in _events) { ArgumentNullException.ThrowIfNull(item); }
    }

    public int FramesConsumed { get; private set; }

    public async IAsyncEnumerable<TranscriptEvent> TranscribeAsync(TranscriptionRequest request,
        IAsyncEnumerable<OwnedAudioFrame> frames, [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(frames);
        ct.ThrowIfCancellationRequested();
        await foreach (var frame in frames.WithCancellation(ct).ConfigureAwait(false))
        {
            using (frame)
            {
                ct.ThrowIfCancellationRequested();
                if (frame.Metadata.Source != request.Source || frame.Metadata.Format != request.Format ||
                    frame.Metadata.Interval.TimelineEpoch != request.StreamStart.TimelineEpoch ||
                    frame.Metadata.Interval.Start.Offset.Value < request.StreamStart.Offset.Value)
                {
                    throw new InvalidOperationException("Scripted audio does not match the transcription request.");
                }
                _ = frame.Payload.Length;
                FramesConsumed++;
            }
        }
        foreach (var item in _events)
        {
            ct.ThrowIfCancellationRequested();
            if (item.Header.Source != request.Source)
            {
                throw new InvalidOperationException("A scripted event belongs to a different source.");
            }
            yield return item;
        }
    }
}
