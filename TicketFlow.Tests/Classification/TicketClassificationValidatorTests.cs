using TicketFlow.Api.Application.Classification;
using TicketFlow.Api.Domain.Tickets;

namespace TicketFlow.Tests.Classification;

public class TicketClassificationValidatorTests
{
    private readonly TicketClassificationValidator _validator = new();

    // ---- Valid candidates ----

    [Fact]
    public void ValidCandidate_ReturnsValid()
    {
        var candidate = new ClassificationCandidate("billing", "high", "Customer charged twice.");
        var result = _validator.Validate(candidate);

        Assert.True(result.IsValid);
        Assert.NotNull(result.Classification);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ValidCandidate_MapsToCorrectEnums()
    {
        var candidate = new ClassificationCandidate("technical", "medium", "Network issue reported.");
        var result = _validator.Validate(candidate);

        Assert.True(result.IsValid);
        Assert.Equal(TicketCategory.Technical, result.Classification!.Category);
        Assert.Equal(TicketPriority.Medium, result.Classification.Priority);
        Assert.Equal("Network issue reported.", result.Classification.Summary);
    }

    [Theory]
    [InlineData("BILLING", "HIGH")]
    [InlineData("Billing", "High")]
    [InlineData("bIlLiNg", "hIgH")]
    public void ValidCandidate_CaseInsensitive(string category, string priority)
    {
        var candidate = new ClassificationCandidate(category, priority, "Summary text.");
        var result = _validator.Validate(candidate);

        Assert.True(result.IsValid);
        Assert.Equal(TicketCategory.Billing, result.Classification!.Category);
        Assert.Equal(TicketPriority.High, result.Classification.Priority);
    }

    [Fact]
    public void ValidCandidate_AllCategoryValues()
    {
        var categories = new (string Input, TicketCategory Expected)[]
        {
            ("billing", TicketCategory.Billing),
            ("technical", TicketCategory.Technical),
            ("account", TicketCategory.Account),
            ("other", TicketCategory.Other),
        };

        foreach (var (input, expected) in categories)
        {
            var result = _validator.Validate(new ClassificationCandidate(input, "low", "Summary."));
            Assert.True(result.IsValid, $"Category '{input}' should be valid.");
            Assert.Equal(expected, result.Classification!.Category);
        }
    }

    [Fact]
    public void ValidCandidate_AllPriorityValues()
    {
        var priorities = new (string Input, TicketPriority Expected)[]
        {
            ("low", TicketPriority.Low),
            ("medium", TicketPriority.Medium),
            ("high", TicketPriority.High),
        };

        foreach (var (input, expected) in priorities)
        {
            var result = _validator.Validate(new ClassificationCandidate("billing", input, "Summary."));
            Assert.True(result.IsValid, $"Priority '{input}' should be valid.");
            Assert.Equal(expected, result.Classification!.Priority);
        }
    }

    [Fact]
    public void SummaryAtMaxLength_ReturnsValid()
    {
        var summary = new string('x', TicketClassificationValidator.MaxSummaryLength);
        var candidate = new ClassificationCandidate("billing", "high", summary);
        var result = _validator.Validate(candidate);

        Assert.True(result.IsValid);
        Assert.Equal(summary, result.Classification!.Summary);
    }

    [Fact]
    public void SummaryAtMinLength_ReturnsValid()
    {
        var summary = new string('x', TicketClassificationValidator.MinSummaryLength);
        var candidate = new ClassificationCandidate("billing", "high", summary);
        var result = _validator.Validate(candidate);

        Assert.True(result.IsValid);
        Assert.Equal(summary, result.Classification!.Summary);
    }

    [Fact]
    public void SummaryWithSurroundingWhitespace_ReturnsValidAndTrimmed()
    {
        var candidate = new ClassificationCandidate(
            "billing", "high", "   Customer was charged twice.   ");
        var result = _validator.Validate(candidate);

        Assert.True(result.IsValid);
        Assert.Equal("Customer was charged twice.", result.Classification!.Summary);
    }

    // ---- Invalid category ----

    [Fact]
    public void InvalidCategory_ReturnsInvalid()
    {
        var candidate = new ClassificationCandidate("banana", "high", "Summary.");
        var result = _validator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Null(result.Classification);
        Assert.Contains(result.Errors, e => e.Contains("banana"));
    }

    [Fact]
    public void NullCategory_ReturnsInvalid()
    {
        var candidate = new ClassificationCandidate(null, "high", "Summary.");
        var result = _validator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Null(result.Classification);
        Assert.Contains(result.Errors, e => e.Contains("Category"));
    }

    // ---- Invalid priority ----

    [Fact]
    public void InvalidPriority_ReturnsInvalid()
    {
        var candidate = new ClassificationCandidate("billing", "urgent", "Summary.");
        var result = _validator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Null(result.Classification);
        Assert.Contains(result.Errors, e => e.Contains("urgent"));
    }

    [Fact]
    public void NullPriority_ReturnsInvalid()
    {
        var candidate = new ClassificationCandidate("billing", null, "Summary.");
        var result = _validator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Null(result.Classification);
        Assert.Contains(result.Errors, e => e.Contains("Priority"));
    }

    // ---- Invalid summary ----

    [Fact]
    public void EmptySummary_ReturnsInvalid()
    {
        var candidate = new ClassificationCandidate("billing", "high", string.Empty);
        var result = _validator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Null(result.Classification);
        Assert.Contains(result.Errors, e => e.Contains("Summary"));
    }

    [Fact]
    public void WhitespaceSummary_ReturnsInvalid()
    {
        var candidate = new ClassificationCandidate("billing", "high", "   ");
        var result = _validator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Null(result.Classification);
        Assert.Contains(result.Errors, e => e.Contains("Summary"));
    }

    [Fact]
    public void NullSummary_ReturnsInvalid()
    {
        var candidate = new ClassificationCandidate("billing", "high", null);
        var result = _validator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Null(result.Classification);
        Assert.Contains(result.Errors, e => e.Contains("Summary"));
    }

    [Fact]
    public void SummaryShorterThanMinLength_ReturnsInvalid()
    {
        var candidate = new ClassificationCandidate("billing", "high", "Four");
        var result = _validator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Null(result.Classification);
        Assert.Contains(result.Errors, e => e.Contains($"at least {TicketClassificationValidator.MinSummaryLength}"));
    }

    [Fact]
    public void SummaryShorterThanMinLengthAfterTrim_ReturnsInvalid()
    {
        var candidate = new ClassificationCandidate("billing", "high", "   Hi   ");
        var result = _validator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Null(result.Classification);
        Assert.Contains(result.Errors, e => e.Contains($"at least {TicketClassificationValidator.MinSummaryLength}"));
    }

    [Fact]
    public void OversizedSummary_ReturnsInvalid()
    {
        var summary = new string('x', TicketClassificationValidator.MaxSummaryLength + 1);
        var candidate = new ClassificationCandidate("billing", "high", summary);
        var result = _validator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Null(result.Classification);
        Assert.Contains(result.Errors, e => e.Contains("maximum length"));
    }

    // ---- Malformed / compound failures ----

    [Fact]
    public void MalformedCandidate_AllNulls_ReturnsInvalid()
    {
        var candidate = new ClassificationCandidate(null, null, null);
        var result = _validator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.Null(result.Classification);
        // All three fields should produce errors.
        Assert.Equal(3, result.Errors.Count);
    }

    [Fact]
    public void InvalidCandidate_NoPartialAcceptance()
    {
        // Only category is bad; priority and summary are fine.
        var candidate = new ClassificationCandidate("banana", "high", "Good summary.");
        var result = _validator.Validate(candidate);

        // The entire candidate is rejected — Classification is null.
        Assert.False(result.IsValid);
        Assert.Null(result.Classification);
    }

    [Fact]
    public void MultipleErrors_AllCollected()
    {
        // Both category and priority are bad.
        var candidate = new ClassificationCandidate("banana", "urgent", "Good summary.");
        var result = _validator.Validate(candidate);

        Assert.False(result.IsValid);
        Assert.True(result.Errors.Count >= 2);
        Assert.Contains(result.Errors, e => e.Contains("banana"));
        Assert.Contains(result.Errors, e => e.Contains("urgent"));
    }
}
