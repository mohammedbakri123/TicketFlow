using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketFlow.Api.Application.Classification;

namespace TicketFlow.Tests.Classification;

public class ClassificationServiceExtensionsTests
{
    [Fact]
    public void AddTicketClassification_WhenProviderIsFake_RegistersFakeTicketClassifier()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AI_PROVIDER"] = "fake"
            })
            .Build();

        services.AddTicketClassification(configuration);
        var provider = services.BuildServiceProvider();

        var classifier = provider.GetService<ITicketClassifier>();
        var validator = provider.GetService<ITicketClassificationValidator>();

        Assert.NotNull(classifier);
        Assert.IsType<FakeTicketClassifier>(classifier);
        Assert.NotNull(validator);
        Assert.IsType<TicketClassificationValidator>(validator);
    }

    [Fact]
    public void AddTicketClassification_WhenGeminiWithValidConfig_RegistersGeminiTicketClassifier()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AI_PROVIDER"] = "gemini",
                ["GEMINI_API_KEY"] = "fake-test-key-for-di",
                ["AI_MODEL"] = "gemini-2.5-flash"
            })
            .Build();

        services.AddTicketClassification(configuration);
        var provider = services.BuildServiceProvider();

        var chatClient = provider.GetService<IChatClient>();
        var classifier = provider.GetService<ITicketClassifier>();
        var validator = provider.GetService<ITicketClassificationValidator>();

        Assert.NotNull(chatClient);
        Assert.NotNull(classifier);
        Assert.IsType<GeminiTicketClassifier>(classifier);
        Assert.NotNull(validator);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddTicketClassification_WhenGeminiAndMissingApiKey_ThrowsInvalidOperationException(string? apiKey)
    {
        var services = new ServiceCollection();
        var configDict = new Dictionary<string, string?>
        {
            ["AI_PROVIDER"] = "gemini",
            ["AI_MODEL"] = "gemini-2.5-flash"
        };
        if (apiKey != null)
        {
            configDict["GEMINI_API_KEY"] = apiKey;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddTicketClassification(configuration));

        Assert.Contains("GEMINI_API_KEY is required", ex.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddTicketClassification_WhenGeminiAndMissingModel_ThrowsInvalidOperationException(string? model)
    {
        var services = new ServiceCollection();
        var configDict = new Dictionary<string, string?>
        {
            ["AI_PROVIDER"] = "gemini",
            ["GEMINI_API_KEY"] = "some-key"
        };
        if (model != null)
        {
            configDict["AI_MODEL"] = model;
        }

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(configDict)
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddTicketClassification(configuration));

        Assert.Contains("AI_MODEL is required", ex.Message);
    }

    [Fact]
    public void AddTicketClassification_WhenUnsupportedProvider_ThrowsInvalidOperationException()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AI_PROVIDER"] = "unsupported-provider"
            })
            .Build();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddTicketClassification(configuration));

        Assert.Contains("Unsupported AI_PROVIDER 'unsupported-provider'", ex.Message);
    }

    [Fact]
    public void AddTicketClassification_NullArguments_ThrowsArgumentNullException()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        Assert.Throws<ArgumentNullException>(() => ClassificationServiceExtensions.AddTicketClassification(null!, config));
        Assert.Throws<ArgumentNullException>(() => ClassificationServiceExtensions.AddTicketClassification(services, null!));
    }
}
