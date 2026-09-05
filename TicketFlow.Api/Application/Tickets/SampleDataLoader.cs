namespace TicketFlow.Api.Application.Tickets;

using System.Net.Http.Json;
using System.Text.Json;

public static class SampleDataLoader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public static async Task<int> LoadAsync(string[] args)
    {
        var sampleFilePath = FindSampleFile();
        if (sampleFilePath is null)
        {
            Console.Error.WriteLine("Error: sample-tickets.json not found in the current or parent directories.");
            return 1;
        }

        List<CreateTicketRequest>? tickets;
        try
        {
            var json = await File.ReadAllTextAsync(sampleFilePath);
            tickets = JsonSerializer.Deserialize<List<CreateTicketRequest>>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error reading {sampleFilePath}: {ex.Message}");
            return 1;
        }

        if (tickets is null || tickets.Count == 0)
        {
            Console.Error.WriteLine("Error: sample-tickets.json contains no tickets.");
            return 1;
        }

        var baseUrl = GetBaseUrl(args);
        Console.WriteLine($"Submitting {tickets.Count} sample tickets to {baseUrl}/tickets...");

        using var httpClient = new HttpClient { BaseAddress = new Uri(baseUrl) };

        var successCount = 0;
        foreach (var ticket in tickets)
        {
            try
            {
                var response = await httpClient.PostAsJsonAsync("/tickets", ticket);
                var statusCode = (int)response.StatusCode;
                if (response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[{statusCode}] Submitted {ticket.Id}: {ticket.Subject}");
                    successCount++;
                }
                else
                {
                    var body = await response.Content.ReadAsStringAsync();
                    Console.Error.WriteLine($"[{statusCode}] Failed {ticket.Id}: {body}");
                }
            }
            catch (HttpRequestException ex)
            {
                Console.Error.WriteLine($"\nFailed to connect to TicketFlow API at {baseUrl}: {ex.Message}");
                Console.Error.WriteLine("Ensure the API is running before loading samples (e.g. 'dotnet run --project TicketFlow.Api').");
                return 1;
            }
        }

        Console.WriteLine($"Successfully submitted {successCount} of {tickets.Count} sample tickets.");
        return successCount == tickets.Count ? 0 : 1;
    }

    private static string GetBaseUrl(string[] args)
    {
        foreach (var arg in args)
        {
            if (arg.StartsWith("--url=", StringComparison.OrdinalIgnoreCase))
            {
                return arg["--url=".Length..].TrimEnd('/');
            }
        }

        return (Environment.GetEnvironmentVariable("API_BASE_URL") ?? "http://localhost:5024").TrimEnd('/');
    }

    private static string? FindSampleFile()
    {
        var directoriesToSearch = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var startDir in directoriesToSearch)
        {
            var current = new DirectoryInfo(startDir);
            while (current != null)
            {
                var candidate = Path.Combine(current.FullName, "sample-tickets.json");
                if (File.Exists(candidate))
                {
                    return candidate;
                }

                current = current.Parent;
            }
        }

        return null;
    }
}
