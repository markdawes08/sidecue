using System.Text.Json;
using Sidecue.Core.Contracts;
using Sidecue.Replay.Tests.Fakes;

namespace Sidecue.Replay.Tests;

/// <summary>Fixture-only request fence model. No scheduling, UI publication or persistence effects.</summary>
internal sealed class AnswerReferenceDriver(SessionId session, ManualSessionClock clock,
    SessionSnapshot stored, IReadOnlyList<TranscriptEvent> currentFinals) : IDisposable
{
    private readonly Dictionary<RequestId, Attempt> _attempts = [];
    private Attempt? _active;
    private ContextEpoch _epoch = new(0);
    private TimelinePosition _contextStart = clock.Anchor.Origin;
    private bool _terminal;
    public string VisibleAnswer { get; private set; } = "";

    internal async Task<string> ApplyAsync(JsonElement step)
    {
        var kind = FixtureEvents.Text(step, "kind");
        var epoch = new ContextEpoch(step.GetProperty("contextEpoch").GetInt64());
        if (kind == "clear")
        {
            if (epoch.Value != checked(_epoch.Value + 1)) { throw new ArgumentException("Clear must advance the context epoch once.", nameof(step)); }
            _active?.Cancellation.Cancel();
            _active = null;
            _epoch = epoch;
            _contextStart = clock.GetPosition();
            VisibleAnswer = "";
            return "cleared";
        }
        var requestId = new RequestId(step.GetProperty("requestId").GetGuid());
        if (kind == "request")
        {
            if (epoch != _epoch || _attempts.ContainsKey(requestId)) { throw new ArgumentException("Invalid replay request identity.", nameof(step)); }
            _active?.Cancellation.Cancel();
            var fence = new AnswerFence(session, epoch, requestId);
            var request = new AssistRequest(ContractVersion.Current, fence, AssistAction.Summarize,
                new ProfileVersionId(Guid.Parse("90000000-0000-0000-0000-000000000001")), stored.CommitVersion,
                _contextStart, AssistOutputProfile.Concise, new TokenBudget(2_000, 200), "en");
            var entries = currentFinals.Where(item => Start(item).Offset.Value >= _contextStart.Offset.Value)
                .Select(item => new TranscriptContextEntry(new CitationId(FormattableString.Invariant(
                    $"{item.Header.Source.SourceId.Value:D}/{item.Header.Source.StreamEpoch.Value}/{item.Header.EventId.Value:D}")),
                    stored.Entries.Single(entry => entry.Write.Event.Header == item.Header).CommitVersion, item));
            var snapshot = new ContextSnapshot(request, new GenerationConfiguration(
                new ProviderConfigurationReference("synthetic-assistance", "fixture-1"), "fixture-model", "fixture-1"), entries);
            _active = new Attempt(snapshot);
            _attempts.Add(requestId, _active);
            _terminal = false;
            VisibleAnswer = "";
            return "requested";
        }
        if (kind != "answer") { throw new ArgumentException("Unknown assistance fixture kind.", nameof(step)); }
        var attempt = _attempts[requestId];
        var fenceForEvent = new AnswerFence(session, epoch, requestId);
        var header = new AnswerEventHeader(ContractVersion.Current, fenceForEvent,
            new AnswerSequence(step.GetProperty("sequence").GetInt64()));
        AnswerEvent answer = FixtureEvents.Text(step, "answerKind") switch
        {
            "started" => new AnswerStarted(header),
            "delta" => new AnswerTextDelta(header, FixtureEvents.Text(step, "text")),
            "completed" => new AnswerCompleted(header, FixtureEvents.Text(step, "text"), [], "synthetic-assistance", "fixture-model"),
            _ => throw new ArgumentException("Unknown answer fixture kind.", nameof(step))
        };
        // Intentionally emulate a remote callback that survives cooperative cancellation.
        var provider = new ScriptedAssistanceProvider([answer], ignoreCancellation: true);
        var outcome = "missing";
        await foreach (var item in provider.GenerateAsync(attempt.Snapshot, attempt.Cancellation.Token))
        {
            if (_active?.Snapshot.Fence != item.Header.Fence) { outcome = "stale"; continue; }
            if (_terminal) { outcome = "after-terminal"; continue; }
            if (item.Header.Sequence.Value <= attempt.LastSequence) { outcome = "out-of-order"; continue; }
            attempt.LastSequence = item.Header.Sequence.Value;
            switch (item)
            {
                case AnswerTextDelta delta: VisibleAnswer += delta.Text; break;
                case AnswerCompleted completed: VisibleAnswer = completed.Text; _terminal = true; break;
            }
            outcome = "accepted";
        }
        return outcome;
    }

    private static TimelinePosition Start(TranscriptEvent item) => item switch
    {
        FinalSegment final => final.Intervals[0].Start,
        FinalAmendment amendment => amendment.Intervals[0].Start,
        _ => throw new ArgumentException("Expected finalized context.", nameof(item))
    };

    public void Dispose()
    {
        foreach (var attempt in _attempts.Values) { attempt.Cancellation.Dispose(); }
        GC.SuppressFinalize(this);
    }

    private sealed class Attempt(ContextSnapshot snapshot)
    {
        internal ContextSnapshot Snapshot { get; } = snapshot;
        internal CancellationTokenSource Cancellation { get; } = new();
        internal long LastSequence { get; set; } = -1;
    }
}
