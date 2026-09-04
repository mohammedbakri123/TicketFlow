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

    Task<IReadOnlyList<string>> GetPendingTicketIdsAsync(CancellationToken cancellationToken = default);
}
