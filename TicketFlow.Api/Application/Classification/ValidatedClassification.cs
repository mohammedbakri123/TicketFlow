namespace TicketFlow.Api.Application.Classification;

using TicketFlow.Api.Domain.Tickets;

/// <summary>
/// Trusted, validated classification result. All fields have been verified
/// against the known category/priority values and the summary length
/// constraint. This type is safe to persist — unlike
/// <see cref="ClassificationCandidate"/>.
/// </summary>
public sealed record ValidatedClassification(
    TicketCategory Category,
    TicketPriority Priority,
    string Summary);
