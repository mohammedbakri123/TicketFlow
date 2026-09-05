using TicketFlow.Api.Application.Classification;
using TicketFlow.Api.Domain.Tickets;

namespace TicketFlow.Tests;

public class FakeTicketClassifierTests
{
    [Fact]
    public async Task Valid_ProducesPlausibleWellFormedCandidate()
    {
        var classifier = new FakeTicketClassifier(FakeClassifierMode.Valid);
        var candidate = await classifier.ClassifyAsync(new Ticket { Id = "t-1001", Subject = "s", Body = "b" });

        Assert.Equal("billing", candidate.Category);
        Assert.Equal("high", candidate.Priority);
        Assert.Equal("The customer reports being charged twice.", candidate.Summary);
    }

    [Fact]
    public async Task InvalidCategory_ReturnsUnknownCategoryUncorrected()
    {
        var classifier = new FakeTicketClassifier(FakeClassifierMode.InvalidCategory);
        var candidate = await classifier.ClassifyAsync(new Ticket { Id = "t-1001", Subject = "s", Body = "b" });

        // The fake must NOT silently fix the invalid value: it is untrusted
        // model output and validation is the caller's job.
        Assert.Equal("banana", candidate.Category);
        Assert.Equal("high", candidate.Priority);
    }

    [Fact]
    public async Task InvalidPriority_ReturnsUnknownPriorityUncorrected()
    {
        var classifier = new FakeTicketClassifier(FakeClassifierMode.InvalidPriority);
        var candidate = await classifier.ClassifyAsync(new Ticket { Id = "t-1001", Subject = "s", Body = "b" });

        Assert.Equal("billing", candidate.Category);
        Assert.Equal("urgent", candidate.Priority);
    }

    [Fact]
    public async Task EmptySummary_ReturnsEmptySummary()
    {
        var classifier = new FakeTicketClassifier(FakeClassifierMode.EmptySummary);
        var candidate = await classifier.ClassifyAsync(new Ticket { Id = "t-1001", Subject = "s", Body = "b" });

        Assert.Equal("billing", candidate.Category);
        Assert.Equal("high", candidate.Priority);
        Assert.Equal(string.Empty, candidate.Summary);
    }

    [Fact]
    public async Task Malformed_ReturnsCandidateWithoutAnyUsableField()
    {
        var classifier = new FakeTicketClassifier(FakeClassifierMode.Malformed);
        var candidate = await classifier.ClassifyAsync(new Ticket { Id = "t-1001", Subject = "s", Body = "b" });

        Assert.Null(candidate.Category);
        Assert.Null(candidate.Priority);
        Assert.Null(candidate.Summary);
    }

    [Fact]
    public async Task Throw_ThrowsFromClassifier()
    {
        var classifier = new FakeTicketClassifier(FakeClassifierMode.Throw);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => classifier.ClassifyAsync(new Ticket { Id = "t-1001", Subject = "s", Body = "b" }));
    }

    [Fact]
    public async Task ClassifyAsync_IsDeterministic_NoRandomBehavior()
    {
        var first = await new FakeTicketClassifier(FakeClassifierMode.Valid)
            .ClassifyAsync(new Ticket { Id = "t-1001", Subject = "s", Body = "b" });
        var second = await new FakeTicketClassifier(FakeClassifierMode.Valid)
            .ClassifyAsync(new Ticket { Id = "t-1002", Subject = "other", Body = "other" });

        // Same mode always yields the same output, regardless of the ticket.
        Assert.Equal(first, second);
    }

    [Fact]
    public void DefaultConstructor_DefaultsToRandomMode()
    {
        var classifier = new FakeTicketClassifier();
        Assert.Equal(FakeClassifierMode.Random, classifier.Mode);
    }

    [Fact]
    public async Task Random_ProducesVariedBehaviorsAcrossCalls()
    {
        var classifier = new FakeTicketClassifier(FakeClassifierMode.Random);
        var ticket = new Ticket { Id = "t-1001", Subject = "s", Body = "b" };
        var outcomes = new HashSet<string>();

        // Run multiple iterations to observe random behaviors (including exceptions).
        for (var i = 0; i < 50; i++)
        {
            try
            {
                var candidate = await classifier.ClassifyAsync(ticket);
                outcomes.Add($"{candidate.Category}|{candidate.Priority}|{candidate.Summary}");
            }
            catch (InvalidOperationException)
            {
                outcomes.Add("THROW");
            }
        }

        // Over 50 runs across 6 behaviors, we should observe multiple distinct outcomes.
        Assert.True(outcomes.Count > 1);
    }
}
