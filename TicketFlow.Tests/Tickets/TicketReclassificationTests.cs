namespace TicketFlow.Tests.Tickets;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TicketFlow.Api.Application.BackgroundWork;
using TicketFlow.Api.Application.Tickets;
using System.Text.Json;
using System.Text.Json.Serialization;
using TicketFlow.Api.Domain.Tickets;
using TicketFlow.Api.Infrastructure.Persistence;
using TicketFlow.Api.Infrastructure.Persistence.Repositories;

public class TicketReclassificationTests
{
    private sealed class RecordingWorkSignal : ITicketWorkSignal
    {
        public int SignalCallCount { get; private set; }

        public void Signal() => SignalCallCount++;

        public ValueTask WaitForSignalAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private static TicketFlowDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TicketFlowDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TicketFlowDbContext(options);
    }

    [Fact]
    public async Task ReclassifyAsync_ClassifiedTicket_ResetsToPending_ClearsFields_ResetsAttempts()
    {
        using var db = CreateDbContext();
        var repository = new TicketRepository(db, NullLogger<TicketRepository>.Instance);

        var pastTime = DateTime.UtcNow.AddHours(-2);
        var ticket = new Ticket
        {
            Id = "t-classified",
            Subject = "Billing issue",
            Body = "I was double charged.",
            Status = TicketStatus.Classified,
            Category = TicketCategory.Billing,
            Priority = TicketPriority.High,
            Summary = "Customer was double charged.",
            Attempts = 2,
            CreatedAt = pastTime,
            UpdatedAt = pastTime
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        var result = await repository.ReclassifyAsync("t-classified");

        Assert.Equal(ReclassifyTicketResult.Requeued, result);

        var updated = await db.Tickets.SingleAsync(t => t.Id == "t-classified");
        Assert.Equal(TicketStatus.Pending, updated.Status);
        Assert.Equal(0, updated.Attempts);
        Assert.Null(updated.Category);
        Assert.Null(updated.Priority);
        Assert.Null(updated.Summary);
        Assert.True(updated.UpdatedAt > pastTime);
        Assert.Equal(pastTime, updated.CreatedAt);
    }

    [Fact]
    public async Task ReclassifyAsync_FailedTicket_ResetsToPending_ClearsFields_ResetsAttempts()
    {
        using var db = CreateDbContext();
        var repository = new TicketRepository(db, NullLogger<TicketRepository>.Instance);

        var pastTime = DateTime.UtcNow.AddHours(-1);
        var ticket = new Ticket
        {
            Id = "t-failed",
            Subject = "Technical glitch",
            Body = "App crashes on start.",
            Status = TicketStatus.Failed,
            Category = null,
            Priority = null,
            Summary = null,
            Attempts = 3,
            CreatedAt = pastTime,
            UpdatedAt = pastTime
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        var result = await repository.ReclassifyAsync("t-failed");

        Assert.Equal(ReclassifyTicketResult.Requeued, result);

        var updated = await db.Tickets.SingleAsync(t => t.Id == "t-failed");
        Assert.Equal(TicketStatus.Pending, updated.Status);
        Assert.Equal(0, updated.Attempts);
        Assert.Null(updated.Category);
        Assert.Null(updated.Priority);
        Assert.Null(updated.Summary);
        Assert.True(updated.UpdatedAt > pastTime);
    }

    [Fact]
    public async Task ReclassifyAsync_PendingTicket_ReturnsAlreadyPending_LeavesTicketUnchanged()
    {
        using var db = CreateDbContext();
        var repository = new TicketRepository(db, NullLogger<TicketRepository>.Instance);

        var pastTime = DateTime.UtcNow.AddMinutes(-10);
        var ticket = new Ticket
        {
            Id = "t-pending",
            Subject = "Account lock",
            Body = "Cannot login.",
            Status = TicketStatus.Pending,
            Category = null,
            Priority = null,
            Summary = null,
            Attempts = 1,
            CreatedAt = pastTime,
            UpdatedAt = pastTime
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        var result = await repository.ReclassifyAsync("t-pending");

        Assert.Equal(ReclassifyTicketResult.AlreadyPending, result);

        var unchanged = await db.Tickets.SingleAsync(t => t.Id == "t-pending");
        Assert.Equal(TicketStatus.Pending, unchanged.Status);
        Assert.Equal(1, unchanged.Attempts);
        Assert.Equal(pastTime, unchanged.UpdatedAt);
    }

    [Fact]
    public async Task ReclassifyAsync_MissingTicket_ReturnsNotFound()
    {
        using var db = CreateDbContext();
        var repository = new TicketRepository(db, NullLogger<TicketRepository>.Instance);

        var result = await repository.ReclassifyAsync("non-existent");

        Assert.Equal(ReclassifyTicketResult.NotFound, result);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ReclassifyAsync_InvalidId_ThrowsArgumentException(string invalidId)
    {
        using var db = CreateDbContext();
        var repository = new TicketRepository(db, NullLogger<TicketRepository>.Instance);

        await Assert.ThrowsAsync<ArgumentException>(() => repository.ReclassifyAsync(invalidId));
    }

    [Fact]
    public async Task TicketService_ReclassifyAsync_DelegatesToRepository()
    {
        using var db = CreateDbContext();
        var repository = new TicketRepository(db, NullLogger<TicketRepository>.Instance);
        var service = new TicketService(repository);

        var ticket = new Ticket
        {
            Id = "t-svc",
            Subject = "Sub",
            Body = "Body",
            Status = TicketStatus.Classified,
            Attempts = 1
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        var result = await service.ReclassifyAsync("t-svc");

        Assert.Equal(ReclassifyTicketResult.Requeued, result);
    }

    private sealed class TestEndpointRouteBuilder(IServiceProvider sp) : IEndpointRouteBuilder
    {
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IServiceProvider ServiceProvider { get; } = sp;
        public IApplicationBuilder CreateApplicationBuilder() => throw new NotImplementedException();
    }

    private static async Task<(int StatusCode, string? Location, string Body)> InvokeReclassifyEndpointAsync(
        string id,
        TicketService service,
        ITicketWorkSignal signal,
        CancellationToken cancellationToken = default)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouting();
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
        services.AddSingleton(service);
        services.AddSingleton(signal);
        var sp = services.BuildServiceProvider();

        var routeBuilder = new TestEndpointRouteBuilder(sp);
        routeBuilder.MapTicketEndpoints();

        var endpoint = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .Single(e => e.Metadata.GetMetadata<EndpointNameMetadata>()?.EndpointName == "ReclassifyTicket");

        var context = new DefaultHttpContext { RequestServices = sp };
        context.Request.RouteValues["id"] = id;
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync(cancellationToken);

        return (context.Response.StatusCode, context.Response.Headers.Location.ToString(), body);
    }

    [Fact]
    public async Task Endpoint_Requeued_Returns202AcceptedWithLocationAndBody_AndSignalsWorker()
    {
        using var db = CreateDbContext();
        var repository = new TicketRepository(db, NullLogger<TicketRepository>.Instance);
        var service = new TicketService(repository);
        var signal = new RecordingWorkSignal();

        var ticket = new Ticket
        {
            Id = "t-accepted",
            Subject = "Sub",
            Body = "Body",
            Status = TicketStatus.Classified,
            Attempts = 1
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        var (statusCode, location, body) = await InvokeReclassifyEndpointAsync(
            "t-accepted", service, signal, CancellationToken.None);

        Assert.Equal(StatusCodes.Status202Accepted, statusCode);
        Assert.Equal("/tickets/t-accepted", location);
        Assert.Contains("\"pending\"", body);
        Assert.Equal(1, signal.SignalCallCount);
    }

    [Fact]
    public async Task Endpoint_AlreadyPending_Returns409Conflict_AndDoesNotSignalWorker()
    {
        using var db = CreateDbContext();
        var repository = new TicketRepository(db, NullLogger<TicketRepository>.Instance);
        var service = new TicketService(repository);
        var signal = new RecordingWorkSignal();

        var ticket = new Ticket
        {
            Id = "t-conflict",
            Subject = "Sub",
            Body = "Body",
            Status = TicketStatus.Pending,
            Attempts = 1
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        var (statusCode, _, body) = await InvokeReclassifyEndpointAsync(
            "t-conflict", service, signal, CancellationToken.None);

        Assert.Equal(StatusCodes.Status409Conflict, statusCode);
        Assert.Contains("Ticket 't-conflict' is already pending classification.", body);
        Assert.Equal(0, signal.SignalCallCount);
    }

    [Fact]
    public async Task Endpoint_MissingTicket_Returns404NotFound_AndDoesNotSignalWorker()
    {
        using var db = CreateDbContext();
        var repository = new TicketRepository(db, NullLogger<TicketRepository>.Instance);
        var service = new TicketService(repository);
        var signal = new RecordingWorkSignal();

        var (statusCode, _, body) = await InvokeReclassifyEndpointAsync(
            "t-missing", service, signal, CancellationToken.None);

        Assert.Equal(StatusCodes.Status404NotFound, statusCode);
        Assert.Contains("Ticket 't-missing' was not found.", body);
        Assert.Equal(0, signal.SignalCallCount);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Endpoint_EmptyId_ReturnsValidationProblem_AndDoesNotSignalWorker(string emptyId)
    {
        using var db = CreateDbContext();
        var repository = new TicketRepository(db, NullLogger<TicketRepository>.Instance);
        var service = new TicketService(repository);
        var signal = new RecordingWorkSignal();

        var (statusCode, _, body) = await InvokeReclassifyEndpointAsync(
            emptyId, service, signal, CancellationToken.None);

        Assert.Equal(StatusCodes.Status400BadRequest, statusCode);
        Assert.Contains("id is required.", body);
        Assert.Equal(0, signal.SignalCallCount);
    }

    [Fact]
    public async Task Endpoint_TrimsIdConsistently()
    {
        using var db = CreateDbContext();
        var repository = new TicketRepository(db, NullLogger<TicketRepository>.Instance);
        var service = new TicketService(repository);
        var signal = new RecordingWorkSignal();

        var ticket = new Ticket
        {
            Id = "t-trim",
            Subject = "Sub",
            Body = "Body",
            Status = TicketStatus.Classified,
            Attempts = 1
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync();

        var (statusCode, location, _) = await InvokeReclassifyEndpointAsync(
            "  t-trim  ", service, signal, CancellationToken.None);

        Assert.Equal(StatusCodes.Status202Accepted, statusCode);
        Assert.Equal("/tickets/t-trim", location);
        Assert.Equal(1, signal.SignalCallCount);
    }
}
