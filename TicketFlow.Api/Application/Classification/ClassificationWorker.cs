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
/// When pending tickets remain after a scan, the worker automatically re-scans after
/// <see cref="RetryInterval"/> or immediately when a new work signal is received.
/// When no pending tickets remain, the worker waits indefinitely for a signal.
/// </summary>
public sealed class ClassificationWorker(
    IServiceScopeFactory scopeFactory,
    ITicketWorkSignal workSignal,
    ITicketClassifier classifier,
    ITicketClassificationValidator validator,
    ILogger<ClassificationWorker> logger,
    TimeSpan retryInterval)
    : BackgroundService
{
    /// <summary>Bounds concurrent classifications to keep provider load predictable.</summary>
    private const int MaxDegreeOfParallelism = 4;

    /// <summary>Default retry interval when pending tickets remain after a scan.</summary>
    public static readonly TimeSpan DefaultRetryInterval = TimeSpan.FromSeconds(5);

    public ClassificationWorker(
        IServiceScopeFactory scopeFactory,
        ITicketWorkSignal workSignal,
        ITicketClassifier classifier,
        ITicketClassificationValidator validator,
        ILogger<ClassificationWorker> logger)
        : this(scopeFactory, workSignal, classifier, validator, logger, DefaultRetryInterval)
    {
    }

    public TimeSpan RetryInterval => retryInterval;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        logger.LogInformation("Ticket classification worker started.");

        // Run this once on startup to recover any pending tickets that were
        // persisted before a crash or restart, even if their signal was lost.
        var hasPendingTickets = await ScanPendingTicketsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            if (hasPendingTickets)
            {
                using var timeoutCts = new CancellationTokenSource(retryInterval);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, timeoutCts.Token);

                try
                {
                    await workSignal.WaitForSignalAsync(linkedCts.Token);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !stoppingToken.IsCancellationRequested)
                {
                    // RetryInterval elapsed without a signal; proceed with retry scan.
                }
            }
            else
            {
                // No pending tickets in progress; wait indefinitely for a new work signal.
                await workSignal.WaitForSignalAsync(stoppingToken);
            }

            hasPendingTickets = await ScanPendingTicketsAsync(stoppingToken);
        }
    }

    /// <summary>
    /// Finds pending tickets and passes each one to the classifier. Only
    /// ticket ids are logged — never ticket bodies or model summaries, because
    /// ticket content is untrusted and may contain sensitive customer data.
    /// A classifier failure for one ticket is logged and does not stop the
    /// worker from processing the remaining tickets.
    /// Returns true if any tickets remain in Pending status after the scan; otherwise false.
    /// </summary>
    internal async Task<bool> ScanPendingTicketsAsync(CancellationToken cancellationToken)
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
            return false;
        }
        catch (Exception ex)
        {
            // A transient database failure must not crash the worker or the host.
            logger.LogError(ex, "Worker failed to fetch pending tickets.");
            return true;
        }

        if (pendingTickets.Count == 0)
        {
            logger.LogDebug("Worker found no pending tickets.");
            return false;
        }

        logger.LogInformation("Worker found {Count} pending tickets.", pendingTickets.Count);

        var pendingRemaining = 0;

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
                async (ticket, token) =>
                {
                    var stillPending = await ProcessTicketAsync(ticket, token);
                    if (stillPending)
                    {
                        Interlocked.Increment(ref pendingRemaining);
                    }
                });
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during shutdown.
            return false;
        }

        return pendingRemaining > 0;
    }

    /// <summary>
    /// Unit of work for a single ticket: passes it to the classifier, validates the candidate,
    /// and persists either the trusted classification or records an attempt failure.
    /// Each parallel operation creates a dedicated async DI scope for its repository/DbContext.
    /// Only ticket ids and non-sensitive statuses are logged — never ticket bodies or model summaries.
    /// Returns true if the ticket remains in Pending status after processing; false if it transitioned
    /// to Classified or Failed.
    /// </summary>
    private async ValueTask<bool> ProcessTicketAsync(Ticket ticket, CancellationToken cancellationToken)
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
                return false;
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
                return ticket.Attempts + 1 < 3;
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
            return true;
        }
    }
}
