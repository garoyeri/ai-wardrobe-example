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
    private static int ConversationCounter;

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
    private readonly ILogger<AgentLoopService> _logger;

    private readonly ConcurrentDictionary<string, AgentSession> _sessions = new();

    private readonly ChatClientAgent _stylistAgent;
    private readonly ChatClientAgent _weatherAgent;
    public AgentLoopService(
        IChatClient chatClient,
        IClosetService closetService,
        IWeatherService weatherService,
        ILogger<AgentLoopService> logger)
    {
        _closetService = closetService;
        _weatherService = weatherService;
        _logger = logger;

        _weatherAgent = new ChatClientAgent(
            chatClient,
            instructions: """
                You are a weather-focused clothing planner.
                Call the evaluateWeatherRisk tool to get today's conditions, then summarize the clothing constraints in 2-3 concise sentences.
                Cover: temperature range, rain/snow risk, sun exposure, and what clothing properties are needed (waterproof, warmth level, layers).
                """,
            name: "weather-agent",
            tools:
            [
                AIFunctionFactory.Create(EvaluateWeatherRiskTool, name: "evaluateWeatherRisk")
            ]);

        _stylistAgent = new ChatClientAgent(
            chatClient,
            instructions: """
                You are a wardrobe stylist with direct access to the user's closet.
                Your job is to build a complete outfit (top, bottom, shoes; optionally hat and jacket) that fits the user's prompt, style intent, and the weather summary provided to you.

                WORKFLOW:
                1. Use the weather summary passed in your prompt — do NOT call a weather tool.
                2. Call searchCloset with relevant filters (role, color, warmth, waterproof, formality) to discover candidates. Use page sizes of 10.
                3. Pick the best items for each slot. Call getClosetItemById as needed and judge both color harmony and pattern harmony qualitatively from the returned item details.
                4. Call validateOutfitCompleteness with your chosen topId, bottomId, shoesId.
                   - If the result is INCOMPLETE, identify which slot is missing and repeat searchCloset to fill it, then re-validate.
                   - Keep iterating until validateOutfitCompleteness confirms the outfit is complete.
                5. Once you have a complete outfit, return ONLY a compact JSON object with these exact keys:
                   topId, bottomId, shoesId, hatId, jacketId, usesHybridTopBottom, rationale.
                   Use null for omitted hatId or jacketId. Do not wrap the JSON in markdown. Do not add any explanation before or after it.
                """,
            name: "stylist-agent",
            tools:
            [
                AIFunctionFactory.Create(SearchClosetTool, name: "searchCloset"),
                AIFunctionFactory.Create(GetClosetItemByIdTool, name: "getClosetItemById"),
                AIFunctionFactory.Create(ValidateOutfitCompletenessTool, name: "validateOutfitCompleteness")
            ]);
    }

    public async Task<AgentLoopResponse> RunAsync(AgentLoopRequest request, CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(request.ConversationId)
            ? NextAvailableConversationId()
            : request.ConversationId.Trim();

        _logger.LogInformation("Starting AgentLoop - ConversationId: {ConversationId} | Prompt: {Prompt}", key, request.Prompt);

        if (!_sessions.TryGetValue(key, out var session))
        {
            session = await _stylistAgent.CreateSessionAsync();
            _sessions[key] = session;
            _logger.LogInformation("Created new session for ConversationId: {ConversationId}", key);
        }

        var toolTraces = new List<AgentToolCallTrace>();
        var handoffs = new List<AgentHandoffTrace>();

        _logger.LogInformation("Initializing trace collection with empty list");
        ActiveTrace.Value = toolTraces;

        // Weather agent gets a fresh ephemeral session each request to prevent its
        // conversation history from polluting the stylist's persistent context.
        var weatherSession = await _weatherAgent.CreateSessionAsync();

        ActiveToolAgent.Value = "weather-agent";
        var weatherSummary = await RunWithHandoffAsync(
            handoffs,
            from: "coordinator",
            to: "weather-agent",
            note: "Call evaluateWeatherRisk tool and summarize clothing constraints",
            () => _weatherAgent.RunAsync("Summarize today's weather constraints for outfit selection.", weatherSession, options: null, cancellationToken));
        ActiveToolAgent.Value = null;

        ActiveToolAgent.Value = "stylist-agent";
        var candidateResponse = await RunWithHandoffAsync(
            handoffs,
            from: "weather-agent",
            to: "stylist-agent",
            note: "Search closet, validate completeness, then return a final outfit candidate as JSON",
            () => _stylistAgent.RunAsync(
                $"Prompt: {request.Prompt}\nBold mode: {request.BoldMode}\nWeather constraints: {weatherSummary}\nMax page size: {Math.Clamp(request.PageSize, 4, 20)}",
                session,
                options: null,
                cancellationToken));
        ActiveToolAgent.Value = null;

        _logger.LogInformation("Raw stylist response: {CandidateResponse}", candidateResponse);
        var candidate = TryParseCandidate(candidateResponse.ToString());

        if (candidate is not null)
            _logger.LogInformation("Candidate parsed from raw stylist response text.");

        if (candidate is null)
        {
            _logger.LogError("Stylist did not return a valid candidate JSON object. Raw response: {CandidateResponse}", candidateResponse);
            throw new InvalidOperationException("Failed to parse candidate outfit from agent response.");
        }

        ActiveTrace.Value = null;
        _logger.LogInformation("Trace collection complete. Total tool calls captured: {ToolCallCount}", toolTraces.Count);

        var explanation = $"Agent workflow summary:\nWeather: {weatherSummary}\nRationale: {candidate.Rationale}";

        // Create a deterministic recommendation from the candidate for display purposes
        var closet = _closetService.List();
        var byId = closet.ToDictionary(item => item.Id);
        var top = !string.IsNullOrWhiteSpace(candidate.TopId) && byId.TryGetValue(candidate.TopId, out var t) ? t : null;
        var bottom = !string.IsNullOrWhiteSpace(candidate.BottomId) && byId.TryGetValue(candidate.BottomId, out var b) ? b : null;
        var shoes = !string.IsNullOrWhiteSpace(candidate.ShoesId) && byId.TryGetValue(candidate.ShoesId, out var s) ? s : null;
        var hat = !string.IsNullOrWhiteSpace(candidate.HatId) && byId.TryGetValue(candidate.HatId, out var h) ? h : null;
        var jacket = !string.IsNullOrWhiteSpace(candidate.JacketId) && byId.TryGetValue(candidate.JacketId, out var j) ? j : null;

        var selection = new OutfitSelectionDto(top, bottom, shoes, hat, jacket, candidate.UsesHybridTopBottom);
        var recommendation = new OutfitRecommendationDto(
            selection,
            Warnings: [],
            Reasons: [candidate.Rationale, "Generated through Agent Framework tool iteration."],
            explanation);

        return new AgentLoopResponse(
            ExtractConversationId(session) ?? key,
            recommendation,
            candidate,
            toolTraces,
            handoffs,
            Summary: "Completed simplified agent flow with weather handoff and direct stylist JSON output.");
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

    [Description("Search closet inventory with bounded paging and filters for role, color, pattern, warmth, and weather safety. Accepts plain scalar values or single-value arrays.")]
    public string SearchClosetTool(
        [Description("Optional role filter, valid values are Top, Bottom, Shoes, Hat, Jacket. Scalar or single-value array accepted.")] JsonElement? role = null,
        [Description("Optional color filter, for example navy or white. Scalar or single-value array accepted.")] JsonElement? color = null,
        [Description("Optional pattern filter, for example solid or floral. Scalar or single-value array accepted.")] JsonElement? pattern = null,
        [Description("Optional minimum warmth value from 1 to 5. Scalar or single-value array accepted.")] JsonElement? minWarmth = null,
        [Description("Optional maximum warmth value from 1 to 5. Scalar or single-value array accepted.")] JsonElement? maxWarmth = null,
        [Description("Optional waterproof requirement. Scalar or single-value array accepted.")] JsonElement? waterproof = null,
        [Description("Optional formality filter, valid values are Casual, SmartCasual, Formal. Scalar or single-value array accepted.")] JsonElement? formality = null,
        [Description("1-based page number. Scalar or single-value array accepted.")] JsonElement? pageNumber = null,
        [Description("Page size between 1 and 50. Scalar or single-value array accepted.")] JsonElement? pageSize = null)
    {
        var parsedRole = ParseOptionalEnum<OutfitRole>(role);
        var parsedColor = ParseOptionalString(color);
        var parsedPattern = ParseOptionalString(pattern);
        var parsedMinWarmth = ParseOptionalInt(minWarmth);
        var parsedMaxWarmth = ParseOptionalInt(maxWarmth);
        var parsedWaterproof = ParseOptionalBool(waterproof);
        var parsedFormality = ParseOptionalEnum<FormalityLevel>(formality);
        var parsedPageNumber = ParseOptionalInt(pageNumber) ?? 1;
        var parsedPageSize = ParseOptionalInt(pageSize) ?? 12;

        var request = new ClosetSearchRequest(
            parsedRole is null ? null : [parsedRole.Value],
            string.IsNullOrWhiteSpace(parsedColor) ? null : [parsedColor],
            string.IsNullOrWhiteSpace(parsedPattern) ? null : [parsedPattern],
            parsedWaterproof,
            parsedMinWarmth,
            parsedMaxWarmth,
            parsedFormality,
            parsedPageNumber,
            parsedPageSize);

        var result = _closetService.Search(request);
        AddToolTrace("searchCloset", JsonSerializer.Serialize(request, JsonOptions), result.Items.Count, $"Returned {result.Items.Count} items out of {result.TotalCount}.");
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [Description("Get a single closet item by ID.")]
    public string GetClosetItemByIdTool([Description("Closet item ID")] string itemId)
    {
        var item = _closetService.List().FirstOrDefault(x => x.Id == itemId);
        AddToolTrace("getClosetItemById", itemId, item is null ? 0 : 1, item is null ? "Not found" : $"Found {item.Name}");
        return item is null ? "null" : JsonSerializer.Serialize(item, JsonOptions);
    }

    [Description("Evaluate weather-driven outfit requirements from the current forecast.")]
    public string EvaluateWeatherRiskTool()
    {
        var forecast = _weatherService.Get();
        var minTemp = forecast.Segments.Min(s => s.TemperatureC);
        var hasRain = forecast.Segments.Any(s => s.Precipitation is PrecipitationKind.Rain or PrecipitationKind.Drizzle or PrecipitationKind.Snow);
        var hasSun = forecast.Segments.Any(s => s.IsSunny);

        var summary = $"minTemp={minTemp}; rain={hasRain}; sunny={hasSun}";
        AddToolTrace("evaluateWeatherRisk", "{}", 1, summary);
        return summary;
    }

    [Description("Validate candidate outfit completeness. Returns COMPLETE or lists the missing required slots so the agent knows what to search for next.")]
    public string ValidateOutfitCompletenessTool(
        [Description("Candidate top item id")] string? topId,
        [Description("Candidate bottom item id")] string? bottomId,
        [Description("Candidate shoes item id")] string? shoesId,
        [Description("Candidate jacket item id (required when rain or cold is expected)")] string? jacketId = null)
    {
        var forecast = _weatherService.Get();
        var hasRain = forecast.Segments.Any(s => s.Precipitation is PrecipitationKind.Rain or PrecipitationKind.Drizzle or PrecipitationKind.Snow);
        var minTemp = forecast.Segments.Min(s => s.TemperatureC);
        var needsJacket = hasRain || minTemp <= 10;

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(topId)) missing.Add("top");
        if (string.IsNullOrWhiteSpace(bottomId)) missing.Add("bottom");
        if (string.IsNullOrWhiteSpace(shoesId)) missing.Add("shoes");
        if (needsJacket && string.IsNullOrWhiteSpace(jacketId)) missing.Add($"jacket (required: rain={hasRain}, minTemp={minTemp}C)");

        var summary = missing.Count == 0
            ? "COMPLETE: all required slots are filled."
            : $"INCOMPLETE: missing slots — {string.Join(", ", missing)}. Search for these and re-validate.";

        AddToolTrace("validateOutfitCompleteness", $"top={topId};bottom={bottomId};shoes={shoesId};jacket={jacketId}", 1, summary);
        return summary;
    }

    private void AddToolTrace(string toolName, string arguments, int resultCount, string summary)
    {
        _logger.LogInformation("Tool called: {ToolName} | Agent: {Agent} | ResultCount: {ResultCount}",
            toolName, ActiveToolAgent.Value ?? "unknown-agent", resultCount);

        if (ActiveTrace.Value is null)
        {
            _logger.LogWarning("ActiveTrace is null when trying to add trace for tool {ToolName}", toolName);
            return;
        }

        ActiveTrace.Value.Add(new AgentToolCallTrace(
            Agent: ActiveToolAgent.Value ?? "unknown-agent",
            Tool: toolName,
            Arguments: arguments,
            ResultCount: resultCount,
            Summary: summary));

        _logger.LogInformation("Tool trace recorded. Total traces: {TraceCount}", ActiveTrace.Value.Count);
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
        }

        // Handle {"submitOutfit": [{...}]} wrapper that some models emit as plain text
        // instead of invoking the tool through the function-calling protocol.
        try
        {
            using var doc = JsonDocument.Parse(payload);
            if (doc.RootElement.TryGetProperty("submitOutfit", out var calls) &&
                calls.ValueKind == JsonValueKind.Array &&
                calls.GetArrayLength() > 0)
            {
                return JsonSerializer.Deserialize<OutfitCandidateProposal>(calls[0].GetRawText(), JsonOptions);
            }
        }
        catch
        {
        }

        return null;
    }

    private static string SummarizeForecast(DailyForecastDto forecast) =>
        string.Join(", ", forecast.Segments.Select(s => $"{s.Segment}: {s.TemperatureC}C, {s.Precipitation}, sunny={s.IsSunny}"));

    private static bool? ParseOptionalBool(JsonElement? value)
    {
        var scalar = GetFirstScalarValue(value);
        if (scalar is null)
            return null;

        if (scalar.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            return scalar.Value.GetBoolean();

        if (scalar.Value.ValueKind == JsonValueKind.String)
        {
            var text = NormalizeToolScalar(scalar.Value.GetString());
            if (bool.TryParse(text, out var result))
                return result;
        }

        return null;
    }

    private static int? ParseOptionalInt(JsonElement? value)
    {
        var scalar = GetFirstScalarValue(value);
        if (scalar is null)
            return null;

        if (scalar.Value.ValueKind == JsonValueKind.Number && scalar.Value.TryGetInt32(out var number))
            return number;

        if (scalar.Value.ValueKind == JsonValueKind.String)
        {
            var text = NormalizeToolScalar(scalar.Value.GetString());
            if (int.TryParse(text, out var parsed))
                return parsed;
        }

        return null;
    }

    private static TEnum? ParseOptionalEnum<TEnum>(JsonElement? value) where TEnum : struct, Enum
    {
        var scalar = GetFirstScalarValue(value);
        if (scalar is null)
            return null;

        if (scalar.Value.ValueKind == JsonValueKind.String)
        {
            var text = NormalizeToolScalar(scalar.Value.GetString());
            if (Enum.TryParse<TEnum>(text, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed))
                return parsed;
        }

        if (scalar.Value.ValueKind == JsonValueKind.Number && scalar.Value.TryGetInt32(out var numeric) && Enum.IsDefined(typeof(TEnum), numeric))
            return (TEnum)Enum.ToObject(typeof(TEnum), numeric);

        return null;
    }

    private static string? ParseOptionalString(JsonElement? value)
    {
        var scalar = GetFirstScalarValue(value);
        if (scalar is null)
            return null;

        return scalar.Value.ValueKind switch
        {
            JsonValueKind.String => NormalizeToolScalar(scalar.Value.GetString()),
            JsonValueKind.Number => scalar.Value.ToString(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            _ => null
        };
    }

    private static JsonElement? GetFirstScalarValue(JsonElement? value)
    {
        if (value is null)
            return null;

        return value.Value.ValueKind switch
        {
            JsonValueKind.Undefined => null,
            JsonValueKind.Null => null,
            JsonValueKind.Array => value.Value.EnumerateArray().Select(item => GetFirstScalarValue(item)).FirstOrDefault(element => element is not null),
            JsonValueKind.Object => null,
            _ => value
        };
    }

    private static string? NormalizeToolScalar(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        return string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase) ? null : trimmed;
    }

    private static string Normalize(string value) => value.Trim().ToLowerInvariant();

    private string NextAvailableConversationId()
    {
        for (var attempts = 0; attempts < 10000; attempts++)
        {
            var candidate = NextConversationId();
            if (!_sessions.ContainsKey(candidate))
                return candidate;
        }

        throw new InvalidOperationException("No available conversation IDs remaining for conv#### format.");
    }

    private static string NextConversationId()
    {
        var value = Interlocked.Increment(ref ConversationCounter) % 10000;
        return $"conv{value:0000}";
    }

    private static string? ExtractConversationId(AgentSession session)
    {
        return session is ChatClientAgentSession chatSession ? chatSession.ConversationId : null;
    }
}
