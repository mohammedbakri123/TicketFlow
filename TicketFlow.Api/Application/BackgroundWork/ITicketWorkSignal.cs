namespace TicketFlow.Api.Application.BackgroundWork;

/// <summary>
/// In-process signal used to wake the classification worker when new ticket
/// work may exist. The signal carries no data: PostgreSQL is the source of
/// truth, and the worker re-queries pending tickets on every wake-up.
/// </summary>
public interface ITicketWorkSignal
{
    /// <summary>
    /// Rings the signal. Safe to call repeatedly; signals that arrive while
    /// one is already pending coalesce into a single wake-up.
    /// </summary>
    void Signal();

    /// <summary>
    /// Waits asynchronously until a signal is available.
    /// Returns after one wake-up, even if several signals accumulated.
    /// </summary>
    ValueTask WaitForSignalAsync(CancellationToken cancellationToken = default);
}
