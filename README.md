# AI Wardrobe Example

An example application that uses a multi-agent AI workflow to recommend outfits from a virtual closet, based on the current weather forecast. Built with .NET Aspire, Blazor, and a locally-running [Ollama](https://ollama.com) model.

## Overview

The app is split into four .NET projects orchestrated by Aspire:

| Project     | Description                                                                       |
| ----------- | --------------------------------------------------------------------------------- |
| **AppHost** | Aspire host — wires up Ollama, the API, and the web frontend                      |
| **Api**     | ASP.NET Core minimal API — exposes closet, weather, and chat/agent-loop endpoints |
| **Web**     | Blazor Server frontend — chat UI, closet browser, and weather viewer              |
| **Shared**  | Shared contracts (DTOs and request/response records) used by Api and Web          |

### Agent workflow

When you send a message in the chat UI with **Agent Loop** mode enabled, the following workflow runs:

1. **WeatherExecutor** — calls the `evaluateWeatherRisk` tool and produces weather-based clothing constraints (temperature, rain, sun).
2. **InitialStylistExecutor** — uses the weather advice and the full closet inventory to select outfit item IDs (top, bottom, shoes, optional jacket and hat).
3. **ValidateOutfitExecutor** — deterministically checks that all required slots are filled and that items have the correct roles; produces structured feedback.
4. **RetryStylistExecutor** _(if needed)_ — re-runs the stylist with the validation feedback until the outfit is complete or the attempt budget is exhausted.
5. **OutputExecutor** — formats the final recommendation message.

All workflow events (handoffs, agent messages, tool calls, validation results) are streamed live to the browser over NDJSON.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Aspire CLI](#install-the-aspire-cli)
- [Podman](https://podman.io/docs/installation) or [Docker Desktop](https://www.docker.com/products/docker-desktop/) (required to run Ollama and Open WebUI containers)

### Install the Aspire CLI

```bash
dotnet tool install -g aspire
```

> Full installation instructions: https://learn.microsoft.com/dotnet/aspire/fundamentals/aspire-sdk-tooling

### Install a container runtime

Choose one:

- **Podman Desktop** — https://podman-desktop.io/  
  After installing, start the Podman machine: `podman machine start`
- **Docker Desktop** — https://www.docker.com/products/docker-desktop/  
  Ensure the Docker daemon is running before launching the app.

## Running the app

```bash
aspire run --project AppHost
```

Aspire will start:

- An **Ollama** container (with the `granite4:3b` model pulled automatically)
- An **Open WebUI** container (direct Ollama access at the URL shown in the dashboard)
- The **Api** service
- The **Web** frontend

Open the Aspire dashboard URL printed in the terminal to see live logs, traces, and resource health. Navigate to the **Web** resource endpoint to open the wardrobe UI.

### First run note

The first run downloads the `granite4:3b` model (~2 GB). Subsequent runs reuse the `ollama-data` Docker/Podman volume and start immediately.

## Project structure

```text
AppHost/          Aspire host project
Api/
  Program.cs      Minimal API routes
  Services/       AgentLoopService, ClosetService, WeatherService, …
Web/
  Components/     Blazor pages and layout
  Services/       WardrobeApiClient (typed HTTP client)
Shared/
  Contracts/      ChatContracts, ClosetContracts, WeatherContracts
ServiceDefaults/  Shared Aspire service defaults (telemetry, health checks)
```

## API endpoints

| Method | Path                          | Description                                 |
| ------ | ----------------------------- | ------------------------------------------- |
| `GET`  | `/api/closet/items`           | List all closet items                       |
| `POST` | `/api/closet/items/search`    | Search closet with filters                  |
| `GET`  | `/api/weather`                | Current simulated forecast                  |
| `POST` | `/api/chat/recommend`         | Single-turn outfit recommendation           |
| `POST` | `/api/chat/agent-loop`        | Multi-agent outfit workflow (batch)         |
| `POST` | `/api/chat/agent-loop/stream` | Multi-agent outfit workflow (NDJSON stream) |

A Swagger UI is available at `/swagger` when running in Development mode.

## License

[MIT](LICENSE)
