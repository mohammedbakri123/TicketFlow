namespace TicketFlow.Api.Domain.Tickets;

using TicketFlow.Api.Application.Classification;

public interface ITicketRepository
{
    Task<bool> AddAsync(Ticket ticket, CancellationToken cancellationToken = default);

    Task<Ticket?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<Ticket> Items, int Total)> ListAsync(
        TicketStatus? status,
        TicketCategory? category,
        TicketPriority? priority,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds pending tickets ordered by creation time and id.
    /// </summary>
    Task<IReadOnlyList<Ticket>> GetPendingTicketsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically persists validated classification for a pending ticket and marks it Classified.
    /// Returns false if the ticket is not found or is no longer Pending.
    /// </summary>
    Task<bool> SaveClassificationAsync(
        string id,
        ValidatedClassification classification,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failed classification attempt by incrementing Attempts and updating UpdatedAt.
    /// Transitions Status to Failed once Attempts reaches 3; otherwise remains Pending.
    /// Returns false if the ticket is not found or is no longer Pending.
    /// </summary>
    Task<bool> RecordClassificationFailureAsync(
        string id,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reclassifies an existing ticket by resetting it to Pending status, resetting Attempts to 0,
    /// and clearing existing classification fields.
    /// Returns <see cref="ReclassifyTicketResult.NotFound"/> if the ticket does not exist,
    /// <see cref="ReclassifyTicketResult.AlreadyPending"/> if the ticket is already in Pending status,
    /// or <see cref="ReclassifyTicketResult.Requeued"/> if the ticket was successfully reset.
    /// </summary>
    Task<ReclassifyTicketResult> ReclassifyAsync(
        string id,
        CancellationToken cancellationToken = default);
}
