using Microsoft.EntityFrameworkCore;
using TicketFlow.Api.Infrastructure.Persistence;

LoadEnv();

var builder = WebApplication.CreateBuilder(args);

var connectionString = builder.Configuration["DATABASE_CONNECTION_STRING"]
    ?? builder.Configuration.GetConnectionString("DATABASE_CONNECTION_STRING");

builder.Services.AddDbContext<TicketFlowDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

var app = builder.Build();

app.MapGet("/", () => "Hello World!");

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

