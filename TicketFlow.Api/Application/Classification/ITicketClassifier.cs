namespace TicketFlow.Api.Application.Classification;

using TicketFlow.Api.Domain.Tickets;

/// <summary>
/// Classifies a support ticket. The model behind any implementation is
/// treated as an unreliable dependency: it may return invalid or malformed
/// data, or throw. The returned <see cref="ClassificationCandidate"/> is
/// therefore untrusted and must be validated before anything is persisted.
/// </summary>
public interface ITicketClassifier
{
    /// <summary>Produces an untrusted classification candidate for a ticket.</summary>
    Task<ClassificationCandidate> ClassifyAsync(Ticket ticket, CancellationToken cancellationToken = default);
}
