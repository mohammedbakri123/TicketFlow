namespace TicketFlow.Api.Application.Tickets;

using Microsoft.EntityFrameworkCore;
using Npgsql;
using TicketFlow.Api.Domain.Tickets;
using TicketFlow.Api.Infrastructure.Persistence;

public class TicketService(TicketFlowDbContext db)
{
    /// <summary>
    /// Persists a new pending ticket. Returns true when a new row was created,
    /// false when a ticket with the same id already exists.
    /// The primary key on Ticket.Id is the final uniqueness guarantee: a
    /// concurrent insert with the same id loses the race and fails with a
    /// unique violation, which is treated as an idempotent no-op (no second
    /// row, no second classification trigger). Any other database error
    /// propagates and results in a server error instead of a fake acceptance.
    /// </summary>
    public async Task<bool> CreateAsync(Ticket ticket)
    {
        db.Tickets.Add(ticket);
        try
        {
            await db.SaveChangesAsync();
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.Entry(ticket).State = EntityState.Detached;
            return false;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

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
