namespace TicketFlow.Tests;

using TicketFlow.Api.Application.Tickets;

public class SampleDataLoaderTests
{
    [Fact]
    public async Task LoadAsync_WhenApiUnreachable_ReturnsExitCodeOne()
    {
        // Use an invalid port that refuses connections immediately
        var args = new[] { "--load-samples", "--url=http://127.0.0.1:59999" };
        var exitCode = await SampleDataLoader.LoadAsync(args);

        Assert.Equal(1, exitCode);
    }
}
