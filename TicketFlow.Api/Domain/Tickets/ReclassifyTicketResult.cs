namespace TicketFlow.Api.Domain.Tickets;

public enum ReclassifyTicketResult
{
    NotFound,
    AlreadyPending,
    Requeued
}
