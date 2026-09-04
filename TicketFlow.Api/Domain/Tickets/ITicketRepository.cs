namespace TicketFlow.Api.Domain.Tickets;

public interface ITicketRepository
{
    Task<bool> AddAsync(Ticket ticket, CancellationToken cancellationToken = default);

    Task<Ticket?> GetByIdAsync(string id, CancellationToken cancellationToken = default);

    Task<(IReadOnlyList<Ticket> Items, int Total)> ListAsync(
        TicketCategory? category,
        TicketPriority? priority,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Finds pending tickets ordered by creation time and id.
    /// </summary>
    Task<IReadOnlyList<Ticket>> GetPendingTicketsAsync(CancellationToken cancellationToken = default);
}
