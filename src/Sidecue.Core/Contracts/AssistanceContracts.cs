using System.Collections.Immutable;

namespace Sidecue.Core.Contracts;

/// <summary>A reference to immutable adapter configuration, never a credential.</summary>
public sealed record ProviderConfigurationReference
{
    public ProviderConfigurationReference(string providerId, string configurationVersion)
    {
        ContractGuard.Text(providerId, nameof(providerId));
        ContractGuard.Text(configurationVersion, nameof(configurationVersion));
        ProviderId = providerId;
        ConfigurationVersion = configurationVersion;
    }

    public string ProviderId { get; }
    public string ConfigurationVersion { get; }
    public override string ToString() => nameof(ProviderConfigurationReference);
}

/// <summary>Pinned model and evaluated capability version for one request.</summary>
public sealed record GenerationConfiguration
{
    public GenerationConfiguration(ProviderConfigurationReference provider, string modelId, string capabilityVersion)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ContractGuard.Text(modelId, nameof(modelId));
        ContractGuard.Text(capabilityVersion, nameof(capabilityVersion));
        Provider = provider;
        ModelId = modelId;
        CapabilityVersion = capabilityVersion;
    }

    public ProviderConfigurationReference Provider { get; }
    public string ModelId { get; }
    public string CapabilityVersion { get; }
    public override string ToString() => nameof(GenerationConfiguration);
}

public sealed record TokenBudget
{
    public TokenBudget(int inputTokens, int outputTokens)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputTokens);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputTokens);
        InputTokens = inputTokens;
        OutputTokens = outputTokens;
    }

    public int InputTokens { get; }
    public int OutputTokens { get; }
}

public enum AssistAction { AnswerQuestion, Summarize, NextSteps }
public enum AssistOutputProfile { Concise, Detailed }

/// <summary>A transcript-only request. External knowledge and images are not accepted by this schema subset.</summary>
public sealed record AssistRequest
{
    public AssistRequest(ContractVersion version, AnswerFence fence, AssistAction action,
        ProfileVersionId profileVersionId, TranscriptCommitVersion transcriptCutoff,
        TimelinePosition contextStart, AssistOutputProfile outputProfile, TokenBudget budget,
        string language, string? question = null)
    {
        ContractGuard.Version(version);
        ArgumentNullException.ThrowIfNull(fence);
        ContractGuard.Guid(profileVersionId.Value, nameof(profileVersionId));
        ArgumentNullException.ThrowIfNull(contextStart);
        ArgumentNullException.ThrowIfNull(budget);
        ContractGuard.Text(language, nameof(language));
        EventGuard.Defined(action, nameof(action));
        EventGuard.Defined(outputProfile, nameof(outputProfile));
        if (action == AssistAction.AnswerQuestion)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(question);
        }
        else if (question is not null)
        {
            throw new ArgumentException("Only the question action accepts a question.", nameof(question));
        }

        Version = version;
        Fence = fence;
        Action = action;
        ProfileVersionId = profileVersionId;
        TranscriptCutoff = transcriptCutoff;
        ContextStart = contextStart;
        OutputProfile = outputProfile;
        Budget = budget;
        Language = language;
        Question = question;
    }

    public ContractVersion Version { get; }
    public AnswerFence Fence { get; }
    public AssistAction Action { get; }
    public ProfileVersionId ProfileVersionId { get; }
    public TranscriptCommitVersion TranscriptCutoff { get; }
    public TimelinePosition ContextStart { get; }
    public AssistOutputProfile OutputProfile { get; }
    public TokenBudget Budget { get; }
    public string Language { get; }
    public string? Question { get; }
    public override string ToString() => $"AssistRequest {{ RequestId = {Fence.RequestId} }}";
}

