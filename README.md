# TicketFlow

TicketFlow is an asynchronous ticket classification service built for the Loura engineering take-home assignment. It ingests customer support tickets over HTTP, stores them in PostgreSQL with a `pending` status, classifies them in the background using an LLM, and exposes endpoints to retrieve and list classified tickets.

---

## 1. Running Locally

### Prerequisites
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- Docker (for PostgreSQL)

### Clean-Clone Setup

1. **Clone the repository**:
   ```bash
   git clone <repo-url>
   cd TicketFlow
   ```

2. **Start PostgreSQL**:
   ```bash
   docker compose up -d
   ```
   Docker Compose starts a PostgreSQL 17 instance. The credentials and database name (`ticketflow`, `postgres`/`postgres`) match `.env.example`.

3. **Copy the environment file**:
   ```bash
   cp .env.example .env
   ```
   The configuration defaults to `AI_PROVIDER=fake`, allowing the service to run out of the box with zero setup and no API key. Using Google Gemini is optional (set `AI_PROVIDER=gemini` and supply `GEMINI_API_KEY`).

4. **Start the API**:
   ```bash
   dotnet run --project TicketFlow.Api
   ```
   Entity Framework Core migrations are applied automatically when the application starts—no manual database, user, or table creation is needed.

The API listens on **`http://localhost:5024`** (Swagger UI: `http://localhost:5024/swagger`).

---

### Ingest & Verify a Ticket

1. **Submit a ticket**:
   ```bash
   curl -X POST http://localhost:5024/tickets \
     -H "Content-Type: application/json" \
     -d '{
       "id": "t-1001",
       "subject": "Charged twice this month",
       "body": "Hi, I see two charges of 49.00 on my card statement dated the 3rd and the 4th. I only have one subscription. Can you refund one of them?"
     }'
   ```
   **Response (`202 Accepted`)**:
   ```json
   {
     "id": "t-1001",
     "status": "pending"
   }
   ```

2. **Retrieve the ticket and observe classification**:
   ```bash
   curl http://localhost:5024/tickets/t-1001
   ```
   **Response (after background classification completes)**:
   ```json
   {
     "id": "t-1001",
     "subject": "Charged twice this month",
     "body": "Hi, I see two charges of 49.00 on my card statement dated the 3rd and the 4th. Can you refund one of them?",
     "status": "classified",
     "category": "billing",
     "priority": "high",
     "summary": "Customer was charged twice for their subscription and requests a refund.",
     "attempts": 1,
     "createdAt": "2026-09-05T20:00:00Z",
     "updatedAt": "2026-09-05T20:00:02Z"
   }
   ```

---

## 2. Loading Sample Tickets

The 10 sample tickets from the assignment appendix are provided in `sample-tickets.json`.

While the service is running, load all 10 tickets using the .NET CLI:
```bash
dotnet run --project TicketFlow.Api -- --load-samples
```
This submits the 10 appendix tickets through the normal `POST /tickets` HTTP endpoint.

You can then query the list endpoint to view the results:
```bash
curl "http://localhost:5024/tickets?pageSize=50"
```

---

## 3. Design Decisions

- **Storage**: PostgreSQL via Entity Framework Core. Relational persistence provides ACID durability for tickets and status transitions. The primary key on `tickets.Id` acts as the definitive uniqueness constraint: duplicate submissions lose the database race, are caught as unique constraint violations, and are treated as idempotent no-ops without re-running classification.
- **Asynchronous Work**: An in-process `BackgroundService` (`ClassificationWorker`) paired with an in-memory `System.Threading.Channels` signal (`ChannelTicketWorkSignal`). The ingestion endpoint writes the ticket with status `pending`, signals the channel, and returns `202 Accepted` immediately. The HTTP request never invokes the model.
- **Concurrency**: Bounded parallelism (`MaxDegreeOfParallelism = 4`) using `Parallel.ForEachAsync`. Each parallel classification executes within its own async dependency injection scope and `DbContext` instance, avoiding shared entity tracking issues and keeping provider connection load predictable.
- **Restart Behavior**: PostgreSQL is the durable source of truth. On startup, `ClassificationWorker` executes an initial recovery query for all tickets in `pending` status. Any in-flight work interrupted by a crash or restart is picked up and processed even if its in-memory signal was lost.
- **Retry Policy & Failure Handling**: Up to 3 attempts. When a classification fails—due to provider network timeouts, rate limits (HTTP 429), unparseable JSON, or schema validation failures—the worker increments `attempts` and leaves the ticket `pending` for retry on a 5-second interval. Once `attempts` reaches 3, the ticket transitions to `failed`. Malformed or unvalidated model outputs are never persisted.
- **Prompt Injection & Untrusted Content**: Prompts use structural role isolation (`ChatRole.System` vs `ChatRole.User`) and XML boundary tags (`<ticket-subject>` and `<ticket-body>`) to separate untrusted customer data from system instructions. System instructions explicitly command the model to ignore roleplay or prompt overrides inside ticket content. Crucially, the model is given no executable tools or functions (`Tools = null`), and all outputs must satisfy server-side validation allow-lists.
- **API Shape**:
  - `POST /tickets`: Accepts `{ id, subject, body }`, validates presence and field lengths, persists as `pending`, and returns `202 Accepted` with a `Location` header. Duplicate IDs are accepted as idempotent no-ops.
  - `GET /tickets/{id}`: Returns `200 OK` with full ticket details, or `404 Not Found`.
  - `GET /tickets`: Returns paginated ticket summaries (`page`, `pageSize`, `total`), filterable by `status`, `category`, and `priority`. Validation failures return standard RFC 7807 problem details.
  - `POST /tickets/{id}/reclassify`: Re-queues a Classified or Failed ticket for asynchronous classification by resetting it to Pending and returning `202 Accepted`. Returns `404` if the ticket does not exist and `409` if it is already Pending.

  Ticket bodies are limited to 100,000 characters. Status, category, and priority filters accept only their documented names; numeric enum values are rejected.

