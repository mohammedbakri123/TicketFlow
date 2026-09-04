namespace TicketFlow.Api.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TicketFlow.Api.Domain.Tickets;
using TicketFlow.Api.Infrastructure.Persistence;

public class TicketRepository(TicketFlowDbContext db, ILogger<TicketRepository> logger) : ITicketRepository
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
    public async Task<bool> AddAsync(Ticket ticket, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        db.Tickets.Add(ticket);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            db.Entry(ticket).State = EntityState.Detached;
            logger.LogInformation("Ticket submission with ID '{TicketId}' already exists. Treated as idempotent no-op.", ticket.Id);
            return false;
        }
        catch (Exception ex)
        {
            db.Entry(ticket).State = EntityState.Detached;
            logger.LogError(ex, "Unexpected error occurred while creating ticket with ID '{TicketId}'.", ticket.Id);
            throw;
        }
    }

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation }
        || ex.GetBaseException() is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    public Task<Ticket?> GetByIdAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return Task.FromResult<Ticket?>(null);
        }

        return db.Tickets.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
    }

    /// <summary>Filters and paginates tickets in the database query.</summary>
    public async Task<(IReadOnlyList<Ticket> Items, int Total)> ListAsync(
        TicketCategory? category,
        TicketPriority? priority,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        if (page < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(page), "page must be greater than or equal to 1.");
        }

        if (pageSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(pageSize), "pageSize must be greater than or equal to 1.");
        }

        var query = db.Tickets.AsNoTracking();

        if (category is not null)
        {
            query = query.Where(t => t.Category == category);
        }

        if (priority is not null)
        {
            query = query.Where(t => t.Priority == priority);
        }

        var total = await query.CountAsync(cancellationToken);
        if (total == 0)
        {
            return (Array.Empty<Ticket>(), 0);
        }

        var items = await query
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return (items, total);
    }

    /// <summary>
    /// Finds ids of pending tickets ordered by creation time and id.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetPendingTicketIdsAsync(CancellationToken cancellationToken = default)
    {
        return await db.Tickets
            .AsNoTracking()
            .Where(t => t.Status == TicketStatus.Pending)
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .Select(t => t.Id)
            .ToListAsync(cancellationToken);
    }
}