/// <summary>One committed revision used as evidence, with a request-local citation key.</summary>
public sealed record TranscriptContextEntry
{
    public TranscriptContextEntry(CitationId citationId, TranscriptCommitVersion commitVersion, TranscriptEvent finalEvent)
    {
        ArgumentNullException.ThrowIfNull(citationId);
        ArgumentNullException.ThrowIfNull(finalEvent);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(commitVersion.Value, nameof(commitVersion));
        if (finalEvent is not (FinalSegment or FinalAmendment))
        {
            throw new ArgumentException("Context evidence must be a finalized revision.", nameof(finalEvent));
        }
        CitationId = citationId;
        CommitVersion = commitVersion;
        FinalEvent = finalEvent;
    }

    public CitationId CitationId { get; }
    public TranscriptCommitVersion CommitVersion { get; }
    public TranscriptEvent FinalEvent { get; }
    public override string ToString() => nameof(TranscriptContextEntry);
}

public enum ContextOmissionReason { TokenBudget, BeforeContextBoundary, ProvisionalExcluded }

public sealed record ContextOmission
{
    public ContextOmission(ContextOmissionReason reason, int count)
    {
        EventGuard.Defined(reason, nameof(reason));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        Reason = reason;
        Count = count;
    }

    public ContextOmissionReason Reason { get; }
    public int Count { get; }
}

/// <summary>Frozen transcript context at exactly the requested commit cutoff.</summary>
/// <remarks>The assembler must verify durable revisions, clear boundaries and model budgets.
/// Construction validates consistency, not database provenance or tokenization.</remarks>
public sealed record ContextSnapshot
{
    public ContextSnapshot(AssistRequest request, GenerationConfiguration generation,
        IEnumerable<TranscriptContextEntry> transcript, PartialSegment? provisional = null,
        IEnumerable<ContextOmission>? omissions = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(generation);
        ArgumentNullException.ThrowIfNull(transcript);
        var entries = transcript.ToImmutableArray();
        var citationIds = new HashSet<CitationId>();
        foreach (var entry in entries)
        {
            ArgumentNullException.ThrowIfNull(entry);
            if (entry.CommitVersion.Value > request.TranscriptCutoff.Value)
            {
                throw new ArgumentException("Evidence exceeds the requested transcript cutoff.", nameof(transcript));
            }
            if (!citationIds.Add(entry.CitationId))
            {
                throw new ArgumentException("Citation IDs must be unique in a snapshot.", nameof(transcript));
            }
            var intervals = entry.FinalEvent switch
            {
                FinalSegment final => final.Intervals,
                FinalAmendment amendment => amendment.Intervals,
                _ => throw new ArgumentException("Context evidence must be finalized.", nameof(transcript))
            };
            ValidateEvidence(request, entry.FinalEvent.Header.Source, intervals);
        }
        if (provisional is not null)
        {
            ValidateEvidence(request, provisional.Header.Source, [provisional.Interval]);
        }
        var omitted = omissions?.ToImmutableArray() ?? [];
        foreach (var omission in omitted) { ArgumentNullException.ThrowIfNull(omission); }

        Request = request;
        Generation = generation;
        Transcript = entries;
        Provisional = provisional;
        Omissions = omitted;
    }

    public AssistRequest Request { get; }
    public GenerationConfiguration Generation { get; }
    public ImmutableArray<TranscriptContextEntry> Transcript { get; }
    public PartialSegment? Provisional { get; }
    public ImmutableArray<ContextOmission> Omissions { get; }
    public ContractVersion Version => Request.Version;
    public AnswerFence Fence => Request.Fence;
    public TranscriptCommitVersion TranscriptVersion => Request.TranscriptCutoff;

    public override string ToString() => $"ContextSnapshot {{ RequestId = {Fence.RequestId} }}";

    private static void ValidateEvidence(AssistRequest request, SourceIdentity source,
        IEnumerable<TimelineInterval> intervals)
    {
        if (source.SessionId != request.Fence.SessionId)
        {
            throw new ArgumentException("Evidence must belong to the request session.", nameof(source));
        }
        foreach (var interval in intervals)
        {
            if (interval.TimelineEpoch != request.ContextStart.TimelineEpoch ||
                interval.Start.Offset.Value < request.ContextStart.Offset.Value)
            {
                throw new ArgumentException("Evidence must lie within the request context boundary.", nameof(intervals));
            }
        }
    }
}
