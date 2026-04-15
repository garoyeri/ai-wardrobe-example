using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
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

    private readonly IClosetService _closetService;
    private readonly IWeatherService _weatherService;

    private readonly ChatClientAgent _weatherAgent;
    private readonly ChatClientAgent _stylistAgent;
    private readonly Workflow _workflow;

    public AgentLoopService(
        IChatClient chatClient,
        IClosetService closetService,
        IWeatherService weatherService)
    {
        _closetService = closetService;
        _weatherService = weatherService;

        _weatherAgent = new ChatClientAgent(
            chatClient,
            instructions: """
                You are a weather-focused clothing planner.
                Call the evaluateWeatherRisk tool and return 2-3 concise sentences about clothing constraints.
                Cover: minimum temperature, precipitation risk, sun exposure, and needed properties (waterproof, warmth, layering).
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
                Build a complete outfit (top, bottom, shoes; optionally hat and jacket) that fits the user request and weather summary provided by the previous agent.

                WORKFLOW:
                1. Call searchCloset with role/formality/warmth/weather filters to find candidates.
                2. Call getClosetItemById as needed to compare colors/patterns qualitatively.
                3. Call validateOutfitCompleteness with topId, bottomId, shoesId, and jacketId when applicable.
                   - If INCOMPLETE, fill missing slots and re-check.
                4. Return ONLY compact JSON with these exact keys:
                   topId, bottomId, shoesId, hatId, jacketId, usesHybridTopBottom, rationale.
                   Use null for omitted hatId or jacketId.
                   Do not use markdown fences and do not add any extra text.
                """,
            name: "stylist-agent",
            tools:
            [
                AIFunctionFactory.Create(SearchClosetTool, name: "searchCloset"),
                AIFunctionFactory.Create(GetClosetItemByIdTool, name: "getClosetItemById"),
                AIFunctionFactory.Create(ValidateOutfitCompletenessTool, name: "validateOutfitCompleteness")
            ]);

        _workflow = AgentWorkflowBuilder.BuildSequential("wardrobe-agent-workflow", [_weatherAgent, _stylistAgent]);
    }

    public async Task<AgentLoopResponse> RunAsync(AgentLoopRequest request, CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(request.ConversationId)
            ? NextConversationId()
            : request.ConversationId.Trim();

        var inputMessages = new List<ChatMessage>
        {
            new(
                ChatRole.User,
                $"User prompt: {request.Prompt}\nBold mode: {request.BoldMode}\nMax page size: {Math.Clamp(request.PageSize, 4, 20)}")
        };

        await using var run = await InProcessExecution.RunAsync(_workflow, inputMessages, key, cancellationToken);

        var rawCandidate = ExtractWorkflowOutput(run);
        var candidate = TryParseCandidate(rawCandidate);

        if (candidate is null)
            throw new InvalidOperationException("Failed to parse candidate outfit from workflow response.");

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
            Reasons: [candidate.Rationale],
            AgentExplanation: $"Workflow completed. Rationale: {candidate.Rationale}");

        return new AgentLoopResponse(
            key,
            recommendation,
            candidate,
            ToolCalls: [],
            Handoffs: [],
            Summary: "Completed sequential workflow using built-in workflow tracing.");
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
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [Description("Get a single closet item by ID.")]
    public string GetClosetItemByIdTool([Description("Closet item ID")] string itemId)
    {
        var item = _closetService.List().FirstOrDefault(x => x.Id == itemId);
        return item is null ? "null" : JsonSerializer.Serialize(item, JsonOptions);
    }

    [Description("Evaluate weather-driven outfit requirements from the current forecast.")]
    public string EvaluateWeatherRiskTool()
    {
        var forecast = _weatherService.Get();
        var minTemp = forecast.Segments.Min(s => s.TemperatureC);
        var hasRain = forecast.Segments.Any(s => s.Precipitation is PrecipitationKind.Rain or PrecipitationKind.Drizzle or PrecipitationKind.Snow);
        var hasSun = forecast.Segments.Any(s => s.IsSunny);

        return $"minTemp={minTemp}; rain={hasRain}; sunny={hasSun}";
    }

    [Description("Validate candidate outfit completeness. Returns COMPLETE or lists missing required slots.")]
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

        return missing.Count == 0
            ? "COMPLETE: all required slots are filled."
            : $"INCOMPLETE: missing slots - {string.Join(", ", missing)}. Search for these and re-validate.";
    }

    private static string? ExtractWorkflowOutput(Run run)
    {
        var outputEvent = run.NewEvents.OfType<WorkflowOutputEvent>().LastOrDefault();
        if (outputEvent is null)
            return null;

        var messages = outputEvent.As<List<ChatMessage>>();
        var lastText = messages?.LastOrDefault()?.Text;
        if (!string.IsNullOrWhiteSpace(lastText))
            return lastText;

        return outputEvent.Data?.ToString();
    }

    private OutfitCandidateProposal? TryParseCandidate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        var payload = raw.Trim();

        if (payload.StartsWith("```") && payload.Contains('\n'))
        {
            var firstNewLine = payload.IndexOf('\n');
            var lastFence = payload.LastIndexOf("```");
            if (firstNewLine > 0 && lastFence > firstNewLine)
                payload = payload[(firstNewLine + 1)..lastFence].Trim();
        }

        try
        {
            return JsonSerializer.Deserialize<OutfitCandidateProposal>(payload, JsonOptions);
        }
        catch
        {
        }

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.TryGetProperty("parameters", out var parameters))
                return JsonSerializer.Deserialize<OutfitCandidateProposal>(parameters.GetRawText(), JsonOptions);

            if (root.TryGetProperty("candidate", out var candidate))
                return JsonSerializer.Deserialize<OutfitCandidateProposal>(candidate.GetRawText(), JsonOptions);
        }
        catch
        {
        }

        return null;
    }

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

    private static string NextConversationId()
    {
        var value = Interlocked.Increment(ref ConversationCounter) % 10000;
        return $"conv{value:0000}";
    }
}
