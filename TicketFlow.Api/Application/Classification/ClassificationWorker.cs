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
/// to produce a <see cref="ClassificationResult"/>.
/// Validated results are atomically persisted as Classified. For failed attempts
/// (due to classifier exceptions or invalid candidates), Attempts is incremented;
/// if Attempts reaches 3, the ticket transitions to Failed, otherwise it remains
/// Pending for future scan/retry.
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
        IReadOnlyList<Ticket> pendingTickets;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var ticketRepository = scope.ServiceProvider.GetRequiredService<ITicketRepository>();

            pendingTickets = await ticketRepository.GetPendingTicketsAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            // A transient database failure must not crash the worker or the host.
            logger.LogError(ex, "Worker failed to fetch pending tickets.");
            return;
        }

        if (pendingTickets.Count == 0)
        {
            logger.LogDebug("Worker found no pending tickets.");
            return;
        }

        logger.LogInformation("Worker found {Count} pending tickets.", pendingTickets.Count);

        try
        {
            // Process tickets concurrently with bounded parallelism. The
            // pending tickets were fetched with a single completed query on
            // one scoped DbContext; each parallel ticket classification creates
            // its own DI scope and DbContext, so no DbContext is shared across
            // parallel operations.
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
    }

    /// <summary>
    /// Unit of work for a single ticket: passes it to the classifier, validates the candidate,
    /// and persists either the trusted classification or records an attempt failure.
    /// Each parallel operation creates a dedicated async DI scope for its repository/DbContext.
    /// Only ticket ids and non-sensitive statuses are logged — never ticket bodies or model summaries.
    /// </summary>
    private async ValueTask ProcessTicketAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        logger.LogInformation("Worker found pending ticket {TicketId}.", ticket.Id);

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var ticketRepository = scope.ServiceProvider.GetRequiredService<ITicketRepository>();

            ClassificationResult? result = null;
            Exception? classifierException = null;

            try
            {
                var candidate = await classifier.ClassifyAsync(ticket, cancellationToken);

                logger.LogInformation(
                    "Worker produced classification candidate for ticket {TicketId}.",
                    ticket.Id);

                result = validator.Validate(candidate);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                classifierException = ex;
                logger.LogError(ex, "Classification failed for ticket {TicketId}.", ticket.Id);
            }

            if (classifierException is null && result is { IsValid: true, Classification: not null })
            {
                logger.LogInformation(
                    "Classification candidate for ticket {TicketId} passed validation.",
                    ticket.Id);

                await ticketRepository.SaveClassificationAsync(ticket.Id, result.Classification, cancellationToken);
            }
            else
            {
                if (result is { IsValid: false })
                {
                    logger.LogWarning(
                        "Classification candidate for ticket {TicketId} failed validation: {Errors}",
                        ticket.Id,
                        string.Join("; ", result.Errors));
                }

                await ticketRepository.RecordClassificationFailureAsync(ticket.Id, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // One bad ticket or unexpected persistence failure must not stop the
            // worker or the remaining pending tickets.
            logger.LogError(ex, "Failed to process ticket {TicketId}.", ticket.Id);
        }
    }
}
