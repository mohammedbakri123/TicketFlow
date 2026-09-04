namespace TicketFlow.Api.Application.Tickets;

using TicketFlow.Api.Domain.Tickets;

/// <summary>Full ticket representation returned by GET /tickets/{id}.</summary>
public record TicketResponse(
    string Id,
    string Subject,
    string Body,
    TicketStatus Status,
    TicketCategory? Category,
    TicketPriority? Priority,
    string? Summary,
    int Attempts,
    DateTime CreatedAt,
    DateTime UpdatedAt)
{
    public static TicketResponse From(Ticket ticket) => new(
        ticket.Id,
        ticket.Subject,
        ticket.Body,
        ticket.Status,
        ticket.Category,
        ticket.Priority,
        ticket.Summary,
        ticket.Attempts,
        ticket.CreatedAt,
        ticket.UpdatedAt);
}

/// <summary>Representation of a ticket inside the GET /tickets list response.</summary>
public record TicketListItemResponse(
    string Id,
    string Subject,
    string Body,
    TicketStatus Status,
    TicketCategory? Category,
    TicketPriority? Priority,
    string? Summary)
{
    public static TicketListItemResponse From(Ticket ticket) => new(
        ticket.Id,
        ticket.Subject,
        ticket.Body,
        ticket.Status,
        ticket.Category,
        ticket.Priority,
        ticket.Summary);
}

public record PaginationResponse(int Page, int PageSize, int Total);

public record TicketListResponse(IReadOnlyList<TicketListItemResponse> Items, PaginationResponse Pagination);
