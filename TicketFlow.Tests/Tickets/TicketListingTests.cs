namespace TicketFlow.Tests.Tickets;

using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TicketFlow.Api.Application.BackgroundWork;
using TicketFlow.Api.Application.Tickets;
using TicketFlow.Api.Domain.Tickets;
using TicketFlow.Api.Infrastructure.Persistence;
using TicketFlow.Api.Infrastructure.Persistence.Repositories;

public class TicketListingTests
{
    private sealed class TestEndpointRouteBuilder(IServiceProvider sp) : IEndpointRouteBuilder
    {
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IServiceProvider ServiceProvider { get; } = sp;
        public IApplicationBuilder CreateApplicationBuilder() => throw new NotImplementedException();
    }

    private static TicketFlowDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<TicketFlowDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        return new TicketFlowDbContext(options);
    }

    [Fact]
    public async Task ListAsync_FilterByStatus_ReturnsOnlyMatchingTickets()
    {
        using var db = CreateDbContext();
        var repository = new TicketRepository(db, NullLogger<TicketRepository>.Instance);

        db.Tickets.AddRange(
            new Ticket { Id = "t-1", Subject = "S1", Body = "B1", Status = TicketStatus.Pending },
            new Ticket { Id = "t-2", Subject = "S2", Body = "B2", Status = TicketStatus.Classified, Category = TicketCategory.Billing, Priority = TicketPriority.High },
            new Ticket { Id = "t-3", Subject = "S3", Body = "B3", Status = TicketStatus.Classified, Category = TicketCategory.Technical, Priority = TicketPriority.Low },
            new Ticket { Id = "t-4", Subject = "S4", Body = "B4", Status = TicketStatus.Failed }
        );
        await db.SaveChangesAsync();

        // Filter Pending
        var (pendingItems, pendingTotal) = await repository.ListAsync(TicketStatus.Pending, null, null, 1, 20);
        Assert.Equal(1, pendingTotal);
        Assert.Single(pendingItems);
        Assert.Equal("t-1", pendingItems[0].Id);

        // Filter Classified
        var (classifiedItems, classifiedTotal) = await repository.ListAsync(TicketStatus.Classified, null, null, 1, 20);
        Assert.Equal(2, classifiedTotal);
        Assert.Equal(2, classifiedItems.Count);
        Assert.Contains(classifiedItems, t => t.Id == "t-2");
        Assert.Contains(classifiedItems, t => t.Id == "t-3");

        // Filter Failed
        var (failedItems, failedTotal) = await repository.ListAsync(TicketStatus.Failed, null, null, 1, 20);
        Assert.Equal(1, failedTotal);
        Assert.Single(failedItems);
        Assert.Equal("t-4", failedItems[0].Id);

        // No filter
        var (allItems, allTotal) = await repository.ListAsync(null, null, null, 1, 20);
        Assert.Equal(4, allTotal);
        Assert.Equal(4, allItems.Count);
    }

    [Fact]
    public async Task ListAsync_FilterByStatusCategoryAndPriority_CombinesAllFilters()
    {
        using var db = CreateDbContext();
        var repository = new TicketRepository(db, NullLogger<TicketRepository>.Instance);

        db.Tickets.AddRange(
            new Ticket { Id = "t-1", Subject = "S1", Body = "B1", Status = TicketStatus.Classified, Category = TicketCategory.Billing, Priority = TicketPriority.High },
            new Ticket { Id = "t-2", Subject = "S2", Body = "B2", Status = TicketStatus.Classified, Category = TicketCategory.Billing, Priority = TicketPriority.Low },
            new Ticket { Id = "t-3", Subject = "S3", Body = "B3", Status = TicketStatus.Classified, Category = TicketCategory.Technical, Priority = TicketPriority.High },
            new Ticket { Id = "t-4", Subject = "S4", Body = "B4", Status = TicketStatus.Pending, Category = TicketCategory.Billing, Priority = TicketPriority.High }
        );
        await db.SaveChangesAsync();

        var (items, total) = await repository.ListAsync(
            TicketStatus.Classified, TicketCategory.Billing, TicketPriority.High, 1, 20);

        Assert.Equal(1, total);
        Assert.Single(items);
        Assert.Equal("t-1", items[0].Id);
    }

    private static async Task<(int StatusCode, string Body)> InvokeListEndpointAsync(
        TicketService service,
        string? queryString = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddRouting();
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        });
        services.AddSingleton(service);
        services.AddSingleton<ITicketWorkSignal, ChannelTicketWorkSignal>();
        var sp = services.BuildServiceProvider();

        var routeBuilder = new TestEndpointRouteBuilder(sp);
        routeBuilder.MapTicketEndpoints();

        var endpoint = routeBuilder.DataSources
            .SelectMany(ds => ds.Endpoints)
            .Single(e => e.Metadata.GetMetadata<EndpointNameMetadata>()?.EndpointName == "ListTickets");

        var context = new DefaultHttpContext { RequestServices = sp };
        if (!string.IsNullOrEmpty(queryString))
        {
            context.Request.QueryString = new QueryString(queryString);
        }
        context.Response.Body = new MemoryStream();

        await endpoint.RequestDelegate!(context);

        context.Response.Body.Seek(0, SeekOrigin.Begin);
        using var reader = new StreamReader(context.Response.Body);
        var body = await reader.ReadToEndAsync();

        return (context.Response.StatusCode, body);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("Pending")]
    [InlineData("PENDING")]
    [InlineData("classified")]
    [InlineData("Classified")]
    [InlineData("failed")]
    [InlineData("FAILED")]
    public async Task ListEndpoint_ValidStatus_ParsesCaseInsensitively(string statusParam)
    {
        using var db = CreateDbContext();
        var repository = new TicketRepository(db, NullLogger<TicketRepository>.Instance);
        var service = new TicketService(repository);

        var (statusCode, body) = await InvokeListEndpointAsync(service, $"?status={statusParam}");

        Assert.Equal(StatusCodes.Status200OK, statusCode);
        Assert.Contains("\"items\"", body);
    }

    [Fact]
    public async Task ListEndpoint_InvalidStatus_ReturnsValidationProblem()
    {
        using var db = CreateDbContext();
        var repository = new TicketRepository(db, NullLogger<TicketRepository>.Instance);
        var service = new TicketService(repository);

        var (statusCode, body) = await InvokeListEndpointAsync(service, "?status=not_a_status");

        Assert.Equal(StatusCodes.Status400BadRequest, statusCode);
        Assert.Contains("Invalid status 'not_a_status'. Valid values are: pending, classified, failed.", body);
    }
}
