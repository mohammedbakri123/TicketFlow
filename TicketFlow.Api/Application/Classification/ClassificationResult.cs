namespace TicketFlow.Api.Application.Classification;

/// <summary>
/// Result of validating a <see cref="ClassificationCandidate"/>. Either the
/// candidate is valid and a <see cref="ValidatedClassification"/> is
/// available, or validation failed with one or more error descriptions.
/// This type never throws for invalid input — callers inspect
/// <see cref="IsValid"/> and branch accordingly.
/// </summary>
public sealed class ClassificationResult
{
    private ClassificationResult(
        ValidatedClassification? classification,
        IReadOnlyList<string> errors)
    {
        Classification = classification;
        Errors = errors;
    }

    public bool IsValid => Classification is not null;

    /// <summary>
    /// The trusted validated classification. Non-null only when
    /// <see cref="IsValid"/> is true.
    /// </summary>
    public ValidatedClassification? Classification { get; }

    /// <summary>
    /// Human-readable validation errors. Non-empty only when
    /// <see cref="IsValid"/> is false.
    /// </summary>
    public IReadOnlyList<string> Errors { get; }

    public static ClassificationResult Valid(ValidatedClassification classification)
        => new(classification, Array.Empty<string>());

    public static ClassificationResult Invalid(IReadOnlyList<string> errors)
        => new(null, errors);
}
