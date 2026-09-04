namespace TicketFlow.Api.Application.Classification;

using Microsoft.EntityFrameworkCore;
using TicketFlow.Api.Application.BackgroundWork;
using TicketFlow.Api.Domain.Tickets;
using TicketFlow.Api.Infrastructure.Persistence;

/// <summary>
/// Background worker for ticket classification. Runs in the same process as
/// the HTTP API. It is woken by <see cref="ITicketWorkSignal"/> when new work
/// may exist, then queries PostgreSQL for pending tickets. On startup it
/// performs a recovery scan so tickets persisted before a crash or restart
/// are picked up even if their signal was lost. In this step no classification
/// happens yet: pending tickets are only identified and logged.
/// </summary>
public sealed class ClassificationWorker(
    IServiceScopeFactory scopeFactory,
    ITicketWorkSignal workSignal,
    ILogger<ClassificationWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Ticket classification worker started.");

        // Startup recovery: PostgreSQL is the source of truth, not the
        // in-memory signal. Tickets saved as pending before a crash are
        // still there when the application restarts.
        await ScanPendingTicketsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await workSignal.WaitForSignalAsync(stoppingToken);
            await ScanPendingTicketsAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Finds pending tickets and logs that they are available for processing.
    /// Only ticket ids are read and logged — never ticket bodies, because
    /// ticket content is untrusted and may contain sensitive customer data.
    /// </summary>
    internal async Task ScanPendingTicketsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<TicketFlowDbContext>();

            var pendingIds = await db.Tickets
                .AsNoTracking()
                .Where(t => t.Status == TicketStatus.Pending)
                .OrderBy(t => t.CreatedAt)
                .ThenBy(t => t.Id)
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

            if (pendingIds.Count == 0)
            {
                logger.LogDebug("Worker found no pending tickets.");
                return;
            }

            logger.LogInformation("Worker found {Count} pending tickets.", pendingIds.Count);

            foreach (var id in pendingIds)
            {
                logger.LogInformation("Worker found pending ticket {TicketId}.", id);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown.
        }
        catch (Exception ex)
        {
            // A transient database failure must not crash the worker or the host.
            logger.LogError(ex, "Worker failed to scan pending tickets.");
        }
    }
}
