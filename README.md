# TicketFlow

TicketFlow is a small asynchronous support-ticket classification service built for the Loura engineering take-home. It accepts tickets over HTTP, stores them in PostgreSQL, classifies them in the background, and exposes the results through the API.

## 1. Running Locally

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Docker

### Clean-clone setup

```bash
git clone <repo-url>
cd TicketFlow
docker compose up -d
cp .env.example .env
dotnet run --project TicketFlow.Api
```

PostgreSQL is created by Docker Compose, and EF Core applies pending migrations automatically on startup.

The default configuration uses the fake classifier, so no API key or external model service is required. Gemini is optional:

```env
AI_PROVIDER=gemini
GEMINI_API_KEY=your-key
AI_MODEL=gemini-2.5-flash
```

The API listens on `http://localhost:5024` and Swagger is available at `/swagger`.

### Example

Create a ticket:

```bash
curl -X POST http://localhost:5024/tickets \
  -H "Content-Type: application/json" \
  -d '{
    "id": "t-1001",
    "subject": "Charged twice this month",
    "body": "I see two charges for the same subscription and would like one refunded."
  }'
```

The endpoint returns `202 Accepted` because classification is asynchronous:

```json
{
  "id": "t-1001",
  "status": "pending"
}
```

Then fetch the ticket:

```bash
curl http://localhost:5024/tickets/t-1001
```

The ticket eventually becomes `classified` or `failed`:

```json
{
  "id": "t-1001",
  "subject": "Charged twice this month",
  "body": "I see two charges for the same subscription and would like one refunded.",
  "status": "classified",
  "category": "billing",
  "priority": "high",
  "summary": "Customer was charged twice for their subscription and requests a refund.",
  "attempts": 1,
  "createdAt": "2026-09-05T20:00:00Z",
  "updatedAt": "2026-09-05T20:00:02Z"
}
```

## 2. Loading Sample Tickets

The ten appendix tickets are included in `sample-tickets.json`.

With the API already running:

```bash
dotnet run --project TicketFlow.Api -- --load-samples
```

The loader submits the samples through the normal `POST /tickets` endpoint.

You can inspect them with:

```bash
curl "http://localhost:5024/tickets?pageSize=50"
```

The list endpoint supports `status`, `category`, and `priority` filters plus pagination.

## 3. Design Decisions

### Storage

PostgreSQL is the durable source of truth. The ticket ID is the primary key, so duplicate submissions are decided by the database uniqueness constraint rather than a check-then-insert race.

A duplicate `POST /tickets` is treated as an idempotent no-op: the existing ticket is not replaced and classification is not triggered again.

### Asynchronous work

The create endpoint only validates and persists the ticket as `pending`. A lightweight in-process `System.Threading.Channels` signal wakes the `ClassificationWorker`; it is not the durable work queue.

The worker always queries PostgreSQL for pending tickets, so losing the in-memory signal does not lose persisted work.

### Concurrency

The worker processes up to four tickets concurrently. Each ticket gets its own dependency-injection scope and EF Core `DbContext`, so parallel classifications do not share tracked database state.

### Restart behavior

On startup, the worker performs a recovery scan for pending tickets. A ticket that was persisted before a restart is therefore eligible for classification even if its in-memory wake-up signal was lost.

This is at-least-once classification. A crash after the model responds but before the result is saved can cause the ticket to be classified again after restart.

### Retry policy

A classifier exception or invalid model output counts as a classification attempt.

- Maximum: 3 attempts
- Invalid model output is never persisted
- Attempts below 3 leave the ticket `pending`
- Attempt 3 transitions the ticket to `failed`
- Retries are picked up by a fixed 5-second scan interval

Persistence errors are not counted as model attempts.

### Prompt injection and untrusted content

Ticket subject and body are untrusted data. The Gemini classifier keeps system instructions separate from the user-provided ticket content and wraps the ticket fields in explicit data boundaries.

The system prompt tells the model to ignore commands and role overrides inside the ticket and gives it no tools or executable actions.

These measures reduce risk and limit what an injected ticket can cause, but they do not make prompt injection impossible.

### API shape

- `POST /tickets` accepts `{ id, subject, body }` and returns `202 Accepted` with a `Location` header. Ticket bodies are limited to 100,000 characters.
- Duplicate IDs are idempotent no-ops.
- `GET /tickets/{id}` returns the full ticket or `404`.
- `GET /tickets` supports `status`, `category`, and `priority` filters with pagination.
- `POST /tickets/{id}/reclassify` re-queues a `classified` or `failed` ticket and returns `202`.
- Reclassification returns `409` when the ticket is already `pending`, because resetting an in-flight retry would make the state and attempt count ambiguous.
- Invalid request/filter values return problem details. Status, category, and priority filters accept only documented names; numeric enum values are rejected.

## 4. Model Boundary

The model provider is treated as an unreliable dependency.

```text
provider output
    -> ClassificationCandidate (raw strings)
    -> TicketClassificationValidator
    -> ValidatedClassification (typed values)
    -> persistence
```

The validator only allows the documented category and priority values and applies summary length checks. It does not silently repair values such as `"urgent"` into `"high"`.

Summary validation is intentionally structural; it does not prove that the summary is semantically correct.

## 5. Tests

The tests cover the parts most likely to regress:

- model output parsing and malformed responses
- strict validation without auto-repair
- adversarial/prompt-injection input
- worker concurrency, retries, stale updates, and restart recovery
- HTTP ingestion, idempotency, filtering, pagination, and validation
- ticket reclassification and its `202`/`404`/`409` behavior
- configuration and service registration

Run:

```bash
dotnet test
```

The test suite uses in-memory/test doubles, so it does not require a running PostgreSQL instance, API key, or network access.

## 6. Weaknesses & Limitations

- The wake-up signal is in-process, so it is not suitable as a coordination mechanism between multiple application instances.
- Classification is at-least-once rather than exactly-once.
- The current worker does not coordinate multiple worker replicas, so running several instances against the same database can duplicate model work.
- Retry timing is a fixed 5-second interval rather than exponential backoff with jitter.

## 7. With More Time

I would improve three areas without changing the basic architecture:

- expand prompt-injection testing and hardening
- add stronger checks for whether summaries are concise, single-sentence, and faithful to the ticket
- use exponential retry backoff with jitter
