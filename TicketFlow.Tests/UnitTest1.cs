using Microsoft.EntityFrameworkCore;
using TicketFlow.Api.Domain.Tickets;
using TicketFlow.Api.Infrastructure.Persistence;

namespace TicketFlow.Tests;

public class TicketTests
{
    [Fact]
    public void Ticket_InitializesWithCorrectDefaults()
    {
        var ticket = new Ticket
        {
            Subject = "Test Subject",
            Body = "Test Body"
        };

        Assert.Equal(string.Empty, ticket.Id);
        Assert.Equal("Test Subject", ticket.Subject);
        Assert.Equal("Test Body", ticket.Body);
        Assert.Equal(TicketStatus.Pending, ticket.Status);
        Assert.Null(ticket.Category);
        Assert.Null(ticket.Priority);
        Assert.Null(ticket.Summary);
        Assert.Equal(0, ticket.Attempts);
        Assert.True(ticket.CreatedAt <= DateTime.UtcNow);
        Assert.True(ticket.UpdatedAt <= DateTime.UtcNow);
    }

    [Fact]
    public void DbContext_Model_HasExpectedConfiguration()
    {
        // Verify model metadata
        var builder = new DbContextOptionsBuilder<TicketFlowDbContext>();
        builder.UseNpgsql("Host=localhost;Database=test;Username=postgres;Password=postgres");

        using var context = new TicketFlowDbContext(builder.Options);
        var entityType = context.Model.FindEntityType(typeof(Ticket));

        Assert.NotNull(entityType);
        Assert.Equal("tickets", entityType.GetTableName());

        var key = entityType.FindPrimaryKey();
        Assert.NotNull(key);
        Assert.Single(key.Properties);
        Assert.Equal(nameof(Ticket.Id), key.Properties[0].Name);

        Assert.Equal(255, entityType.FindProperty(nameof(Ticket.Subject))?.GetMaxLength());
        Assert.Equal(50, entityType.FindProperty(nameof(Ticket.Status))?.GetMaxLength());
        Assert.Equal(50, entityType.FindProperty(nameof(Ticket.Category))?.GetMaxLength());
        Assert.Equal(50, entityType.FindProperty(nameof(Ticket.Priority))?.GetMaxLength());
        Assert.Equal(1000, entityType.FindProperty(nameof(Ticket.Summary))?.GetMaxLength());
    }
}
