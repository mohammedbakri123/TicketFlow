namespace TicketFlow.Api.Application.Classification;

/// <summary>
/// Untrusted classification output produced by a model. Category and priority
/// are plain strings — not the <c>TicketCategory</c>/<c>TicketPriority</c>
/// enums — so invalid model output ("banana", "urgent") can be represented
/// without throwing. This type carries no validation and MUST NOT be
/// persisted as-is; validation happens in a separate step before the
/// validated values reach the database.
/// </summary>
public sealed record ClassificationCandidate(
    string? Category,
    string? Priority,
    string? Summary);
