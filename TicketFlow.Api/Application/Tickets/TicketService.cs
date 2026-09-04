namespace TicketFlow.Api.Application.Tickets;

using TicketFlow.Api.Domain.Tickets;

public class TicketService(ITicketRepository ticketRepository)
{
    public Task<bool> CreateAsync(Ticket ticket, CancellationToken cancellationToken = default) =>
        ticketRepository.AddAsync(ticket, cancellationToken);

    public Task<Ticket?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
        ticketRepository.GetByIdAsync(id, cancellationToken);

    public Task<(IReadOnlyList<Ticket> Items, int Total)> ListAsync(
        TicketCategory? category,
        TicketPriority? priority,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        ticketRepository.ListAsync(category, priority, page, pageSize, cancellationToken);
}
