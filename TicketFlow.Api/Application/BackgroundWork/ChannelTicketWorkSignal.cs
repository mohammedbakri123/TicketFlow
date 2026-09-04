namespace TicketFlow.Api.Application.BackgroundWork;

using System.Threading.Channels;

/// <summary>
/// Channel-based in-process signal. Capacity 1 with DropWrite means at most
/// one pending signal exists: the signal only means "new work may exist" and
/// the worker re-queries PostgreSQL on wake-up, so repeated signals coalesce
/// instead of queuing up.
/// </summary>
public sealed class ChannelTicketWorkSignal : ITicketWorkSignal
{
    private readonly Channel<object?> _channel = Channel.CreateBounded<object?>(
        new BoundedChannelOptions(capacity: 1)
        {
            FullMode = BoundedChannelFullMode.DropWrite
        });

    public void Signal() => _channel.Writer.TryWrite(null);

    public async ValueTask WaitForSignalAsync(CancellationToken cancellationToken = default)
    {
        await _channel.Reader.ReadAsync(cancellationToken);

        // Drain any signals that arrived during the wake-up so a burst of
        // submissions triggers a single scan instead of redundant ones.
        while (_channel.Reader.TryRead(out _))
        {
        }
    }
}
