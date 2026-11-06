using System.Globalization;
using System.Text.Json;
using Sidecue.Core.Contracts;
using Sidecue.Replay.Tests.Fakes;
using Sidecue.Replay.Tests.Reference;

namespace Sidecue.Replay.Tests;

/// <summary>Single-threaded fixture driver and reference observations, never product orchestration.</summary>
internal static class ReplayHarness
{
    internal static async Task<ReplayResult> RunAsync(string fixturePath)
    {
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(fixturePath));
        var root = fixture.RootElement;
        if (root.GetProperty("schemaVersion").GetInt32() != 1)
        {
            throw new ArgumentException("Unsupported replay fixture schema.", nameof(fixturePath));
        }
        var session = new SessionId(root.GetProperty("sessionId").GetGuid());
        var timeline = new TimelineEpochId(root.GetProperty("timelineEpochId").GetGuid());
        var anchor = new SessionClockAnchor(session, new TimelinePosition(timeline, new SessionOffset(0)),
            new UtcTimestamp(root.GetProperty("anchorUtc").GetDateTimeOffset()));
        var clock = new ManualSessionClock(anchor);
        var store = new FakeSessionStore();
        var transcript = new TranscriptReferenceModel(store);
        var outcomes = new List<string>();
        var disposedFrames = 0;
        var format = new AudioFormat(16_000, 1, AudioSampleRepresentation.Pcm16, AudioByteOrder.LittleEndian);

        foreach (var stream in root.GetProperty("streams").EnumerateArray())
        {
            var source = new SourceIdentity(session, new SourceId(stream.GetProperty("sourceId").GetGuid()),
                new StreamEpoch(stream.GetProperty("epoch").GetInt64()));
            var start = stream.GetProperty("startUs").GetInt64();
            var count = stream.GetProperty("frameCount").GetInt32();
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
            var metadata = Enumerable.Range(0, count).Select(index => new AudioFrameMetadata(
                ContractVersion.Current, source, new FrameSequence(index),
                new TimelineInterval(new TimelinePosition(timeline, new SessionOffset(checked(start + index * 100_000L))),
                    new TimelinePosition(timeline, new SessionOffset(checked(start + (index + 1L) * 100_000L)))),
                format, 1_600)).ToArray();
            var capture = new FakeCaptureSource(clock, metadata);
            var scripted = stream.GetProperty("events").EnumerateArray().ToArray();
            var events = scripted.Select(item => FixtureEvents.Read(item, source, timeline)).ToArray();
            var transcriber = new ScriptedTranscriber(events);
            var sourceKind = FixtureEvents.Text(stream, "sourceKind") switch
            {
                "microphone" => CaptureSourceKind.Microphone,
                "remotePlayback" => CaptureSourceKind.RemotePlayback,
                _ => throw new ArgumentException("Unknown fixture capture source kind.", nameof(fixturePath))
            };
            var request = new CaptureRequest(ContractVersion.Current, source, anchor,
                new CaptureSourceSelection(sourceKind, "synthetic-source"),
                new AudioBufferPolicy(2_000_000, 64_000));
            // Batch delivery time advances while original overlapping capture offsets stay intact.
            clock.AdvanceTo(Math.Max(clock.GetPosition().Offset.Value, checked(start + count * 100_000L)));
            await using (var run = await capture.OpenAsync(request, CancellationToken.None))
            {
                var transcription = new TranscriptionRequest(ContractVersion.Current, source, "en", format,
                    new ProviderConfigurationReference("synthetic-speech", "fixture-1"), metadata[0].Interval.Start);
                var index = 0;
                await foreach (var item in transcriber.TranscribeAsync(transcription,
                    run.ReadFramesAsync(CancellationToken.None), CancellationToken.None))
                {
                    outcomes.Add($"{FixtureEvents.Text(scripted[index++], "label")}:{await transcript.ApplyAsync(item)}");
                }
                await run.StopAsync(CaptureStopReason.End, CancellationToken.None);
            }
            if (capture.OwnersCreated != capture.OwnersDisposed)
            {
                throw new InvalidOperationException("Replay leaked an audio owner.");
            }
            disposedFrames += capture.OwnersDisposed;
        }

        foreach (var invalid in root.GetProperty("invalidEvents").EnumerateArray())
        {
            var outcome = "unexpectedly-valid";
            try
            {
                var source = new SourceIdentity(session, new SourceId(invalid.GetProperty("sourceId").GetGuid()),
                    new StreamEpoch(invalid.GetProperty("epoch").GetInt64()));
                _ = FixtureEvents.Read(invalid, source, timeline);
            }
            catch (ArgumentException) { outcome = "invalid"; }
            outcomes.Add($"{FixtureEvents.Text(invalid, "label")}:{outcome}");
        }

        var stored = await store.ReadSessionAsync(session, CancellationToken.None);
        using var answers = new AnswerReferenceDriver(session, clock, stored, transcript.CurrentFinals);
        var answerOutcomes = new List<string>();
        foreach (var step in root.GetProperty("assistance").EnumerateArray())
        {
            answerOutcomes.Add($"{FixtureEvents.Text(step, "label")}:{await answers.ApplyAsync(step)}");
        }
        return new ReplayResult(FixtureEvents.Text(root, "id"), session.Value.ToString("D"), timeline.Value.ToString("D"),
            anchor.UtcAtOrigin.Value.ToString("O", CultureInfo.InvariantCulture), clock.GetUtcNow().Value.ToString("O", CultureInfo.InvariantCulture),
            outcomes.ToArray(), answerOutcomes.ToArray(), checked((int)stored.CommitVersion.Value),
            transcript.CurrentFinals.Count, transcript.Partials.Count, disposedFrames, answers.VisibleAnswer,
            transcript.CurrentFinals.Select(NormalizeFinal).ToArray());
    }

    private static string NormalizeFinal(TranscriptEvent item)
    {
        var (segment, revision, intervals) = item switch
        {
            FinalSegment final => (final.SegmentId, final.Revision, final.Intervals),
            FinalAmendment amendment => (amendment.SegmentId, amendment.Revision, amendment.Intervals),
            _ => throw new ArgumentException("Expected finalized replay state.", nameof(item))
        };
        var source = item.Header.Source;
        var ranges = string.Join(",", intervals.Select(interval => FormattableString.Invariant(
            $"{interval.TimelineEpoch.Value:D}:{interval.Start.Offset.Value}-{interval.End.Offset.Value}")));
        return FormattableString.Invariant($"{source.SourceId.Value:D}/epoch{source.StreamEpoch.Value}/{segment.Value:D}/r{revision.Value}/seq{item.Header.Sequence.Value}|{ranges}|{TranscriptFingerprint.Compute(item)}");
    }
}
