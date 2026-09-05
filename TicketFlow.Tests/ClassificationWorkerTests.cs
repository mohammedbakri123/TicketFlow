using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TicketFlow.Api.Application.BackgroundWork;
using TicketFlow.Api.Application.Classification;
using TicketFlow.Api.Domain.Tickets;
using TicketFlow.Api.Infrastructure.Persistence;
using TicketFlow.Api.Infrastructure.Persistence.Repositories;

namespace TicketFlow.Tests;

public class ClassificationWorkerTests
{
    // ---- Test doubles (tiny, no mocking framework) ----

    /// <summary>Stub classifier that records the tickets it receives and can be told to fail for specific ids.</summary>
    private sealed class StubTicketClassifier : ITicketClassifier
    {
        private readonly object gate = new();

        public List<Ticket> ClassifiedTickets { get; } = [];

        public HashSet<string> FailForTicketIds { get; } = new();

        public Task<ClassificationCandidate> ClassifyAsync(Ticket ticket, CancellationToken cancellationToken = default)
        {
            lock (gate)
            {
                ClassifiedTickets.Add(ticket);
            }

            if (FailForTicketIds.Contains(ticket.Id))
            {
                throw new InvalidOperationException("Simulated classifier failure.");
            }

            return Task.FromResult(new ClassificationCandidate(
                "billing", "high", "The customer reports being charged twice."));
        }
    }

    private sealed class CustomCandidateClassifier(ClassificationCandidate candidate) : ITicketClassifier
    {
        public Task<ClassificationCandidate> ClassifyAsync(Ticket ticket, CancellationToken cancellationToken = default)
            => Task.FromResult(candidate);
    }

    private sealed class FlakyTicketClassifier(int failFirstNCalls = 1) : ITicketClassifier
    {
        private int _calls;
        public int Calls => _calls;

        public Task<ClassificationCandidate> ClassifyAsync(Ticket ticket, CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _calls);
            if (call <= failFirstNCalls)
            {
                throw new InvalidOperationException("Simulated transient error.");
            }

