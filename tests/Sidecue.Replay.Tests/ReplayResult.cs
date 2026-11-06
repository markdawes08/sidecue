using System.Text.Json;

namespace Sidecue.Replay.Tests;

/// <summary>Normalized synthetic observations; no wall-clock durations or machine paths.</summary>
internal sealed record ReplayResult(string Id, string SessionId, string TimelineEpochId,
    string AnchorUtc, string FinalUtc, string[] TranscriptOutcomes, string[] AnswerOutcomes,
    int DurableCommits, int CurrentFinalCount, int PartialCount, int FramesDisposed,
    string VisibleAnswer, string[] TranscriptState)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    public string ToJson() => JsonSerializer.Serialize(this, JsonOptions);
}
