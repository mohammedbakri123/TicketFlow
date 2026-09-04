namespace TicketFlow.Api.Application.Classification;

using TicketFlow.Api.Domain.Tickets;

/// <summary>
/// Validates an untrusted <see cref="ClassificationCandidate"/> and
/// produces a <see cref="ClassificationResult"/>. Validation is
/// deterministic and independent of any LLM / provider.
/// </summary>
public interface ITicketClassificationValidator
{
    ClassificationResult Validate(ClassificationCandidate candidate);
}

/// <summary>
/// Deterministic validator for <see cref="ClassificationCandidate"/>.
/// Category and priority are matched against explicit allow-lists (not
/// <see cref="Enum.TryParse{TEnum}(string?, out TEnum)"/>, which would
/// accept numeric strings and future enum members). Summary is checked for
/// presence, whitespace, and length only — semantic quality is not a
/// deterministic check.
/// </summary>
public sealed class TicketClassificationValidator : ITicketClassificationValidator
{
    /// <summary>
    /// Summary must be at most this many characters. Aligned with the
    /// database column constraint (1000 chars).
    /// </summary>
    internal const int MaxSummaryLength = 1000;

    /// <summary>
    /// Allowed category strings mapped to their enum values.
    /// Case-insensitive matching via <see cref="StringComparer.OrdinalIgnoreCase"/>.
    /// </summary>
    private static readonly Dictionary<string, TicketCategory> AllowedCategories =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["billing"] = TicketCategory.Billing,
            ["technical"] = TicketCategory.Technical,
            ["account"] = TicketCategory.Account,
            ["other"] = TicketCategory.Other,
        };

    /// <summary>
    /// Allowed priority strings mapped to their enum values.
    /// Case-insensitive matching via <see cref="StringComparer.OrdinalIgnoreCase"/>.
    /// </summary>
    private static readonly Dictionary<string, TicketPriority> AllowedPriorities =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["low"] = TicketPriority.Low,
            ["medium"] = TicketPriority.Medium,
            ["high"] = TicketPriority.High,
        };

    public ClassificationResult Validate(ClassificationCandidate candidate)
    {
        var errors = new List<string>();

        // ---- Category ----
        TicketCategory? category = null;
        if (candidate.Category is null)
        {
            errors.Add("Category is required.");
        }
        else if (!AllowedCategories.TryGetValue(candidate.Category, out var cat))
        {
            errors.Add($"Category '{candidate.Category}' is not a recognized value.");
        }
        else
        {
            category = cat;
        }

        // ---- Priority ----
        TicketPriority? priority = null;
        if (candidate.Priority is null)
        {
            errors.Add("Priority is required.");
        }
        else if (!AllowedPriorities.TryGetValue(candidate.Priority, out var pri))
        {
            errors.Add($"Priority '{candidate.Priority}' is not a recognized value.");
        }
        else
        {
            priority = pri;
        }

        // ---- Summary ----
        string? summary = null;
        if (string.IsNullOrWhiteSpace(candidate.Summary))
        {
            errors.Add("Summary is required and must not be empty or whitespace.");
        }
        else if (candidate.Summary.Length > MaxSummaryLength)
        {
            errors.Add($"Summary exceeds the maximum length of {MaxSummaryLength} characters.");
        }
        else
        {
            summary = candidate.Summary;
        }

        // ---- Result ----
        if (errors.Count > 0)
        {
            return ClassificationResult.Invalid(errors);
        }

        return ClassificationResult.Valid(
            new ValidatedClassification(category!.Value, priority!.Value, summary!));
    }
}
