namespace TicketFlow.Tests.Tickets;

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Api.Application.BackgroundWork;
using TicketFlow.Api.Application.Classification;
using TicketFlow.Api.Application.Tickets;
using TicketFlow.Api.Domain.Tickets;

public class TicketEndpointTests
{
    private sealed class RecordingWorkSignal : ITicketWorkSignal
    {
        public int SignalCallCount { get; private set; }

        public void Signal() => SignalCallCount++;

        public ValueTask WaitForSignalAsync(CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class RecordingRepository : ITicketRepository
    {
        public List<Ticket> Tickets { get; } = [];
        public int AddCallCount { get; private set; }
        public int ListCallCount { get; private set; }
        public TicketCategory? LastCategory { get; private set; }
        public TicketPriority? LastPriority { get; private set; }
        public int LastPage { get; private set; }
        public int LastPageSize { get; private set; }

        public Task<bool> AddAsync(Ticket ticket, CancellationToken cancellationToken = default)
        {
            AddCallCount++;
            if (Tickets.Any(existing => existing.Id == ticket.Id))
            {
                return Task.FromResult(false);
            }

            Tickets.Add(ticket);
            return Task.FromResult(true);
        }

        public Task<Ticket?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult(Tickets.FirstOrDefault(ticket => ticket.Id == id));

        public Task<(IReadOnlyList<Ticket> Items, int Total)> ListAsync(
            TicketCategory? category,
            TicketPriority? priority,
            int page,
            int pageSize,
            CancellationToken cancellationToken = default)
        {
            ListCallCount++;
            LastCategory = category;
            LastPriority = priority;
            LastPage = page;
            LastPageSize = pageSize;

            var query = Tickets.AsEnumerable();
            if (category is not null)
            {
                query = query.Where(ticket => ticket.Category == category);
            }

            if (priority is not null)
            {
                query = query.Where(ticket => ticket.Priority == priority);
            }

            var items = query
                .OrderBy(ticket => ticket.CreatedAt)
                .ThenBy(ticket => ticket.Id)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .ToList();

            var total = query.Count();
            return Task.FromResult<(IReadOnlyList<Ticket> Items, int Total)>((items, total));
        }

        public Task<IReadOnlyList<Ticket>> GetPendingTicketsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<Ticket>>(Tickets.Where(ticket => ticket.Status == TicketStatus.Pending).ToList());

        public Task<bool> SaveClassificationAsync(
            string id,
            ValidatedClassification classification,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> RecordClassificationFailureAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<ReclassifyTicketResult> ReclassifyAsync(
            string id,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class TestEndpointRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public IApplicationBuilder CreateApplicationBuilder() => throw new NotSupportedException();
    }

    private sealed class RequestBodyDetectionFeature : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => true;
    }

    private sealed record EndpointResponse(int StatusCode, string? Location, string Body);

    private static async Task<EndpointResponse> InvokeEndpointAsync(
        string endpointName,
        TicketService ticketService,
        ITicketWorkSignal workSignal,
        string? id = null,
        string? queryString = null,
        object? requestBody = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouting();
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
        services.AddSingleton(ticketService);
        services.AddSingleton(workSignal);
        var serviceProvider = services.BuildServiceProvider();

        var routeBuilder = new TestEndpointRouteBuilder(serviceProvider);
        routeBuilder.MapTicketEndpoints();

        var endpoint = routeBuilder.DataSources
            .SelectMany(dataSource => dataSource.Endpoints)
            .Single(endpoint => endpoint.Metadata.GetMetadata<EndpointNameMetadata>()?.EndpointName == endpointName);

        var context = new DefaultHttpContext { RequestServices = serviceProvider };
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new RequestBodyDetectionFeature());
        if (id is not null)
        {
            context.Request.RouteValues["id"] = id;
        }

        if (queryString is not null)
        {
            context.Request.QueryString = new QueryString(queryString);
        }

        if (requestBody is not null)
        {
            var json = JsonSerializer.Serialize(requestBody);
            var requestBytes = Encoding.UTF8.GetBytes(json);
            context.Request.ContentType = "application/json";
            context.Request.ContentLength = requestBytes.Length;
            context.Request.Body = new MemoryStream(requestBytes);
        }

        context.Response.Body = new MemoryStream();
        await endpoint.RequestDelegate!(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();
        return new EndpointResponse(
            context.Response.StatusCode,
            context.Response.Headers.Location.ToString(),
            body);
    }

    [Fact]
    public async Task CreateTicket_NewTicket_ReturnsAcceptedAndSignalsWorker()
    {
        var repository = new RecordingRepository();
        var signal = new RecordingWorkSignal();
        var service = new TicketService(repository);

        var response = await InvokeEndpointAsync(
            "CreateTicket",
            service,
            signal,
            requestBody: new { id = "t-new", subject = "Billing issue", body = "Please help." });

        Assert.Equal(StatusCodes.Status202Accepted, response.StatusCode);
        Assert.Equal("/tickets/t-new", response.Location);
        Assert.Contains("\"pending\"", response.Body);
        Assert.Equal(1, signal.SignalCallCount);
        Assert.Equal(1, repository.AddCallCount);
        Assert.Equal(TicketStatus.Pending, repository.Tickets.Single().Status);
    }

    [Fact]
    public async Task CreateTicket_DuplicateId_ReturnsAcceptedWithoutResignaling()
    {
        var repository = new RecordingRepository();
        var signal = new RecordingWorkSignal();
        var service = new TicketService(repository);
        var request = new { id = "t-duplicate", subject = "Original", body = "Original body" };

        await InvokeEndpointAsync("CreateTicket", service, signal, requestBody: request);
        var response = await InvokeEndpointAsync(
            "CreateTicket",
            service,
            signal,
            requestBody: new { id = "t-duplicate", subject = "Different", body = "Different body" });

        Assert.Equal(StatusCodes.Status202Accepted, response.StatusCode);
        Assert.Equal(1, signal.SignalCallCount);
        Assert.Equal(2, repository.AddCallCount);
        Assert.Single(repository.Tickets);
        Assert.Equal("Original", repository.Tickets[0].Subject);
    }

    [Fact]
    public async Task CreateTicket_OversizedBody_ReturnsValidationProblemWithoutPersistence()
    {
        var repository = new RecordingRepository();
        var signal = new RecordingWorkSignal();
        var service = new TicketService(repository);

        var response = await InvokeEndpointAsync(
            "CreateTicket",
            service,
            signal,
            requestBody: new { id = "t-large", subject = "Large ticket", body = new string('x', 100_001) });

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("body must not exceed 100000 characters", response.Body);
        Assert.Equal(0, repository.AddCallCount);
        Assert.Equal(0, signal.SignalCallCount);
    }

    [Fact]
    public async Task GetTicket_ReturnsTicketOrNotFound()
    {
        var repository = new RecordingRepository();
        repository.Tickets.Add(new Ticket { Id = "t-existing", Subject = "Subject", Body = "Body" });
        var service = new TicketService(repository);
        var signal = new RecordingWorkSignal();

        var found = await InvokeEndpointAsync("GetTicketById", service, signal, id: "t-existing");
        var missing = await InvokeEndpointAsync("GetTicketById", service, signal, id: "t-missing");

        Assert.Equal(StatusCodes.Status200OK, found.StatusCode);
        Assert.Contains("t-existing", found.Body);
        Assert.Equal(StatusCodes.Status404NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task ListTickets_PassesFiltersAndPaginationToService()
    {
        var repository = new RecordingRepository();
        repository.Tickets.AddRange([
            new Ticket
            {
                Id = "t-billing-1",
                Subject = "First",
                Body = "Body",
                Category = TicketCategory.Billing,
                Priority = TicketPriority.High,
                CreatedAt = DateTime.UtcNow.AddMinutes(-2)
            },
            new Ticket
            {
                Id = "t-billing-2",
                Subject = "Second",
                Body = "Body",
                Category = TicketCategory.Billing,
                Priority = TicketPriority.High,
                CreatedAt = DateTime.UtcNow.AddMinutes(-1)
            }
        ]);
        var service = new TicketService(repository);
        var signal = new RecordingWorkSignal();

        var response = await InvokeEndpointAsync(
            "ListTickets",
            service,
            signal,
            queryString: "?category=billing&priority=high&page=2&pageSize=1");

        Assert.Equal(StatusCodes.Status200OK, response.StatusCode);
        Assert.Equal(TicketCategory.Billing, repository.LastCategory);
        Assert.Equal(TicketPriority.High, repository.LastPriority);
        Assert.Equal(2, repository.LastPage);
        Assert.Equal(1, repository.LastPageSize);
        Assert.Contains("t-billing-2", response.Body);
        Assert.Contains("\"total\":2", response.Body);
    }

    [Theory]
    [InlineData("?category=0")]
    [InlineData("?priority=1")]
    public async Task ListTickets_NumericEnumFilters_ReturnValidationProblem(string queryString)
    {
        var repository = new RecordingRepository();
        var service = new TicketService(repository);
        var signal = new RecordingWorkSignal();

        var response = await InvokeEndpointAsync(
            "ListTickets",
            service,
            signal,
            queryString: queryString);

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Equal(0, repository.ListCallCount);
    }
}
