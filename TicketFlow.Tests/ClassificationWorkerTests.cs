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
        public List<Ticket> ClassifiedTickets { get; } = [];

        public HashSet<string> FailForTicketIds { get; } = new();

        public Task<ClassificationCandidate> ClassifyAsync(Ticket ticket, CancellationToken cancellationToken = default)
        {
            ClassifiedTickets.Add(ticket);

            if (FailForTicketIds.Contains(ticket.Id))
            {
                throw new InvalidOperationException("Simulated classifier failure.");
            }

            return Task.FromResult(new ClassificationCandidate(
                "billing", "high", "The customer reports being charged twice."));
        }
    }

    private sealed class TestLogger : ILogger<ClassificationWorker>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
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
        TestLogger logger)
        => new(scopeFactory, new ChannelTicketWorkSignal(), classifier, logger);

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

        // The second ticket was still processed after the first one failed.
        Assert.Equal(["t-1001", "t-1002"], classifier.ClassifiedTickets.Select(t => t.Id));

        var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("t-1001", error.Message);
    }

    [Fact]
    public async Task Scan_DoesNotMarkTicketsClassifiedAtThisStage()
    {
        var (scopeFactory, classifier, logger) = CreateWorkerDependencies();
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-1001", Subject = "s", Body = "b" });

        await CreateWorker(scopeFactory, classifier, logger).ScanPendingTicketsAsync(CancellationToken.None);

        var ticket = await GetTicketAsync(scopeFactory, "t-1001");
        Assert.Equal(TicketStatus.Pending, ticket.Status);
    }

    [Fact]
    public async Task Scan_DoesNotPersistUnvalidatedClassification()
    {
        // The stub returns a plausible candidate, but nothing may reach the
        // ticket before validation (next step).
        var (scopeFactory, classifier, logger) = CreateWorkerDependencies();
        await SeedTicketsAsync(scopeFactory, new Ticket { Id = "t-1001", Subject = "s", Body = "b" });

        await CreateWorker(scopeFactory, classifier, logger).ScanPendingTicketsAsync(CancellationToken.None);

        var ticket = await GetTicketAsync(scopeFactory, "t-1001");
        Assert.Equal(TicketStatus.Pending, ticket.Status);
        Assert.Null(ticket.Category);
        Assert.Null(ticket.Priority);
        Assert.Null(ticket.Summary);
    }
}
