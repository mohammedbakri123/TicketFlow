using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using TicketFlow.Api.Application.Tickets;
using TicketFlow.Api.Infrastructure.Persistence;

LoadEnv();

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration["DATABASE_CONNECTION_STRING"]
    ?? builder.Configuration.GetConnectionString("DATABASE_CONNECTION_STRING");

builder.Services.AddDbContext<TicketFlowDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

builder.Services.AddScoped<TicketService>();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Serialize enums as camelCase strings, e.g. "pending", "billing", "high".
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});

var app = builder.Build();

app.MapTicketEndpoints();

app.Run();

static void LoadEnv()
{
    var directoriesToSearch = new[]
    {
        Directory.GetCurrentDirectory(),
        AppContext.BaseDirectory
    };

    foreach (var dir in directoriesToSearch)
    {
        var current = new DirectoryInfo(dir);
        while (current != null)
        {
            var envFilePath = Path.Combine(current.FullName, ".env");
            if (File.Exists(envFilePath))
            {
                foreach (var line in File.ReadAllLines(envFilePath))
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                        continue;

                    var separatorIndex = trimmed.IndexOf('=');
                    if (separatorIndex <= 0)
                        continue;

                    var key = trimmed[..separatorIndex].Trim();
                    var value = trimmed[(separatorIndex + 1)..].Trim();

                    if (value.Length >= 2 &&
                        ((value.StartsWith('"') && value.EndsWith('"')) ||
                         (value.StartsWith('\'') && value.EndsWith('\''))))
                    {
                        value = value[1..^1];
                    }

                    if (Environment.GetEnvironmentVariable(key) is null)
                    {
                        Environment.SetEnvironmentVariable(key, value);
                    }
                }
                return;
            }

            current = current.Parent;
        }
    }
}


public partial class Program { }
