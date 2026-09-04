namespace TicketFlow.Api.Application.Classification;

using TicketFlow.Api.Application.BackgroundWork;
using TicketFlow.Api.Domain.Tickets;

/// <summary>
/// Background worker for ticket classification. Runs in the same process as
/// the HTTP API. It is woken by <see cref="ITicketWorkSignal"/> when new work
/// may exist, then queries the repository for pending tickets. On startup it
/// performs a recovery scan so tickets persisted before a crash or restart
/// are picked up even if their signal was lost.
///
/// For each pending ticket the worker calls <see cref="ITicketClassifier"/>
/// and receives an untrusted <see cref="ClassificationCandidate"/>. The
/// candidate is then validated by <see cref="ITicketClassificationValidator"/>
/// to produce a <see cref="ClassificationResult"/>. Neither the untrusted
/// candidate nor the validated result is persisted in this step: status
/// transitions (Classified / Failed) and attempt counting are intentionally
/// not touched yet.
/// </summary>
public sealed class ClassificationWorker(
    IServiceScopeFactory scopeFactory,
    ITicketWorkSignal workSignal,
    ITicketClassifier classifier,
    ITicketClassificationValidator validator,
    ILogger<ClassificationWorker> logger) : BackgroundService
{
    /// <summary>Bounds concurrent classifications to keep provider load predictable.</summary>
    private const int MaxDegreeOfParallelism = 4;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Ticket classification worker started.");

        // Run this once on startup to recover any pending tickets that were
        // persisted before a crash or restart, even if their signal was lost.
        await ScanPendingTicketsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            await workSignal.WaitForSignalAsync(stoppingToken);
            await ScanPendingTicketsAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Finds pending tickets and passes each one to the classifier. Only
    /// ticket ids are logged — never ticket bodies or model summaries, because
    /// ticket content is untrusted and may contain sensitive customer data.
    /// A classifier failure for one ticket is logged and does not stop the
    /// worker from processing the remaining tickets.
    /// </summary>
    internal async Task ScanPendingTicketsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var ticketRepository = scope.ServiceProvider.GetRequiredService<ITicketRepository>();

            var pendingTickets = await ticketRepository.GetPendingTicketsAsync(cancellationToken);

            if (pendingTickets.Count == 0)
            {
                logger.LogDebug("Worker found no pending tickets.");
                return;
            }

            logger.LogInformation("Worker found {Count} pending tickets.", pendingTickets.Count);

            // Process tickets concurrently with bounded parallelism. The
            // pending tickets were fetched with a single completed query on
            // one scoped DbContext; the per-ticket work below touches only
            // the classifier, so no DbContext is shared between parallel
            // operations. (When persistence is added, each ticket will need
            // its own scope — that belongs to the persistence step.)
            await Parallel.ForEachAsync(
                pendingTickets,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = MaxDegreeOfParallelism,
                    CancellationToken = cancellationToken
                },
                (ticket, token) => ProcessTicketAsync(ticket, token));
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

    /// <summary>
    /// Unit of work for a single ticket: passes it to the classifier and logs
    /// the result. Only ticket ids are logged — never ticket bodies or model
    /// summaries, because ticket content is untrusted and may contain
    /// sensitive customer data. A classifier failure for one ticket is logged
    /// and does not stop the worker from processing the other tickets.
    /// </summary>
    private async ValueTask ProcessTicketAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        logger.LogInformation("Worker found pending ticket {TicketId}.", ticket.Id);

        try
        {
            var candidate = await classifier.ClassifyAsync(ticket, cancellationToken);

            logger.LogInformation(
                "Worker produced classification candidate for ticket {TicketId}.",
                ticket.Id);

            var result = validator.Validate(candidate);

            if (result.IsValid)
            {
                logger.LogInformation(
                    "Classification candidate for ticket {TicketId} passed validation.",
                    ticket.Id);

                // Validated result is available as result.Classification
                // (a ValidatedClassification with TicketCategory, TicketPriority, string).
                // Persistence will be added in the next step.
            }
            else
            {
                logger.LogWarning(
                    "Classification candidate for ticket {TicketId} failed validation: {Errors}",
                    ticket.Id,
                    string.Join("; ", result.Errors));

                // Retry / failure handling will be added in the next step.
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad ticket (or provider failure) must not stop the
            // worker or the remaining pending tickets.
            logger.LogError(ex, "Classification failed for ticket {TicketId}.", ticket.Id);
        }
    }
}
