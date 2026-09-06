namespace TicketFlow.Api.Application.Classification;

using Google.GenAI;
using Microsoft.Extensions.AI;

public static class ClassificationServiceExtensions
{
    /// <summary>
    /// Registers the ticket classification services, validator, and provider-specific classifier
    /// based on the <c>AI_PROVIDER</c> configuration.
    /// Supports "fake" (default) and "gemini" for live provider integration.
    /// </summary>
    public static IServiceCollection AddTicketClassification(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var aiProvider = configuration["AI_PROVIDER"]
            ?? Environment.GetEnvironmentVariable("AI_PROVIDER")
            ?? "fake";

        if (string.Equals(aiProvider, "gemini", StringComparison.OrdinalIgnoreCase))
        {
            var geminiApiKey = configuration["GEMINI_API_KEY"]
                ?? Environment.GetEnvironmentVariable("GEMINI_API_KEY");
            var geminiModel = configuration["AI_MODEL"]
                ?? Environment.GetEnvironmentVariable("AI_MODEL");

            if (string.IsNullOrWhiteSpace(geminiApiKey))
            {
                throw new InvalidOperationException(
                    "GEMINI_API_KEY is required when AI_PROVIDER is 'gemini'. Set GEMINI_API_KEY in your .env file or environment.");
            }

            if (string.IsNullOrWhiteSpace(geminiModel))
            {
                throw new InvalidOperationException(
                    "AI_MODEL is required when AI_PROVIDER is 'gemini'. Set AI_MODEL in your .env file or environment (e.g. 'gemini-2.5-flash').");
            }

            var client = new Client(apiKey: geminiApiKey);
            var chatClient = client.AsIChatClient(geminiModel);
            services.AddSingleton<IChatClient>(chatClient);
            services.AddSingleton<ITicketClassifier, GeminiTicketClassifier>();
        }
        else if (string.Equals(aiProvider, "fake", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<ITicketClassifier, FakeTicketClassifier>();
        }
        else
        {
            throw new InvalidOperationException(
                $"Unsupported AI_PROVIDER '{aiProvider}'. Supported values are: 'gemini', 'fake'.");
        }

        services.AddSingleton<ITicketClassificationValidator, TicketClassificationValidator>();

        return services;
    }
}
