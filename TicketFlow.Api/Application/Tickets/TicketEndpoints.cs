namespace TicketFlow.Api.Application.Tickets;

using TicketFlow.Api.Domain.Tickets;

public static class TicketEndpoints
{
    private const int DefaultPage = 1;
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;

    public static IEndpointRouteBuilder MapTicketEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /tickets: validate -> persist pending ticket -> 202 Accepted.
        // Idempotent on the client-supplied id: a duplicate submission is an
        // accepted no-op and never triggers classification again. No
        // classification, worker, queue, or LLM is involved in this endpoint.
        app.MapPost("/tickets", async (CreateTicketRequest? request, TicketService ticketService) =>
        {
            if (request is null)
            {
                return BadRequest("Request body is required.");
            }

            if (string.IsNullOrWhiteSpace(request.Id))
            {
                return BadRequest("id is required.");
            }

            if (string.IsNullOrWhiteSpace(request.Subject))
            {
                return BadRequest("subject is required.");
            }

            if (string.IsNullOrWhiteSpace(request.Body))
            {
                return BadRequest("body is required.");
            }

            var ticket = new Ticket
            {
                Id = request.Id.Trim(),
                Subject = request.Subject.Trim(),
                Body = request.Body.Trim()
            };

            await ticketService.CreateAsync(ticket);

            return Results.Accepted($"/tickets/{ticket.Id}", new { id = ticket.Id, status = TicketStatus.Pending });
        });

        app.MapGet("/tickets/{id}", async (string id, TicketService ticketService) =>
        {
            var ticket = await ticketService.GetByIdAsync(id);

            return ticket is null
                ? Results.NotFound(new { error = $"Ticket '{id}' was not found." })
                : Results.Ok(TicketResponse.From(ticket));
        });

        app.MapGet("/tickets", async (
            string? category,
            string? priority,
            int? page,
            int? pageSize,
            TicketService ticketService) =>
        {
            TicketCategory? categoryFilter = null;
            if (!string.IsNullOrWhiteSpace(category))
            {
                if (!Enum.TryParse<TicketCategory>(category, ignoreCase: true, out var parsedCategory))
                {
                    return BadRequest(
                        $"Invalid category '{category}'. Valid values are: billing, technical, account, other.");
                }

                categoryFilter = parsedCategory;
            }

            TicketPriority? priorityFilter = null;
            if (!string.IsNullOrWhiteSpace(priority))
            {
                if (!Enum.TryParse<TicketPriority>(priority, ignoreCase: true, out var parsedPriority))
                {
                    return BadRequest(
                        $"Invalid priority '{priority}'. Valid values are: low, medium, high.");
                }

                priorityFilter = parsedPriority;
            }

            var currentPage = page ?? DefaultPage;
            var currentPageSize = pageSize ?? DefaultPageSize;

            if (currentPage < 1)
            {
                return BadRequest("page must be greater than or equal to 1.");
            }

            if (currentPageSize < 1 || currentPageSize > MaxPageSize)
            {
                return BadRequest($"pageSize must be between 1 and {MaxPageSize}.");
            }

            var (items, total) = await ticketService.ListAsync(
                categoryFilter, priorityFilter, currentPage, currentPageSize);

            return Results.Ok(new TicketListResponse(
                Items: items.Select(TicketListItemResponse.From).ToList(),
                Pagination: new PaginationResponse(currentPage, currentPageSize, total)));
        });

        return app;
    }

    private static IResult BadRequest(string message) => Results.BadRequest(new { error = message });
}
