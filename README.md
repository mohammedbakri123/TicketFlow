# TicketFlow

## Database Setup

1. **Create local environment file**
   Copy the example environment configuration:
   ```bash
   cp .env.example .env
   ```

2. **Configure PostgreSQL connection string**
   Update `.env` with your PostgreSQL connection string:
   ```env
   DATABASE_CONNECTION_STRING=Host=localhost;Port=5432;Database=ticketflow;Username=postgres;Password=postgres
   AI_PROVIDER=openai
   AI_MODEL=gpt-4o-mini
   ```

3. **Run EF Core migrations**
   Apply the database migrations to create the database schema:
   ```bash
   dotnet ef database update --project TicketFlow.Api
   ```

4. **Run the application**
   ```bash
   dotnet run --project TicketFlow.Api
   ```