---

## 4. Model Boundary

The AI provider is treated strictly as an **unreliable, untrusted external dependency**:
- The classifier (`GeminiTicketClassifier`) returns raw, unvalidated strings as a `ClassificationCandidate` without attempting internal normalization or auto-repair (e.g., `"urgent"` is not converted to `"high"`).
- The candidate must pass through `ITicketClassificationValidator`, which enforces strict allow-lists (`billing`, `technical`, `account`, `other` and `low`, `medium`, `high`) and summary length constraints (5–1,000 characters).
- Only candidates that pass validation are mapped to typed domain enums and persisted as `classified`. Invalid outputs are rejected and trigger the failure/retry workflow.

---

## 5. Tests

The test suite covers:
- **Model Candidate Deserialization**: Correct mapping of structured LLM responses into `ClassificationCandidate`.
- **Malformed & Empty Response Handling**: Graceful error handling and retry triggering for non-JSON or null model payloads.
- **Strict Validation Without Normalization**: Verifying that unexpected strings are rejected rather than silently repaired.
- **Prompt Isolation & Adversarial Containment**: Verifying XML data framing and ignoring prompt injection commands (such as `t-1005`).
- **Worker Concurrency, Retries & State Transitions**: Bounded parallelism, failure retries up to attempt limits, stale update prevention, and startup crash recovery.
- **HTTP Endpoint Behavior**: Ingestion idempotency, asynchronous signaling, body-size validation, retrieval, filtering, pagination, and invalid query filters.
- **Service Registration & Configuration**: DI lifecycle validation and missing environment variable handling.

All tests use in-memory repositories and an `IChatClient` test double; **no API key, network access, or running database is required to run tests**.

Execute the tests with:
```bash
dotnet test
```

---

## 6. Weaknesses & Limitations

- **In-process wake-up signal**: `System.Threading.Channels` is local to a single process. In a multi-replica deployment, an instance would not receive wake-up signals for tickets inserted by sibling nodes (though the startup recovery query and retry polling would eventually process them).
- **At-least-once classification**: Classification semantics are at-least-once rather than strictly exactly-once model execution. If the process crashes after the LLM responds but before the result is saved to PostgreSQL, the ticket will be re-classified upon restart.
- **Single-instance concurrency**: The background worker does not use distributed locks (such as PostgreSQL advisory locks or `FOR UPDATE SKIP LOCKED`). Running multiple instances against the same database would result in concurrent duplicate classification attempts on the same pending tickets.
- **Fixed retry backoff**: Retries poll on a fixed 5-second interval rather than using exponential backoff with jitter or honoring provider `Retry-After` headers.

---

## 7. With More Time

- **Distributed Queue / Outbox Pattern:** Replace the in-process Channel with a distributed broker (e.g., RabbitMQ or AWS SQS) or a transactional outbox with `FOR UPDATE SKIP LOCKED` to support horizontal scaling across multiple worker replicas without duplicate processing.
- **Stronger prompt-injection resistance:** Expand safeguards and test against a broader set of adversarial ticket content.
- **Stronger summary validation:** Validate that generated summaries are concise, single-sentence, and representative of the ticket.
- **Retry resilience:** Replace the fixed retry interval with exponential backoff and jitter.