            return Task.FromResult(new ClassificationCandidate(
                "billing", "high", "The customer reports being charged twice."));
        }
    }

    private sealed class TestLogger : ILogger<ClassificationWorker>
    {
        private readonly object gate = new();

        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (gate)
            {
                Entries.Add((logLevel, formatter(state, exception), exception));
            }
        }
    }

    // ---- Helpers ----

    private static (IServiceScopeFactory ScopeFactory, StubTicketClassifier Classifier, TestLogger Logger) CreateWorkerDependencies()
    {
        // Fixed database name shared by every context created from this
        // provider, so seeded data is visible across scopes.
        var databaseName = Guid.NewGuid().ToString();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<TicketFlowDbContext>(options => options.UseInMemoryDatabase(databaseName));
        services.AddScoped<ITicketRepository, TicketRepository>();

        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();

        return (scopeFactory, new StubTicketClassifier(), new TestLogger());
    }

    private static ClassificationWorker CreateWorker(
        IServiceScopeFactory scopeFactory,
        ITicketClassifier classifier,
        TestLogger logger,
        ITicketClassificationValidator? validator = null,
        ITicketWorkSignal? workSignal = null,
        TimeSpan? retryInterval = null)
    {
        var signal = workSignal ?? new ChannelTicketWorkSignal();
        var val = validator ?? new TicketClassificationValidator();
        return retryInterval.HasValue
            ? new ClassificationWorker(scopeFactory, signal, classifier, val, logger, retryInterval.Value)
            : new ClassificationWorker(scopeFactory, signal, classifier, val, logger);
    }

    private static async Task SeedTicketsAsync(IServiceScopeFactory scopeFactory, params Ticket[] tickets)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketFlowDbContext>();
        db.Tickets.AddRange(tickets);
        await db.SaveChangesAsync();
    }

    private static async Task<Ticket> GetTicketAsync(IServiceScopeFactory scopeFactory, string id)
    {
        using var scope = scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TicketFlowDbContext>();
        return await db.Tickets.SingleAsync(t => t.Id == id);
    }

    // ---- Tests ----

    [Fact]
    public async Task Scan_PassesPendingTicketsToClassifier()
    {
        var (scopeFactory, classifier, logger) = CreateWorkerDependencies();
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-1001", Subject = "Double charge", Body = "b" });

        await CreateWorker(scopeFactory, classifier, logger).ScanPendingTicketsAsync(CancellationToken.None);

        var passed = Assert.Single(classifier.ClassifiedTickets);
        Assert.Equal("t-1001", passed.Id);
        Assert.Equal("Double charge", passed.Subject);
    }

    [Fact]
    public async Task Scan_UsesClassifierAbstraction_NotFakeTicketClassifierDirectly()
    {
        // The stub is our own ITicketClassifier implementation, NOT the
        // FakeTicketClassifier. The worker invoking the stub proves it depends
        // only on the abstraction.
        var (scopeFactory, classifier, logger) = CreateWorkerDependencies();
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-1001", Subject = "s", Body = "b" });

        await CreateWorker(scopeFactory, classifier, logger).ScanPendingTicketsAsync(CancellationToken.None);

        Assert.Single(classifier.ClassifiedTickets);
    }

    [Fact]
    public async Task Scan_ClassifierException_DoesNotTerminateWorker_ProcessesRemainingTickets()
    {
        var (scopeFactory, classifier, logger) = CreateWorkerDependencies();
        classifier.FailForTicketIds.Add("t-1001");
        await SeedTicketsAsync(
            scopeFactory,
            new Ticket { Id = "t-1001", Subject = "s1", Body = "b1" },
            new Ticket { Id = "t-1002", Subject = "s2", Body = "b2" });

        // Must not throw even though the classifier throws for t-1001.
        await CreateWorker(scopeFactory, classifier, logger).ScanPendingTicketsAsync(CancellationToken.None);

        // Both tickets were processed; order is nondeterministic under
        // bounded parallelism.
        Assert.Equal(
            new[] { "t-1001", "t-1002" },
            classifier.ClassifiedTickets.Select(t => t.Id).Order());

        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("t-1001", error.Message);
    }

    [Fact]
    public async Task Scan_ValidClassification_PersistsFieldsAndMarksClassified()
    {
        var (scopeFactory, classifier, logger) = CreateWorkerDependencies();
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-1001", Subject = "s", Body = "b", Attempts = 0 });

        await CreateWorker(scopeFactory, classifier, logger).ScanPendingTicketsAsync(CancellationToken.None);

        var ticket = await GetTicketAsync(scopeFactory, "t-1001");
        Assert.Equal(TicketStatus.Classified, ticket.Status);
        Assert.Equal(TicketCategory.Billing, ticket.Category);
        Assert.Equal(TicketPriority.High, ticket.Priority);
        Assert.Equal("The customer reports being charged twice.", ticket.Summary);
        Assert.Equal(1, ticket.Attempts);
        Assert.True(ticket.UpdatedAt >= ticket.CreatedAt);
    }

    /// <summary>
    /// Classifier that only completes a classification once all four tickets
    /// are inside it at the same time. If the worker processed tickets
    /// sequentially — or with a lower degree of parallelism than the four
    /// seeded tickets — the barrier never opens and the stub times out,
    /// failing the test deterministically instead of hanging.
    /// </summary>
    private sealed class BarrierClassifier : ITicketClassifier
    {
        private readonly TaskCompletionSource allEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int entered;

        public List<string> ProcessedTicketIds { get; } = [];

        public async Task<ClassificationCandidate> ClassifyAsync(Ticket ticket, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref entered) == 4)
            {
                allEntered.TrySetResult();
            }

            // Hold every in-flight classification open until all four are
            // concurrently inside the classifier.
            await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

            lock (ProcessedTicketIds)
            {
                ProcessedTicketIds.Add(ticket.Id);
            }

            return new ClassificationCandidate("billing", "high", "s");
        }
    }

    [Fact]
    public async Task Scan_ProcessesPendingTicketsConcurrently()
    {
        var (scopeFactory, _, logger) = CreateWorkerDependencies();
        var classifier = new BarrierClassifier();
        await SeedTicketsAsync(
            scopeFactory,
            new Ticket { Id = "t-1001", Subject = "s", Body = "b" },
            new Ticket { Id = "t-1002", Subject = "s", Body = "b" },
            new Ticket { Id = "t-1003", Subject = "s", Body = "b" },
            new Ticket { Id = "t-1004", Subject = "s", Body = "b" });

        await CreateWorker(scopeFactory, classifier, logger).ScanPendingTicketsAsync(CancellationToken.None);

        // All four tickets were processed, which is only possible if the scan
        // ran them concurrently (bounded by MaxDegreeOfParallelism = 4).
        Assert.Equal(4, classifier.ProcessedTicketIds.Count);
    }

    // ---- Persistence and Retry/Failure Integration ----

    [Fact]
    public async Task Scan_InvalidCategory_IncrementsAttempts_RemainsPending_NoFieldsPersisted()
    {
        var (scopeFactory, _, logger) = CreateWorkerDependencies();
        var classifier = new FakeTicketClassifier(FakeClassifierMode.InvalidCategory);
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-1001", Subject = "s", Body = "b", Attempts = 0 });

        await CreateWorker(scopeFactory, classifier, logger).ScanPendingTicketsAsync(CancellationToken.None);

        var ticket = await GetTicketAsync(scopeFactory, "t-1001");
        Assert.Equal(TicketStatus.Pending, ticket.Status);
        Assert.Equal(1, ticket.Attempts);
        Assert.Null(ticket.Category);
        Assert.Null(ticket.Priority);
        Assert.Null(ticket.Summary);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("t-1001"));
    }

    [Fact]
    public async Task Scan_InvalidPriority_IncrementsAttempts_RemainsPending_NoFieldsPersisted()
    {
        var (scopeFactory, _, logger) = CreateWorkerDependencies();
        var classifier = new FakeTicketClassifier(FakeClassifierMode.InvalidPriority);
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-1001", Subject = "s", Body = "b", Attempts = 0 });

        await CreateWorker(scopeFactory, classifier, logger).ScanPendingTicketsAsync(CancellationToken.None);

        var ticket = await GetTicketAsync(scopeFactory, "t-1001");
        Assert.Equal(TicketStatus.Pending, ticket.Status);
        Assert.Equal(1, ticket.Attempts);
        Assert.Null(ticket.Category);
        Assert.Null(ticket.Priority);
        Assert.Null(ticket.Summary);
    }

    [Fact]
    public async Task Scan_EmptySummary_IncrementsAttempts_RemainsPending_NoFieldsPersisted()
    {
        var (scopeFactory, _, logger) = CreateWorkerDependencies();
        var classifier = new FakeTicketClassifier(FakeClassifierMode.EmptySummary);
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-1001", Subject = "s", Body = "b", Attempts = 0 });

        await CreateWorker(scopeFactory, classifier, logger).ScanPendingTicketsAsync(CancellationToken.None);

        var ticket = await GetTicketAsync(scopeFactory, "t-1001");
        Assert.Equal(TicketStatus.Pending, ticket.Status);
        Assert.Equal(1, ticket.Attempts);
        Assert.Null(ticket.Category);
        Assert.Null(ticket.Priority);
        Assert.Null(ticket.Summary);
    }

    [Fact]
    public async Task Scan_MalformedCandidate_IncrementsAttempts_RemainsPending_NoFieldsPersisted()
    {
        var (scopeFactory, _, logger) = CreateWorkerDependencies();
        var classifier = new FakeTicketClassifier(FakeClassifierMode.Malformed);
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-1001", Subject = "s", Body = "b", Attempts = 0 });

        await CreateWorker(scopeFactory, classifier, logger).ScanPendingTicketsAsync(CancellationToken.None);

        var ticket = await GetTicketAsync(scopeFactory, "t-1001");
        Assert.Equal(TicketStatus.Pending, ticket.Status);
        Assert.Equal(1, ticket.Attempts);
        Assert.Null(ticket.Category);
        Assert.Null(ticket.Priority);
        Assert.Null(ticket.Summary);
    }

    [Fact]
    public async Task Scan_ClassifierException_IncrementsAttempts_RemainsPending_NoFieldsPersisted()
    {
        var (scopeFactory, _, logger) = CreateWorkerDependencies();
        var classifier = new FakeTicketClassifier(FakeClassifierMode.Throw);
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-1001", Subject = "s", Body = "b", Attempts = 0 });

        await CreateWorker(scopeFactory, classifier, logger).ScanPendingTicketsAsync(CancellationToken.None);

        var ticket = await GetTicketAsync(scopeFactory, "t-1001");
        Assert.Equal(TicketStatus.Pending, ticket.Status);
        Assert.Equal(1, ticket.Attempts);
        Assert.Null(ticket.Category);
        Assert.Null(ticket.Priority);
        Assert.Null(ticket.Summary);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("t-1001"));
    }

    [Fact]
    public async Task Scan_ThirdFailedAttempt_TransitionsStatusToFailed()
    {
        var (scopeFactory, _, logger) = CreateWorkerDependencies();
        var classifier = new FakeTicketClassifier(FakeClassifierMode.InvalidCategory);
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-1001", Subject = "s", Body = "b", Attempts = 2, Status = TicketStatus.Pending });

        await CreateWorker(scopeFactory, classifier, logger).ScanPendingTicketsAsync(CancellationToken.None);

        var ticket = await GetTicketAsync(scopeFactory, "t-1001");
        Assert.Equal(TicketStatus.Failed, ticket.Status);
        Assert.Equal(3, ticket.Attempts);
        Assert.Null(ticket.Category);
        Assert.Null(ticket.Priority);
        Assert.Null(ticket.Summary);
    }

    [Fact]
    public async Task Scan_SuccessfulRetryAfterPreviousFailure_PersistsClassificationAndMarksClassified()
    {
        var (scopeFactory, classifier, logger) = CreateWorkerDependencies();
        // Previous attempt failed, so Attempts starts at 1
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-1001", Subject = "s", Body = "b", Attempts = 1, Status = TicketStatus.Pending });

        await CreateWorker(scopeFactory, classifier, logger).ScanPendingTicketsAsync(CancellationToken.None);

        var ticket = await GetTicketAsync(scopeFactory, "t-1001");
        Assert.Equal(TicketStatus.Classified, ticket.Status);
        Assert.Equal(2, ticket.Attempts);
        Assert.Equal(TicketCategory.Billing, ticket.Category);
        Assert.Equal(TicketPriority.High, ticket.Priority);
        Assert.Equal("The customer reports being charged twice.", ticket.Summary);
    }

    [Fact]
    public async Task Scan_FailedAttempt_NeverPartiallyPersistsCandidate()
    {
        var (scopeFactory, _, logger) = CreateWorkerDependencies();
        // Candidate has invalid category, but valid priority and summary
        var classifier = new CustomCandidateClassifier(new ClassificationCandidate("banana", "high", "Valid summary"));
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-1001", Subject = "s", Body = "b", Attempts = 0, Status = TicketStatus.Pending });

        await CreateWorker(scopeFactory, classifier, logger).ScanPendingTicketsAsync(CancellationToken.None);

        var ticket = await GetTicketAsync(scopeFactory, "t-1001");
        Assert.Equal(TicketStatus.Pending, ticket.Status);
        Assert.Equal(1, ticket.Attempts);
        Assert.Null(ticket.Category);
        Assert.Null(ticket.Priority);
        Assert.Null(ticket.Summary);
    }

    [Fact]
    public async Task Scan_ParallelProcessing_NoDbContextConcurrencyExceptions()
    {
        var (scopeFactory, classifier, logger) = CreateWorkerDependencies();
        var tickets = Enumerable.Range(1, 20)
            .Select(i => new Ticket { Id = $"t-{i:D4}", Subject = $"Subject {i}", Body = $"Body {i}", Attempts = 0, Status = TicketStatus.Pending })
            .ToArray();
        await SeedTicketsAsync(scopeFactory, tickets);

        await CreateWorker(scopeFactory, classifier, logger).ScanPendingTicketsAsync(CancellationToken.None);

        for (var i = 1; i <= 20; i++)
        {
            var ticket = await GetTicketAsync(scopeFactory, $"t-{i:D4}");
            Assert.Equal(TicketStatus.Classified, ticket.Status);
            Assert.Equal(TicketCategory.Billing, ticket.Category);
            Assert.Equal(TicketPriority.High, ticket.Priority);
            Assert.Equal(1, ticket.Attempts);
        }
    }

    [Fact]
    public async Task Repository_StaleUpdate_DoesNotOverwriteClassifiedTicket()
    {
        var (scopeFactory, _, _) = CreateWorkerDependencies();
        var classifiedTicket = new Ticket
        {
            Id = "t-classified",
            Subject = "Subject",
            Body = "Body",
            Status = TicketStatus.Classified,
            Category = TicketCategory.Billing,
            Priority = TicketPriority.High,
            Summary = "Original Summary",
            Attempts = 1
        };
        await SeedTicketsAsync(scopeFactory, classifiedTicket);

        using var scope = scopeFactory.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ITicketRepository>();

        var saveResult = await repository.SaveClassificationAsync(
            "t-classified",
            new ValidatedClassification(TicketCategory.Technical, TicketPriority.Low, "New Summary"));
        Assert.False(saveResult);

        var failResult = await repository.RecordClassificationFailureAsync("t-classified");
        Assert.False(failResult);

        var ticket = await GetTicketAsync(scopeFactory, "t-classified");
        Assert.Equal(TicketStatus.Classified, ticket.Status);
        Assert.Equal(TicketCategory.Billing, ticket.Category);
        Assert.Equal(TicketPriority.High, ticket.Priority);
        Assert.Equal("Original Summary", ticket.Summary);
        Assert.Equal(1, ticket.Attempts);
    }

    // ---- Automatic Retry and Timing Tests ----

    [Fact]
    public async Task Scan_ReturnsTrue_WhenTicketsRemainPendingAfterFailure()
    {
        var (scopeFactory, _, logger) = CreateWorkerDependencies();
        var classifier = new FakeTicketClassifier(FakeClassifierMode.InvalidCategory);
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-1001", Subject = "s", Body = "b", Attempts = 0 });

        var worker = CreateWorker(scopeFactory, classifier, logger);
        var hasPending = await worker.ScanPendingTicketsAsync(CancellationToken.None);

        Assert.True(hasPending);
        var ticket = await GetTicketAsync(scopeFactory, "t-1001");
        Assert.Equal(TicketStatus.Pending, ticket.Status);
        Assert.Equal(1, ticket.Attempts);
    }

    [Fact]
    public async Task Scan_ReturnsFalse_WhenAllTicketsClassifiedOrFailed()
    {
        var (scopeFactory, classifier, logger) = CreateWorkerDependencies();
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-1001", Subject = "s", Body = "b", Attempts = 0 });

        var worker = CreateWorker(scopeFactory, classifier, logger);
        var hasPending = await worker.ScanPendingTicketsAsync(CancellationToken.None);

        Assert.False(hasPending);
        var ticket = await GetTicketAsync(scopeFactory, "t-1001");
        Assert.Equal(TicketStatus.Classified, ticket.Status);
    }

    [Fact]
    public async Task Scan_ReturnsFalse_WhenFailedTicketReachesThreeAttempts()
    {
        var (scopeFactory, _, logger) = CreateWorkerDependencies();
        var classifier = new FakeTicketClassifier(FakeClassifierMode.InvalidCategory);
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-1001", Subject = "s", Body = "b", Attempts = 2, Status = TicketStatus.Pending });

        var worker = CreateWorker(scopeFactory, classifier, logger);
        var hasPending = await worker.ScanPendingTicketsAsync(CancellationToken.None);

        Assert.False(hasPending);
        var ticket = await GetTicketAsync(scopeFactory, "t-1001");
        Assert.Equal(TicketStatus.Failed, ticket.Status);
        Assert.Equal(3, ticket.Attempts);
    }

    [Fact]
    public async Task ExecuteAsync_AutomaticallyRetriesPendingTicket_AfterRetryInterval()
    {
        var (scopeFactory, _, logger) = CreateWorkerDependencies();
        var flakyClassifier = new FlakyTicketClassifier(failFirstNCalls: 1);
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-retry", Subject = "s", Body = "b", Attempts = 0 });

        var worker = CreateWorker(
            scopeFactory,
            flakyClassifier,
            logger,
            retryInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        // Wait for the automatic retry to run and classify the ticket without any external signal
        var deadline = DateTime.UtcNow.AddSeconds(5);
        Ticket? ticket = null;
        while (DateTime.UtcNow < deadline)
        {
            ticket = await GetTicketAsync(scopeFactory, "t-retry");
            if (ticket.Status == TicketStatus.Classified)
            {
                break;
            }
            await Task.Delay(20);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(ticket);
        Assert.Equal(TicketStatus.Classified, ticket.Status);
        Assert.Equal(2, ticket.Attempts);
        Assert.Equal(2, flakyClassifier.Calls);
    }

    [Fact]
    public async Task ExecuteAsync_NewSignal_WakesWorkerImmediatelyBeforeRetryInterval()
    {
        var (scopeFactory, _, logger) = CreateWorkerDependencies();
        var flakyClassifier = new FlakyTicketClassifier(failFirstNCalls: 1);
        var workSignal = new ChannelTicketWorkSignal();
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-sig", Subject = "s", Body = "b", Attempts = 0 });

        // Long retry interval so we can prove the worker woke up due to signal, not timer
        var worker = CreateWorker(
            scopeFactory,
            flakyClassifier,
            logger,
            workSignal: workSignal,
            retryInterval: TimeSpan.FromSeconds(30));

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        // Wait until first attempt fails
        var deadline = DateTime.UtcNow.AddSeconds(5);
        Ticket? ticket = null;
        while (DateTime.UtcNow < deadline)
        {
            ticket = await GetTicketAsync(scopeFactory, "t-sig");
            if (ticket.Attempts == 1)
            {
                break;
            }
            await Task.Delay(20);
        }

        Assert.NotNull(ticket);
        Assert.Equal(1, ticket.Attempts);
        Assert.Equal(TicketStatus.Pending, ticket.Status);

        // Now signal new work: worker should wake up immediately and classify
        var sw = System.Diagnostics.Stopwatch.StartNew();
        workSignal.Signal();

        while (DateTime.UtcNow < deadline)
        {
            ticket = await GetTicketAsync(scopeFactory, "t-sig");
            if (ticket.Status == TicketStatus.Classified)
            {
                break;
            }
            await Task.Delay(20);
        }
        sw.Stop();

        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(ticket);
        Assert.Equal(TicketStatus.Classified, ticket.Status);
        Assert.Equal(2, ticket.Attempts);
        // Completed long before the 30-second timer
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ExecuteAsync_NoPendingTickets_WaitsIndefinitelyForSignal()
    {
        var (scopeFactory, _, logger) = CreateWorkerDependencies();
        var classifier = new StubTicketClassifier();
        var workSignal = new ChannelTicketWorkSignal();

        var worker = CreateWorker(
            scopeFactory,
            classifier,
            logger,
            workSignal: workSignal,
            retryInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        // Initially no tickets; classifier should not be called
        await Task.Delay(100);
        Assert.Empty(classifier.ClassifiedTickets);

        // When a ticket is seeded and signal sent, it wakes and processes
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-idle", Subject = "s", Body = "b", Attempts = 0 });
        workSignal.Signal();

        var deadline = DateTime.UtcNow.AddSeconds(5);
        Ticket? ticket = null;
        while (DateTime.UtcNow < deadline)
        {
            ticket = await GetTicketAsync(scopeFactory, "t-idle");
            if (ticket.Status == TicketStatus.Classified)
            {
                break;
            }
            await Task.Delay(20);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(ticket);
        Assert.Equal(TicketStatus.Classified, ticket.Status);
    }

    [Fact]
    public async Task ExecuteAsync_RepeatedFailures_TransitionToFailedAfterThreeAttempts()
    {
        var (scopeFactory, _, logger) = CreateWorkerDependencies();
        var classifier = new FakeTicketClassifier(FakeClassifierMode.Throw);
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-fail3", Subject = "s", Body = "b", Attempts = 0 });

        var worker = CreateWorker(
            scopeFactory,
            classifier,
            logger,
            retryInterval: TimeSpan.FromMilliseconds(30));

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        var deadline = DateTime.UtcNow.AddSeconds(5);
        Ticket? ticket = null;
        while (DateTime.UtcNow < deadline)
        {
            ticket = await GetTicketAsync(scopeFactory, "t-fail3");
            if (ticket.Status == TicketStatus.Failed)
            {
                break;
            }
            await Task.Delay(20);
        }

        await worker.StopAsync(CancellationToken.None);

        Assert.NotNull(ticket);
        Assert.Equal(TicketStatus.Failed, ticket.Status);
        Assert.Equal(3, ticket.Attempts);
    }

    [Fact]
    public async Task ExecuteAsync_GracefulShutdown_OnCancellation()
    {
        var (scopeFactory, classifier, logger) = CreateWorkerDependencies();
        var worker = CreateWorker(
            scopeFactory,
            classifier,
            logger,
            retryInterval: TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        await worker.StartAsync(cts.Token);

        // Cancel during wait
        cts.Cancel();
        await worker.StopAsync(CancellationToken.None);

        // No unhandled exception should occur
    }
}
