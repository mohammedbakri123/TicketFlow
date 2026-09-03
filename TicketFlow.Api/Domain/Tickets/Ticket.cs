namespace TicketFlow.Api.Domain.Tickets;

public class Ticket
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public string Subject { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public TicketStatus Status { get; set; } = TicketStatus.Pending;

    public TicketCategory? Category { get; set; }

    public TicketPriority? Priority { get; set; }

    public string? Summary { get; set; }

    public int Attempts { get; set; } = 0;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
