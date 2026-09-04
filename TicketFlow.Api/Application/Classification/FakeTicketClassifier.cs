namespace TicketFlow.Api.Application.Classification;

using TicketFlow.Api.Domain.Tickets;

/// <summary>Explicit, deterministic behaviors the fake classifier can exhibit.</summary>
public enum FakeClassifierMode
{
    /// <summary>Plausible, well-formed output.</summary>
    Valid,

    /// <summary>Category that is not a known TicketCategory value.</summary>
    InvalidCategory,

    /// <summary>Priority that is not a known TicketPriority value.</summary>
    InvalidPriority,

    /// <summary>Well-formed category/priority but an empty summary.</summary>
    EmptySummary,

    /// <summary>Output that could not be shaped into any field at all.</summary>
    Malformed,

    /// <summary>Simulates a provider outage by throwing.</summary>
    Throw
}

/// <summary>
/// Deterministic stand-in for a real LLM provider, used until a provider is
/// integrated. The selected behavior is fixed per instance — no randomness.
/// It deliberately does NOT "fix" invalid outputs: the model is an unreliable
/// dependency, and cleaning its output is the caller's (validator's) job.
/// </summary>
public sealed class FakeTicketClassifier(FakeClassifierMode mode = FakeClassifierMode.Valid) : ITicketClassifier
{
    /// <summary>Gets the fixed behavior of this instance.</summary>
    public FakeClassifierMode Mode { get; } = mode;

    public Task<ClassificationCandidate> ClassifyAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        return Task.FromResult(Mode switch
        {
            FakeClassifierMode.Valid => new ClassificationCandidate(
                "billing", "high", "The customer reports being charged twice."),
            FakeClassifierMode.InvalidCategory => new ClassificationCandidate(
                "banana", "high", "The customer reports being charged twice."),
            FakeClassifierMode.InvalidPriority => new ClassificationCandidate(
                "billing", "urgent", "The customer reports being charged twice."),
            FakeClassifierMode.EmptySummary => new ClassificationCandidate(
                "billing", "high", string.Empty),
            // e.g. the model returned prose or unusable output and nothing
            // could be extracted into a field — not silently corrected.
            FakeClassifierMode.Malformed => new ClassificationCandidate(
                null, null, null),
            FakeClassifierMode.Throw => throw new InvalidOperationException(
                "Simulated classifier failure."),
            _ => throw new ArgumentOutOfRangeException(nameof(mode), Mode, "Unknown fake classifier mode.")
        });
    }
}
