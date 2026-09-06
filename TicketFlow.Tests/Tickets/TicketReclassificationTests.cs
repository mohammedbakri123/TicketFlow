namespace TicketFlow.Tests.Tickets;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TicketFlow.Api.Application.BackgroundWork;
using TicketFlow.Api.Application.Tickets;
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

        var result = await TicketEndpoints.ReclassifyEndpointAsync(
            "t-accepted", service, signal, CancellationToken.None);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status202Accepted, statusResult.StatusCode);

        var location = result.GetType().GetProperty("Location")?.GetValue(result) as string;
        Assert.Equal("/tickets/t-accepted", location);

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

        var result = await TicketEndpoints.ReclassifyEndpointAsync(
            "t-conflict", service, signal, CancellationToken.None);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        var statusResult = (IStatusCodeHttpResult)result;
        Assert.Equal(StatusCodes.Status409Conflict, statusResult.StatusCode);

        Assert.IsType<ProblemHttpResult>(result);
        var problem = (ProblemHttpResult)result;
        Assert.Equal("Ticket 't-conflict' is already pending classification.", problem.ProblemDetails.Detail);

        Assert.Equal(0, signal.SignalCallCount);
    }

    [Fact]
    public async Task Endpoint_MissingTicket_Returns404NotFound_AndDoesNotSignalWorker()
    {
        using var db = CreateDbContext();
        var repository = new TicketRepository(db, NullLogger<TicketRepository>.Instance);
        var service = new TicketService(repository);
        var signal = new RecordingWorkSignal();

        var result = await TicketEndpoints.ReclassifyEndpointAsync(
            "t-missing", service, signal, CancellationToken.None);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        var statusResult = (IStatusCodeHttpResult)result;
        Assert.Equal(StatusCodes.Status404NotFound, statusResult.StatusCode);

        Assert.IsType<ProblemHttpResult>(result);
        var problem = (ProblemHttpResult)result;
        Assert.Equal("Ticket 't-missing' was not found.", problem.ProblemDetails.Detail);

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

        var result = await TicketEndpoints.ReclassifyEndpointAsync(
            emptyId, service, signal, CancellationToken.None);

        Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        var statusResult = (IStatusCodeHttpResult)result;
        Assert.Equal(StatusCodes.Status400BadRequest, statusResult.StatusCode);

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

        var result = await TicketEndpoints.ReclassifyEndpointAsync(
            "  t-trim  ", service, signal, CancellationToken.None);

        var statusResult = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status202Accepted, statusResult.StatusCode);

        var location = result.GetType().GetProperty("Location")?.GetValue(result) as string;
        Assert.Equal("/tickets/t-trim", location);
        Assert.Equal(1, signal.SignalCallCount);
    }
}
