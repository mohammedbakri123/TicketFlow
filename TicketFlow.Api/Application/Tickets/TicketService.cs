namespace TicketFlow.Api.Application.Tickets;

using Microsoft.EntityFrameworkCore;
using TicketFlow.Api.Domain.Tickets;
using TicketFlow.Api.Infrastructure.Persistence;

public class TicketService(TicketFlowDbContext db)
{
    /// <summary>
    /// Persists a new pending ticket. Returns true when a new row was created,
    /// false when a ticket with the same id already exists.
    /// The primary key on Ticket.Id is the final uniqueness guarantee: a
    /// concurrent insert with the same id loses the race and throws a
    /// duplicate-key exception, which is treated as an idempotent no-op
    /// (no second row, no second classification trigger).
    /// </summary>
    public async Task<bool> CreateAsync(Ticket ticket)
    {
        db.Tickets.Add(ticket);
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException)
        {
            db.Entry(ticket).State = EntityState.Detached;
            return false;
        }
    }

    public Task<Ticket?> GetByIdAsync(string id) =>
        db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);

    /// <summary>Filters and paginates tickets in the database query.</summary>
    public async Task<(IReadOnlyList<Ticket> Items, int Total)> ListAsync(
        TicketCategory? category,
        TicketPriority? priority,
        int page,
        int pageSize)
    {
        var query = db.Tickets.AsNoTracking();

        if (category is not null)
        {
            query = query.Where(t => t.Category == category);
        }

        if (priority is not null)
        {
            query = query.Where(t => t.Priority == priority);
        }

        var total = await query.CountAsync();

        var items = await query
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        return (items, total);
    }
}
