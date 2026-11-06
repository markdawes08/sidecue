using Sidecue.Core.Contracts;
using Xunit;

namespace Sidecue.Application.Tests.Contracts;

public sealed class AssistanceContractTests
{
    [Fact]
    public void SnapshotCopiesEvidenceAndOmissionsWithFixedRequestMetadata()
    {
        var request = Request();
        var entry = Entry();
        var entries = new List<TranscriptContextEntry> { entry };
        var omission = new ContextOmission(ContextOmissionReason.TokenBudget, 1);
        var omissions = new List<ContextOmission> { omission };
        var snapshot = new ContextSnapshot(request, Generation(), entries, omissions: omissions);

        entries.Clear();
        omissions.Clear();

        Assert.Equal(entry, Assert.Single(snapshot.Transcript));
        Assert.Equal(omission, Assert.Single(snapshot.Omissions));
        Assert.Equal(request.Fence, snapshot.Fence);
        Assert.Equal(request.Version, snapshot.Version);
        Assert.Equal(request.TranscriptCutoff, snapshot.TranscriptVersion);
        Assert.Equal(ContractSamples.Profile, snapshot.Request.ProfileVersionId);
    }

    [Fact]
    public void SnapshotRejectsForeignSessionAndEvidenceBeyondTheFrozenCutoff()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ContextSnapshot(Request(), Generation(),
            [Entry(ContractSamples.OtherSession)]));
        Assert.ThrowsAny<ArgumentException>(() => new ContextSnapshot(Request(), Generation(),
            [Entry(commit: 2)]));
    }

    [Fact]
    public void SnapshotRejectsPreClearEvidenceAndDuplicateCitationKeys()
    {
        Assert.ThrowsAny<ArgumentException>(() => new ContextSnapshot(Request(contextStart: 50_000), Generation(), [Entry()]));
        Assert.ThrowsAny<ArgumentException>(() => new ContextSnapshot(Request(), Generation(), [Entry(), Entry()]));
    }

    [Fact]
    public void ProvisionalTextMustRemainSeparateFromCommittedEvidence()
    {
        var partial = new PartialSegment(ContractSamples.Header(), ContractSamples.Segment, new SegmentRevision(0),
            ContractSamples.Interval(), "Synthetic provisional text");

        Assert.ThrowsAny<ArgumentException>(() => new TranscriptContextEntry(new CitationId("partial"),
            new TranscriptCommitVersion(1), partial));
        var snapshot = new ContextSnapshot(Request(), Generation(), [], partial);

        Assert.Same(partial, snapshot.Provisional);
        Assert.Empty(snapshot.Transcript);
    }

    [Fact]
    public void QuestionActionRequiresQuestionAndOtherActionsCannotHideOne()
    {
        Assert.ThrowsAny<ArgumentException>(() => Request(question: " "));
        Assert.ThrowsAny<ArgumentException>(() => new AssistRequest(ContractVersion.Current, Fence(), AssistAction.Summarize,
            ContractSamples.Profile, new TranscriptCommitVersion(1), ContractSamples.Position(),
            AssistOutputProfile.Concise, new TokenBudget(1_000, 100), "en", "Hidden question"));
    }

    [Fact]
    public void RequestsAndCommandsRejectUnsupportedVersions()
    {
        ContractVersion[] versions = [default, new(2)];
        Assert.All(versions, version =>
        {
            Assert.ThrowsAny<ArgumentException>(() => Request(version: version));
            Assert.ThrowsAny<ArgumentException>(() => new PauseListening(version, ContractSamples.Session));
            Assert.ThrowsAny<ArgumentException>(() => new TranscriptWrite(version, ContractSamples.Final()));
            Assert.ThrowsAny<ArgumentException>(() => new CaptureRequest(version, ContractSamples.Identity(),
                new SessionClockAnchor(ContractSamples.Session, ContractSamples.Position(), CaptureAndStoreContractTests.Utc()),
                CaptureAndStoreContractTests.Selection(), new AudioBufferPolicy(2_000_000, 64_000)));
        });
    }

    [Fact]
    public void PauseHideClearAndEndAreDistinctSessionCommands()
    {
        SessionCommand[] commands =
        [
            new PauseListening(ContractVersion.Current, ContractSamples.Session),
            new HideOverlay(ContractVersion.Current, ContractSamples.Session),
            new ClearAssistantContext(ContractVersion.Current, ContractSamples.Session),
            new EndSession(ContractVersion.Current, ContractSamples.Session)
        ];

        Assert.Equal(4, commands.Select(command => command.GetType()).Distinct().Count());
        Assert.All(commands, command =>
        {
            Assert.Equal(ContractVersion.Current, command.Version);
            Assert.Equal(ContractSamples.Session, command.SessionId);
        });
        Assert.ThrowsAny<ArgumentException>(() => new ClearAssistantContext(ContractVersion.Current, default));
    }

    internal static AnswerFence Fence() => new(ContractSamples.Session, new ContextEpoch(1), ContractSamples.Request);
    internal static AssistRequest Request(string question = "Synthetic question", long contextStart = 0,
        ContractVersion? version = null) =>
        new(version ?? ContractVersion.Current, Fence(), AssistAction.AnswerQuestion, ContractSamples.Profile,
            new TranscriptCommitVersion(1), ContractSamples.Position(contextStart), AssistOutputProfile.Concise,
            new TokenBudget(1_000, 100), "en", question);
    internal static GenerationConfiguration Generation() =>
        new(new ProviderConfigurationReference("synthetic-provider", "fixture-config-1"), "fixture-model", "fixture-capabilities-1");
    private static TranscriptContextEntry Entry(SessionId? session = null, long commit = 1) =>
        new(new CitationId("synthetic-final-1"), new TranscriptCommitVersion(commit), ContractSamples.Final(session));
}
