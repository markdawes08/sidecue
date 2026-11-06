using Sidecue.Core.Contracts;

namespace Sidecue.Application.Abstractions;

public interface IContextAssembler
{
    /// <summary>Build a frozen snapshot at the request's exact cutoff, or fail explicitly.</summary>
    /// <remarks>Respect the clear boundary and token budgets. Pin the selected provider/model/configuration;
    /// never silently substitute a cutoff or provider. Cancellation abandons assembly.</remarks>
    Task<ContextSnapshot> BuildAsync(AssistRequest request, CancellationToken cancellationToken);
}

public interface IAssistanceProvider
{
    /// <summary>Generate against one immutable snapshot; every event preserves its answer fence.</summary>
    /// <remarks>One enumeration is one attempt. The adapter owns its transport and releases it on
    /// completion, failure, cancellation or enumerator disposal. Cancellation is cooperative;
    /// the coordinator still rejects stale output and supplies missing terminal state. No provider
    /// fallback is implicit. The snapshot has no screenshot or other disposable payload.</remarks>
    IAsyncEnumerable<AnswerEvent> GenerateAsync(ContextSnapshot snapshot, CancellationToken cancellationToken);
}
