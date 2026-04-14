using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared.Contracts;

namespace Api.Services;

public interface IAgentLoopService
{
    Task<AgentLoopResponse> RunAsync(AgentLoopRequest request, CancellationToken cancellationToken = default);
}

public sealed class AgentLoopService : IAgentLoopService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private static readonly AsyncLocal<List<AgentToolCallTrace>?> ActiveTrace = new();
    private static readonly AsyncLocal<string?> ActiveToolAgent = new();

    private static readonly HashSet<string> NeutralColors =
    [
        "black",
        "white",
        "gray",
        "charcoal",
        "navy",
        "tan",
        "cream"
    ];

    private readonly IClosetService _closetService;
    private readonly IWeatherService _weatherService;
    private readonly IOutfitRecommendationService _recommendationService;
    private readonly ILogger<AgentLoopService> _logger;

    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();

    private readonly ChatClientAgent _stylistAgent;
    private readonly ChatClientAgent _weatherAgent;
    private readonly ChatClientAgent _ruleAgent;
    private readonly ChatClientAgent _closetSearchAgent;

    public AgentLoopService(
        IChatClient chatClient,
        IClosetService closetService,
        IWeatherService weatherService,
        IOutfitRecommendationService recommendationService,
        ILogger<AgentLoopService> logger)
    {
        _closetService = closetService;
        _weatherService = weatherService;
        _recommendationService = recommendationService;
        _logger = logger;

        _stylistAgent = new ChatClientAgent(
            chatClient,
            instructions: "You are a practical wardrobe stylist. Focus on intent and constraints in one short paragraph.",
            name: "stylist-agent");

        _weatherAgent = new ChatClientAgent(
            chatClient,
            instructions: "You are a weather-focused clothing planner. Extract rain/cold/sun constraints concisely.",
            name: "weather-agent");

        _ruleAgent = new ChatClientAgent(
            chatClient,
            instructions: "You are a fashion rule reviewer. Flag pattern clashes and poor color harmony clearly.",
            name: "rule-check-agent");

        _closetSearchAgent = new ChatClientAgent(
            chatClient,
            instructions:
                "You are a closet search agent. Use registered tools and return strict JSON for OutfitCandidateProposal with topId, bottomId, shoesId, hatId, jacketId, usesHybridTopBottom, rationale.",
            name: "closet-search-agent",
            tools:
            [
                AIFunctionFactory.Create(SearchClosetTool, name: "searchCloset"),
                AIFunctionFactory.Create(GetClosetItemByIdTool, name: "getClosetItemById"),
                AIFunctionFactory.Create(CheckPatternCompatibilityTool, name: "checkPatternCompatibility"),
                AIFunctionFactory.Create(CheckColorCompatibilityTool, name: "checkColorCompatibility"),
                AIFunctionFactory.Create(EvaluateWeatherRiskTool, name: "evaluateWeatherRisk"),
                AIFunctionFactory.Create(ValidateOutfitCompletenessTool, name: "validateOutfitCompleteness")
            ]);
    }

    public async Task<AgentLoopResponse> RunAsync(AgentLoopRequest request, CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(request.ConversationId)
            ? Guid.NewGuid().ToString("n")
            : request.ConversationId.Trim();

        if (!_sessions.TryGetValue(key, out var session))
        {
            session = await _stylistAgent.CreateSessionAsync();
            _sessions[key] = session;
        }

        var toolTraces = new List<AgentToolCallTrace>();
        var handoffs = new List<AgentHandoffTrace>();

        try
        {
            ActiveTrace.Value = toolTraces;

            var forecast = _weatherService.Get();

            var weatherSummary = await RunWithHandoffAsync(
                handoffs,
                from: "coordinator",
                to: "weather-agent",
                note: "Extract weather constraints",
                () => _weatherAgent.RunAsync($"Forecast: {SummarizeForecast(forecast)}", session, options: null, cancellationToken));

            var styleSummary = await RunWithHandoffAsync(
                handoffs,
                from: "weather-agent",
                to: "stylist-agent",
                note: "Shape style intent",
                () => _stylistAgent.RunAsync($"User prompt: {request.Prompt}. Bold mode: {request.BoldMode}. Weather constraints: {weatherSummary}", session, options: null, cancellationToken));

            ActiveToolAgent.Value = "closet-search-agent";
            var candidateResponse = await RunWithHandoffAsync(
                handoffs,
                from: "stylist-agent",
                to: "closet-search-agent",
                note: "Run iterative closet search with tools",
                () => _closetSearchAgent.RunAsync(
                    $"Prompt: {request.Prompt}\nBold mode: {request.BoldMode}\nWeather constraints: {weatherSummary}\nStyle intent: {styleSummary}\nUse tools repeatedly as needed. Page size should be <= {Math.Clamp(request.PageSize, 4, 20)}. Return JSON only.",
                    session,
                    options: null,
                    cancellationToken));

            ActiveToolAgent.Value = "rule-check-agent";
            var ruleSummary = await RunWithHandoffAsync(
                handoffs,
                from: "closet-search-agent",
                to: "rule-check-agent",
                note: "Check pattern/color compatibility",
                () => _ruleAgent.RunAsync($"Candidate JSON: {candidateResponse}. Forecast: {SummarizeForecast(forecast)}. Give short validation notes.", session, options: null, cancellationToken));

            var candidate = TryParseCandidate(candidateResponse.ToString()) ?? BuildFallbackCandidate(forecast, request.PageSize);
            var explanation = $"Agent workflow summary:\nWeather: {weatherSummary}\nStylist: {styleSummary}\nRules: {ruleSummary}";

            var recommendation = _recommendationService.ValidateCandidate(
                new ChatRequest(request.Prompt, request.BoldMode),
                candidate,
                _closetService.List(),
                forecast,
                explanation);

            return new AgentLoopResponse(
                ExtractConversationId(session) ?? key,
                recommendation,
                candidate,
                toolTraces,
                handoffs,
                UsedFallback: false,
                Summary: "Completed Agent Framework handoff flow with tool-enabled closet search and deterministic validation.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Agent loop failed, falling back to deterministic recommendation.");
            var closet = _closetService.List();
            var forecast = _weatherService.Get();
            var fallbackRecommendation = _recommendationService.Recommend(
                new ChatRequest(request.Prompt, request.BoldMode),
                closet,
                forecast,
                "Agent loop fallback path was used.");

            return new AgentLoopResponse(
                ExtractConversationId(session) ?? key,
                fallbackRecommendation,
                BuildFallbackCandidate(forecast, request.PageSize),
                toolTraces,
                handoffs,
                UsedFallback: true,
                Summary: "Agent loop failed and deterministic fallback was returned.");
        }
        finally
        {
            ActiveTrace.Value = null;
            ActiveToolAgent.Value = null;
        }
    }

    private async Task<string> RunWithHandoffAsync(
        List<AgentHandoffTrace> handoffs,
        string from,
        string to,
        string note,
        Func<Task<AgentResponse>> action)
    {
        handoffs.Add(new AgentHandoffTrace(from, to, note));
        var response = await action();
        return response.ToString();
    }

    [Description("Search closet inventory with bounded paging and filters for role, color, pattern, warmth, and weather safety.")]
    private string SearchClosetTool(
        [Description("Optional role filter, for example Top or Shoes")] OutfitRole? role = null,
        [Description("Optional color filter, for example navy or white")] string? color = null,
        [Description("Optional pattern filter, for example solid or floral")] string? pattern = null,
        [Description("Optional minimum warmth value from 1 to 5")] int? minWarmth = null,
        [Description("Optional maximum warmth value from 1 to 5")] int? maxWarmth = null,
        [Description("Optional waterproof requirement")] bool? waterproof = null,
        [Description("Optional formality filter")] FormalityLevel? formality = null,
        [Description("1-based page number")] int pageNumber = 1,
        [Description("Page size between 1 and 50")] int pageSize = 12)
    {
        var request = new ClosetSearchRequest(
            role is null ? null : [role.Value],
            string.IsNullOrWhiteSpace(color) ? null : [color],
            string.IsNullOrWhiteSpace(pattern) ? null : [pattern],
            waterproof,
            minWarmth,
            maxWarmth,
            formality,
            pageNumber,
            pageSize);

        var result = _closetService.Search(request);
        AddToolTrace("searchCloset", JsonSerializer.Serialize(request, JsonOptions), result.Items.Count, $"Returned {result.Items.Count} items out of {result.TotalCount}.");
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [Description("Get a single closet item by ID.")]
    private string GetClosetItemByIdTool([Description("Closet item ID")] Guid itemId)
    {
        var item = _closetService.List().FirstOrDefault(x => x.Id == itemId);
        AddToolTrace("getClosetItemById", itemId.ToString(), item is null ? 0 : 1, item is null ? "Not found" : $"Found {item.Name}");
        return item is null ? "null" : JsonSerializer.Serialize(item, JsonOptions);
    }

    [Description("Check whether two pattern families are compatible for top and bottom pairing.")]
    private string CheckPatternCompatibilityTool(
        [Description("Top pattern, for example solid, striped, floral")] string topPattern,
        [Description("Bottom pattern, for example solid, plaid, floral")] string bottomPattern)
    {
        var top = Normalize(topPattern);
        var bottom = Normalize(bottomPattern);
        var isCompatible = top == "solid" || bottom == "solid" || top == bottom;
        var summary = isCompatible ? "Pattern pairing is acceptable." : "Pattern pairing is risky.";
        AddToolTrace("checkPatternCompatibility", $"{topPattern}/{bottomPattern}", 1, summary);
        return summary;
    }

    [Description("Check whether two colors are harmonious for outfit pairing.")]
    private string CheckColorCompatibilityTool(
        [Description("Primary color from one garment")] string firstColor,
        [Description("Primary color from another garment")] string secondColor)
    {
        var first = Normalize(firstColor);
        var second = Normalize(secondColor);
        var compatible = first == second
            || (NeutralColors.Contains(first) || NeutralColors.Contains(second))
            || (first, second) is ("navy", "white") or ("white", "navy") or ("blue", "tan") or ("tan", "blue");

        var summary = compatible ? "Color pairing is safe or neutral." : "Color pairing may clash.";
        AddToolTrace("checkColorCompatibility", $"{firstColor}/{secondColor}", 1, summary);
        return summary;
    }

    [Description("Evaluate weather-driven outfit requirements from the current forecast.")]
    private string EvaluateWeatherRiskTool()
    {
        var forecast = _weatherService.Get();
        var minTemp = forecast.Segments.Min(s => s.TemperatureC);
        var hasRain = forecast.Segments.Any(s => s.Precipitation is PrecipitationKind.Rain or PrecipitationKind.Drizzle or PrecipitationKind.Snow);
        var hasSun = forecast.Segments.Any(s => s.IsSunny);

        var summary = $"minTemp={minTemp}; rain={hasRain}; sunny={hasSun}";
        AddToolTrace("evaluateWeatherRisk", "{}", 1, summary);
        return summary;
    }

    [Description("Validate candidate outfit completeness for required slots.")]
    private string ValidateOutfitCompletenessTool(
        [Description("Candidate top item id")] Guid? topId,
        [Description("Candidate bottom item id")] Guid? bottomId,
        [Description("Candidate shoes item id")] Guid? shoesId)
    {
        var complete = topId.HasValue && bottomId.HasValue && shoesId.HasValue;
        var summary = complete ? "Complete required slots." : "Missing at least one required slot.";
        AddToolTrace("validateOutfitCompleteness", $"top={topId};bottom={bottomId};shoes={shoesId}", 1, summary);
        return summary;
    }

    private OutfitCandidateProposal BuildFallbackCandidate(DailyForecastDto forecast, int pageSize)
    {
        var boundedPageSize = Math.Clamp(pageSize, 4, 20);
        var hasRain = forecast.Segments.Any(s => s.Precipitation is PrecipitationKind.Rain or PrecipitationKind.Drizzle or PrecipitationKind.Snow);
        var minTemp = forecast.Segments.Min(s => s.TemperatureC);

        var tops = _closetService.Search(new ClosetSearchRequest(Roles: [OutfitRole.Top], PageSize: boundedPageSize)).Items;
        var bottoms = _closetService.Search(new ClosetSearchRequest(Roles: [OutfitRole.Bottom], PageSize: boundedPageSize)).Items;
        var shoes = _closetService.Search(new ClosetSearchRequest(Roles: [OutfitRole.Shoes], Waterproof: hasRain ? true : null, PageSize: boundedPageSize)).Items;
        var jackets = _closetService.Search(new ClosetSearchRequest(Roles: [OutfitRole.Jacket], Waterproof: hasRain ? true : null, MinWarmth: minTemp <= 10 ? 3 : null, PageSize: boundedPageSize)).Items;
        var hats = _closetService.Search(new ClosetSearchRequest(Roles: [OutfitRole.Hat], PageSize: boundedPageSize)).Items;

        return new OutfitCandidateProposal(
            tops.FirstOrDefault()?.Id,
            bottoms.FirstOrDefault()?.Id,
            shoes.FirstOrDefault()?.Id,
            hats.FirstOrDefault()?.Id,
            jackets.FirstOrDefault()?.Id,
            UsesHybridTopBottom: false,
            Rationale: "Fallback candidate produced from bounded closet searches.");
    }

    private void AddToolTrace(string toolName, string arguments, int resultCount, string summary)
    {
        if (ActiveTrace.Value is null)
        {
            return;
        }

        ActiveTrace.Value.Add(new AgentToolCallTrace(
            Agent: ActiveToolAgent.Value ?? "unknown-agent",
            Tool: toolName,
            Arguments: arguments,
            ResultCount: resultCount,
            Summary: summary));
    }

    private OutfitCandidateProposal? TryParseCandidate(string raw)
    {
        var payload = raw.Trim();

        if (payload.StartsWith("```") && payload.Contains('\n'))
        {
            var firstNewLine = payload.IndexOf('\n');
            var lastFence = payload.LastIndexOf("```");
            if (firstNewLine > 0 && lastFence > firstNewLine)
            {
                payload = payload[(firstNewLine + 1)..lastFence].Trim();
            }
        }

        try
        {
            return JsonSerializer.Deserialize<OutfitCandidateProposal>(payload, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static string SummarizeForecast(DailyForecastDto forecast) =>
        string.Join(", ", forecast.Segments.Select(s => $"{s.Segment}: {s.TemperatureC}C, {s.Precipitation}, sunny={s.IsSunny}"));

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private static string? ExtractConversationId(AgentSession session)
    {
        return session is ChatClientAgentSession chatSession ? chatSession.ConversationId : null;
    }
}
