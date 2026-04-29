# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Running the app

This is a .NET Aspire-orchestrated app. The whole stack (Ollama container + Open WebUI + Api + Web) is started together — do not run individual projects directly.

```bash
aspire run --project AppHost      # Start everything (preferred)
aspire start                      # Same, using aspire.config.json profile
aspire describe                   # List resources and endpoints
aspire logs [resource]            # Tail console logs for a resource
aspire otel logs [resource]       # Structured logs (OpenTelemetry)
aspire stop                       # Stop the app
```

The first run pulls two Ollama models into the `ollama-data` volume: `granite4:3b` (chat/reasoning, ~2 GB) and `nomic-embed-text:v1.5` (embeddings for closet semantic search). Subsequent runs reuse both. A container runtime (Podman or Docker Desktop) must be running.

Build / restore individual projects with `dotnet build` against [ai-wardrobe-example.slnx](ai-wardrobe-example.slnx). Target framework is **net10.0**. There is no test project in the repo.

Swagger UI is available at the Api's `/swagger` in Development mode. [Api/Api.http](Api/Api.http) contains sample requests.

## Architecture

Five projects, one solution ([ai-wardrobe-example.slnx](ai-wardrobe-example.slnx)):

- **[AppHost](AppHost/Program.cs)** — Aspire host. Wires Ollama (with Open WebUI sidecar) + Qdrant → Api → Web. Passes `Ollama__Model` and `Ollama__Embeddings` env vars to Api, and uses Aspire service discovery to give Web the Api endpoint via `services:api:http:0`.
- **[Api](Api/Program.cs)** — ASP.NET Core minimal API. Registers two keyed `IChatClient`/`IEmbeddingGenerator` instances (`"chat"` for `granite4:3b`, `"embeddings"` for `nomic-embed-text:v1.5`) and a `QdrantClient` via `AddQdrantClient("vector-db")`. Routes are registered via extension methods in [Api/Extensions/](Api/Extensions/) — closet, weather, and chat endpoints.
- **[Web](Web/Program.cs)** — Blazor Server. Single typed [WardrobeApiClient](Web/Services/WardrobeApiClient.cs) for Api calls. Main page is [Components/Pages/Home.razor](Web/Components/Pages/Home.razor) with chat / closet / weather tabs.
- **[Shared](Shared/Contracts/)** — DTOs and request/response records used by both Api and Web. **All cross-project contracts live here** — when adding new endpoints, add the types to Shared first.
- **[ServiceDefaults](ServiceDefaults/Extensions.cs)** — Standard Aspire telemetry/health-check defaults. Both Api and Web call `builder.AddServiceDefaults()`.

### Agent workflow ([Api/Services/AgentLoopService.cs](Api/Services/AgentLoopService.cs))

The core feature is a multi-agent outfit recommendation workflow built on **Microsoft.Agents.AI.Workflows**. It is a directed graph of `Executor<TIn, TOut>` nodes, not a free-form agent loop. The graph is built per-request inside `StreamAsync`:

```
WeatherExecutor → InitialStylistExecutor → ValidateOutfitExecutor
                                            ↓ (NeedsRetry=true)        ↓ (NeedsRetry=false)
                                          RetryStylistExecutor       OutputExecutor
                                            ↓
                                          ValidateOutfitExecutor (loop)
```

Three `ChatClientAgent`s are constructed once in the service constructor: `_weatherAgent`, `_stylistAgent`, `_summaryAgent`. They share a single `IChatClient` configured in `Program.cs` with `.UseFunctionInvocation()` and OpenTelemetry. Tools (`searchCloset`, `getClosetItemById`, `evaluateWeatherRisk`) are registered via `AIFunctionFactory.Create` and bound to instance methods on `AgentLoopService`.

Key behaviors to preserve when modifying the workflow:

- **Validation is deterministic, not LLM-driven.** [ValidateOutfitExecutor](Api/Services/AgentLoopService.cs) parses stylist output for IDs (regex `[a-z]{4}\d{4}`), checks roles against the closet, and decides whether retry is needed. The stylist agent is instructed to output ONLY a comma-separated ID list — do not change that contract without updating the regex and the validator.
- **Retry budget** comes from `MaxAttempts` (clamped 2–8 from `AgentLoopRequest.MaxToolCalls`). When attempts are exhausted, `NeedsRetry` becomes false even if invalid, and `OutputExecutor` produces a failure message.
- **Streaming events.** The workflow emits `WorkflowDebugEvent` (a custom event subclass) for agent messages, validation results, and final output. The Api translates these into `AgentLoopStreamEvent` records and writes NDJSON to the response. The browser consumes the stream live.
- **Conversation state** (rolling summary + recent turns) is kept in-memory in `_conversationState` keyed by `ConversationId`. When summary+transcript+prompt exceeds `ContextCharBudget` (5000 chars), `_summaryAgent` collapses old turns. State is process-local; restarting Api drops history.
- **Cancellation.** [ConversationCancellationManager](Api/Services/ConversationCancellationManager.cs) tracks per-conversation `CancellationTokenSource`s. `POST /api/chat/stop/{conversationId}` triggers it. Always create a linked CTS combining the request token and the conversation token before passing the token down.

### Closet storage and search

[ClosetService](Api/Services/ClosetService.cs) uses **Qdrant** (a vector database running in a container with a persistent `qdrant-data` volume) — not an in-memory collection. The collection is created and seeded only when it doesn't exist. Changes made via the REST API survive Api restarts. `POST /api/closet/reset` drops and recreates the collection with seed data.

`ClosetItemDto.Description` is a computed property that produces a natural-language sentence about the item; this string is embedded via `nomic-embed-text:v1.5` and stored as the Qdrant vector. Search has two strategies: when a `Description` is provided, a cosine-similarity vector search is run; without one, all items are scrolled and filtered in Qdrant using the built-in field filters (role, color, pattern, waterproof, warmth range, formality).

### Tool argument parsing

The Ollama function-calling format sometimes wraps scalar arguments in single-element arrays. Tool methods on `AgentLoopService` accept `JsonElement?` and use the `ParseOptional*` helpers to handle both shapes. When adding new tools, follow the same pattern rather than typed parameters.

### Closet IDs

IDs follow `{rolePrefix}{4-digit}` where prefixes are: `tops`, `bttm`, `shoe`, `hats`, `jckt` (see [ClosetService.RolePrefixes](Api/Services/ClosetService.cs)). The stylist agent's prompt and the ID-extraction regex both depend on this format.

## Project conventions

- **net10.0** target with `Nullable` and `ImplicitUsings` enabled across all projects.
- File-scoped namespaces, primary constructors, and collection expressions (`[...]`) are used throughout — match the surrounding style.
- The closet store is backed by Qdrant and seeded on first run. Weather is simulated ([WeatherService](Api/Services/WeatherService.cs)), not from a real API.
