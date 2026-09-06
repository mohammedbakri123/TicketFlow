namespace TicketFlow.Api.Infrastructure.Persistence.Repositories;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using TicketFlow.Api.Application.Classification;
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
    /// Finds pending tickets ordered by creation time and id.
    /// </summary>
    public async Task<IReadOnlyList<Ticket>> GetPendingTicketsAsync(CancellationToken cancellationToken = default)
    {
        return await db.Tickets
            .AsNoTracking()
            .Where(t => t.Status == TicketStatus.Pending)
            .OrderBy(t => t.CreatedAt)
            .ThenBy(t => t.Id)
            .ToListAsync(cancellationToken);
    }

    /// <summary>
    /// Atomically persists validated classification for a pending ticket and marks it Classified.
    /// If the ticket is not found or is no longer Pending (stale work), returns false without overwriting.
    /// </summary>
    public async Task<bool> SaveClassificationAsync(
        string id,
        ValidatedClassification classification,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(classification);

        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            logger.LogWarning("Cannot save classification: ticket '{TicketId}' was not found.", id);
            return false;
        }

        if (ticket.Status != TicketStatus.Pending)
        {
            logger.LogWarning(
                "Cannot save classification: ticket '{TicketId}' is not in Pending status (current status: {Status}). Stale update ignored.",
                id,
                ticket.Status);
            return false;
        }

        ticket.Category = classification.Category;
        ticket.Priority = classification.Priority;
        ticket.Summary = classification.Summary;
        ticket.Status = TicketStatus.Classified;
        ticket.Attempts += 1;
        ticket.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Ticket '{TicketId}' classified successfully (Category: {Category}, Priority: {Priority}, Attempts: {Attempts}).",
            id,
            ticket.Category,
            ticket.Priority,
            ticket.Attempts);
        return true;
    }

    /// <summary>
    /// Records a failed classification attempt by incrementing Attempts and updating UpdatedAt.
    /// Transitions Status to Failed once Attempts reaches 3; otherwise remains Pending.
    /// If the ticket is not found or is no longer Pending (stale work), returns false without overwriting.
    /// Model outputs are never persisted.
    /// </summary>
    public async Task<bool> RecordClassificationFailureAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            logger.LogWarning("Cannot record classification failure: ticket '{TicketId}' was not found.", id);
            return false;
        }

        if (ticket.Status != TicketStatus.Pending)
        {
            logger.LogWarning(
                "Cannot record classification failure: ticket '{TicketId}' is not in Pending status (current status: {Status}). Stale update ignored.",
                id,
                ticket.Status);
            return false;
        }

        ticket.Attempts += 1;
        if (ticket.Attempts >= 3)
        {
            ticket.Status = TicketStatus.Failed;
        }
        ticket.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation(
            "Recorded classification failure for ticket '{TicketId}'. Attempts: {Attempts}, Status: {Status}.",
            id,
            ticket.Attempts,
            ticket.Status);
        return true;
    }

    /// <summary>
    /// Reclassifies an existing ticket by resetting it to Pending status, resetting Attempts to 0,
    /// and clearing existing classification fields.
    /// Returns NotFound if not found, AlreadyPending if already Pending, or Requeued on success.
    /// </summary>
    public async Task<ReclassifyTicketResult> ReclassifyAsync(
        string id,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        var ticket = await db.Tickets.FirstOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            logger.LogWarning("Cannot reclassify: ticket '{TicketId}' was not found.", id);
            return ReclassifyTicketResult.NotFound;
        }

        if (ticket.Status == TicketStatus.Pending)
        {
            logger.LogWarning("Cannot reclassify: ticket '{TicketId}' is already in Pending status.", id);
            return ReclassifyTicketResult.AlreadyPending;
        }

        ticket.Status = TicketStatus.Pending;
        ticket.Category = null;
        ticket.Priority = null;
        ticket.Summary = null;
        ticket.Attempts = 0;
        ticket.UpdatedAt = DateTime.UtcNow;

        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Ticket '{TicketId}' reset to Pending for reclassification.", id);
        return ReclassifyTicketResult.Requeued;
    }
}
