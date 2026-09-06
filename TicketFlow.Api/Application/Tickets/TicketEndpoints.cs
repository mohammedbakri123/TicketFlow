namespace TicketFlow.Api.Application.Tickets;

using Microsoft.AspNetCore.Http;
using TicketFlow.Api.Application.BackgroundWork;
using TicketFlow.Api.Domain.Tickets;

public static class TicketEndpoints
{
    private const int DefaultPage = 1;
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxIdLength = 100;
    private const int MaxSubjectLength = 255;

    public static IEndpointRouteBuilder MapTicketEndpoints(this IEndpointRouteBuilder app)
    {
        // POST /tickets: validate -> persist pending ticket -> 202 Accepted.
        // Idempotent on the client-supplied id: a duplicate submission is an
        // accepted no-op and never triggers classification again. No
        // classification, worker, queue, or LLM is involved in this endpoint.
        app.MapPost("/tickets", async (
            CreateTicketRequest? request,
            TicketService ticketService,
            ITicketWorkSignal workSignal,
            CancellationToken cancellationToken) =>
        {
            if (request is null)
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Bad Request",
                    detail: "Request body is required.");
            }

            var errors = new Dictionary<string, string[]>();

            if (string.IsNullOrWhiteSpace(request.Id))
            {
                errors["id"] = ["id is required."];
            }
            else if (request.Id.Trim().Length > MaxIdLength)
            {
                errors["id"] = [$"id must not exceed {MaxIdLength} characters."];
            }

            if (string.IsNullOrWhiteSpace(request.Subject))
            {
                errors["subject"] = ["subject is required."];
            }
            else if (request.Subject.Trim().Length > MaxSubjectLength)
            {
                errors["subject"] = [$"subject must not exceed {MaxSubjectLength} characters."];
            }

            if (string.IsNullOrWhiteSpace(request.Body))
            {
                errors["body"] = ["body is required."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var ticket = new Ticket
            {
                Id = request.Id!.Trim(),
                Subject = request.Subject!.Trim(),
                Body = request.Body!.Trim()
            };

            var created = await ticketService.CreateAsync(ticket, cancellationToken);

            // Notify the background worker only after a new ticket was
            // persisted. A duplicate submission is an idempotent no-op and
            // must not signal new work. The endpoint never classifies.
            if (created)
            {
                workSignal.Signal();
            }

            return Results.Accepted($"/tickets/{ticket.Id}", new { id = ticket.Id, status = TicketStatus.Pending });
        })
        .WithName("CreateTicket")
        .Produces(StatusCodes.Status202Accepted)
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapGet("/tickets/{id}", async (
            string id,
            TicketService ticketService,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["id"] = ["id is required."]
                });
            }

            var ticket = await ticketService.GetByIdAsync(id.Trim(), cancellationToken);

            return ticket is null
                ? Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Not Found",
                    detail: $"Ticket '{id}' was not found.")
                : Results.Ok(TicketResponse.From(ticket));
        })
        .WithName("GetTicketById")
        .Produces<TicketResponse>(StatusCodes.Status200OK)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status500InternalServerError);

        app.MapGet("/tickets", async (
            string? category,
            string? priority,
            int? page,
            int? pageSize,
            TicketService ticketService,
            CancellationToken cancellationToken) =>
        {
            var errors = new Dictionary<string, string[]>();

            TicketCategory? categoryFilter = null;
            if (!string.IsNullOrWhiteSpace(category))
            {
                if (Enum.TryParse<TicketCategory>(category, ignoreCase: true, out var parsedCategory))
                {
                    categoryFilter = parsedCategory;
                }
                else
                {
                    errors["category"] = [$"Invalid category '{category}'. Valid values are: billing, technical, account, other."];
                }
            }

            TicketPriority? priorityFilter = null;
            if (!string.IsNullOrWhiteSpace(priority))
            {
                if (Enum.TryParse<TicketPriority>(priority, ignoreCase: true, out var parsedPriority))
                {
                    priorityFilter = parsedPriority;
                }
                else
                {
                    errors["priority"] = [$"Invalid priority '{priority}'. Valid values are: low, medium, high."];
                }
            }

            var currentPage = page ?? DefaultPage;
            var currentPageSize = pageSize ?? DefaultPageSize;

            if (page.HasValue && page.Value < 1)
            {
                errors["page"] = ["page must be greater than or equal to 1."];
            }

            if (pageSize.HasValue && (pageSize.Value < 1 || pageSize.Value > MaxPageSize))
            {
                errors["pageSize"] = [$"pageSize must be between 1 and {MaxPageSize}."];
            }

            if (errors.Count > 0)
            {
                return Results.ValidationProblem(errors);
            }

            var (items, total) = await ticketService.ListAsync(
                categoryFilter, priorityFilter, currentPage, currentPageSize, cancellationToken);

            return Results.Ok(new TicketListResponse(
                Items: items.Select(TicketListItemResponse.From).ToList(),
                Pagination: new PaginationResponse(currentPage, currentPageSize, total)));
        })
        .WithName("ListTickets")
        .Produces<TicketListResponse>(StatusCodes.Status200OK)
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status500InternalServerError);

        // POST /tickets/{id}/reclassify: re-queue an existing Classified or Failed ticket
        // for background classification by resetting it to Pending. Returns 409 Conflict if
        // the ticket is already Pending, and 404 Not Found if the ticket does not exist.
        app.MapPost("/tickets/{id}/reclassify", async (
            string id,
            TicketService ticketService,
            ITicketWorkSignal workSignal,
            CancellationToken cancellationToken) =>
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]>
                {
                    ["id"] = ["id is required."]
                });
            }

            var trimmedId = id.Trim();
            var result = await ticketService.ReclassifyAsync(trimmedId, cancellationToken);

            return result switch
            {
                ReclassifyTicketResult.NotFound => Results.Problem(
                    statusCode: StatusCodes.Status404NotFound,
                    title: "Not Found",
                    detail: $"Ticket '{trimmedId}' was not found."),

                ReclassifyTicketResult.AlreadyPending => Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Conflict",
                    detail: $"Ticket '{trimmedId}' is already pending classification."),

                ReclassifyTicketResult.Requeued => OnRequeued(trimmedId, workSignal),

                _ => throw new InvalidOperationException($"Unexpected reclassify result: {result}")
            };

            static IResult OnRequeued(string id, ITicketWorkSignal workSignal)
            {
                workSignal.Signal();
                return Results.Accepted($"/tickets/{id}", new { id, status = TicketStatus.Pending });
            }
        })
        .WithName("ReclassifyTicket")
        .Produces(StatusCodes.Status202Accepted)
        .ProducesProblem(StatusCodes.Status404NotFound)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesValidationProblem()
        .ProducesProblem(StatusCodes.Status500InternalServerError);

        return app;
    }
}
