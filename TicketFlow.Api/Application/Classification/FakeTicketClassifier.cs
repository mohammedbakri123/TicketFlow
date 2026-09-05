namespace TicketFlow.Api.Application.Classification;

using TicketFlow.Api.Domain.Tickets;

/// <summary>Behaviors the fake classifier can exhibit.</summary>
public enum FakeClassifierMode
{
    /// <summary>Picks a random behavior on each call to ClassifyAsync.</summary>
    Random,

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
/// Stand-in for a real LLM provider, used until a provider is integrated.
/// In <see cref="FakeClassifierMode.Random"/> mode (the default), it randomly exhibits
/// different behaviors on each call (valid, invalid category/priority, empty summary,
/// malformed, or throwing). Specific modes can also be selected for deterministic testing.
/// It deliberately does NOT "fix" invalid outputs: the model is an unreliable
/// dependency, and cleaning its output is the caller's (validator's) job.
/// </summary>
public sealed class FakeTicketClassifier(FakeClassifierMode mode = FakeClassifierMode.Random) : ITicketClassifier
{
    private static readonly FakeClassifierMode[] AvailableBehaviors =
    [
        FakeClassifierMode.Valid,
        FakeClassifierMode.InvalidCategory,
        FakeClassifierMode.InvalidPriority,
        FakeClassifierMode.EmptySummary,
        FakeClassifierMode.Malformed,
        FakeClassifierMode.Throw
    ];

    private static readonly string[] ValidCategories = ["billing", "technical", "account", "other"];
    private static readonly string[] ValidPriorities = ["low", "medium", "high"];
    private static readonly string[] ValidSummaries =
    [
        "The customer reports being charged twice.",
        "User unable to connect to the corporate VPN.",
        "Account password reset request for user.",
        "General inquiry regarding service billing cycle."
    ];

    /// <summary>Gets the configured behavior of this instance.</summary>
    public FakeClassifierMode Mode { get; } = mode;

    public Task<ClassificationCandidate> ClassifyAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        var effectiveMode = Mode == FakeClassifierMode.Random
            ? AvailableBehaviors[Random.Shared.Next(AvailableBehaviors.Length)]
            : Mode;

        if (effectiveMode == FakeClassifierMode.Valid && Mode == FakeClassifierMode.Random)
        {
            var category = ValidCategories[Random.Shared.Next(ValidCategories.Length)];
            var priority = ValidPriorities[Random.Shared.Next(ValidPriorities.Length)];
            var summary = ValidSummaries[Random.Shared.Next(ValidSummaries.Length)];
            return Task.FromResult(new ClassificationCandidate(category, priority, summary));
        }

        return Task.FromResult(effectiveMode switch
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
