using System.ComponentModel;
using System.Threading.Channels;
using System.Text.Json;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared.Contracts;

namespace Api.Services;

public interface IAgentLoopService
{
    Task<AgentLoopResponse> RunAsync(AgentLoopRequest request, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AgentLoopStreamEvent> StreamAsync(AgentLoopRequest request, CancellationToken cancellationToken = default);
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
    private readonly ILogger<AgentLoopService> _logger;

    private readonly ChatClientAgent _weatherAgent;
    private readonly ChatClientAgent _stylistAgent;
    private static readonly AsyncLocal<StreamContext?> CurrentStreamContext = new();

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
                /no_think You are a weather-focused clothing planner.
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
                /no_think You are a wardrobe stylist with direct access to the user's closet.
                Build a complete outfit (top, bottom, shoes; optionally hat and jacket) that fits the user request and weather summary provided by the previous agent.

                WORKFLOW:
                1. Call searchCloset with role/formality/warmth/weather filters to find candidates.
                2. Call getClosetItemById as needed to compare colors/patterns qualitatively.
                3. Call validateOutfitCompleteness with topId, bottomId, shoesId, and jacketId when applicable.
                   - If INCOMPLETE, fill missing slots and re-check.
                4. Return a concise final recommendation in plain language.
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
            ? NextConversationId()
            : request.ConversationId.Trim();

        return await ExecuteAsync(request, key, streamContext: null, cancellationToken);
    }

    public async IAsyncEnumerable<AgentLoopStreamEvent> StreamAsync(
        AgentLoopRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(request.ConversationId)
            ? NextConversationId()
            : request.ConversationId.Trim();

        var channel = Channel.CreateUnbounded<AgentLoopStreamEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });

        var streamContext = new StreamContext(key, channel.Writer);

        _ = Task.Run(async () =>
        {
            try
            {
                await ExecuteAsync(request, key, streamContext, cancellationToken);
                channel.Writer.TryComplete();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                channel.Writer.TryComplete();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Agent loop streaming failed for conversation {ConversationId}", key);
                streamContext.TryWrite(new AgentLoopStreamEvent(
                    key,
                    streamContext.NextSequence(),
                    AgentLoopEventType.Error,
                    ex.Message));
                channel.Writer.TryComplete(ex);
            }
        }, cancellationToken);

        await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
        {
            yield return item;
        }
    }

    private async Task<AgentLoopResponse> ExecuteAsync(
        AgentLoopRequest request,
        string conversationId,
        StreamContext? streamContext,
        CancellationToken cancellationToken)
    {
        using var scope = BeginStreamScope(streamContext);

        streamContext?.TryWrite(new AgentLoopStreamEvent(
            conversationId,
            streamContext.NextSequence(),
            AgentLoopEventType.Status,
            "Starting wardrobe workflow."));

        var handoffs = new List<AgentHandoffTrace>();

        var weatherHandoff = new AgentHandoffTrace("user", _weatherAgent.Name ?? "weather-agent", "Collect weather constraints before styling.");
        handoffs.Add(weatherHandoff);
        streamContext?.TryWrite(new AgentLoopStreamEvent(
            conversationId,
            streamContext.NextSequence(),
            AgentLoopEventType.Handoff,
            weatherHandoff.Note,
            Agent: weatherHandoff.To,
            Handoff: weatherHandoff));

        var weatherPrompt = $"User prompt: {request.Prompt}\nAnalyze the current forecast and summarize the outfit constraints in 2-3 concise sentences.";
        var weatherResponse = await _weatherAgent.RunAsync(weatherPrompt, cancellationToken: cancellationToken);
        var weatherText = weatherResponse.ToString();
        streamContext?.TryWrite(new AgentLoopStreamEvent(
            conversationId,
            streamContext.NextSequence(),
            AgentLoopEventType.AgentMessage,
            weatherText,
            Agent: _weatherAgent.Name));

        var stylistHandoff = new AgentHandoffTrace(_weatherAgent.Name ?? "weather-agent", _stylistAgent.Name ?? "stylist-agent", "Weather summary ready. Build the outfit using closet tools.");
        handoffs.Add(stylistHandoff);
        streamContext?.TryWrite(new AgentLoopStreamEvent(
            conversationId,
            streamContext.NextSequence(),
            AgentLoopEventType.Handoff,
            stylistHandoff.Note,
            Agent: stylistHandoff.To,
            Handoff: stylistHandoff));

        var cappedPageSize = Math.Clamp(request.PageSize, 4, 20);
        var stylistPrompt = $"""
            User prompt: {request.Prompt}
            Weather summary: {weatherText}
            Max page size for searchCloset: {cappedPageSize}
            Build a complete outfit and explain the recommendation briefly.
            """;
        var stylistResponse = await _stylistAgent.RunAsync(stylistPrompt, cancellationToken: cancellationToken);
        var stylistText = stylistResponse.ToString();
        streamContext?.TryWrite(new AgentLoopStreamEvent(
            conversationId,
            streamContext.NextSequence(),
            AgentLoopEventType.AgentMessage,
            stylistText,
            Agent: _stylistAgent.Name));

        var toolCalls = streamContext?.SnapshotToolCalls() ?? [];
        var response = new AgentLoopResponse(
            conversationId,
            stylistText,
            ToolCalls: toolCalls,
            Handoffs: handoffs,
            Summary: $"Completed streaming workflow with {toolCalls.Count} tool calls across {handoffs.Count} handoffs.");

        streamContext?.TryWrite(new AgentLoopStreamEvent(
            conversationId,
            streamContext.NextSequence(),
            AgentLoopEventType.Complete,
            "Workflow completed.",
            Agent: _stylistAgent.Name,
            Response: response));

        return response;
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
        var payload = JsonSerializer.Serialize(result, JsonOptions);
        ReportToolCall(new AgentToolCallTrace(
            Agent: "stylist-agent",
            Tool: "searchCloset",
            Arguments: JsonSerializer.Serialize(request, JsonOptions),
            ResultCount: result.Items.Count,
            Summary: $"Found {result.Items.Count} closet item candidates."));

        return payload;
    }

    [Description("Get a single closet item by ID.")]
    public string GetClosetItemByIdTool([Description("Closet item ID")] string itemId)
    {
        var item = _closetService.List().FirstOrDefault(x => x.Id == itemId);
        ReportToolCall(new AgentToolCallTrace(
            Agent: "stylist-agent",
            Tool: "getClosetItemById",
            Arguments: JsonSerializer.Serialize(new { itemId }, JsonOptions),
            ResultCount: item is null ? 0 : 1,
            Summary: item is null ? $"No closet item found for {itemId}." : $"Loaded closet item {item.Name}."));

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
        ReportToolCall(new AgentToolCallTrace(
            Agent: "weather-agent",
            Tool: "evaluateWeatherRisk",
            Arguments: "{}",
            ResultCount: forecast.Segments.Count,
            Summary: $"Weather check: min temp {minTemp}C, rain={hasRain}, sunny={hasSun}."));

        return summary;
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
            ? RecordValidationResult("COMPLETE: all required slots are filled.", missing.Count)
            : RecordValidationResult($"INCOMPLETE: missing slots - {string.Join(", ", missing)}. Search for these and re-validate.", missing.Count);
    }

    private static string RecordValidationResult(string message, int missingCount)
    {
        ReportToolCall(new AgentToolCallTrace(
            Agent: "stylist-agent",
            Tool: "validateOutfitCompleteness",
            Arguments: "validation-request",
            ResultCount: missingCount,
            Summary: message));

        return message;
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

    private static IDisposable? BeginStreamScope(StreamContext? streamContext)
    {
        var previous = CurrentStreamContext.Value;
        CurrentStreamContext.Value = streamContext;
        return new StreamScope(previous);
    }

    private static void ReportToolCall(AgentToolCallTrace trace)
    {
        var streamContext = CurrentStreamContext.Value;
        streamContext?.RecordToolCall(trace);
    }

    private sealed class StreamScope(StreamContext? previous) : IDisposable
    {
        public void Dispose()
        {
            CurrentStreamContext.Value = previous;
        }
    }

    private sealed class StreamContext(string conversationId, ChannelWriter<AgentLoopStreamEvent> writer)
    {
        private readonly object _gate = new();
        private readonly List<AgentToolCallTrace> _toolCalls = [];
        private int _sequence;

        public int NextSequence() => Interlocked.Increment(ref _sequence);

        public void RecordToolCall(AgentToolCallTrace trace)
        {
            lock (_gate)
            {
                _toolCalls.Add(trace);
            }

            TryWrite(new AgentLoopStreamEvent(
                conversationId,
                NextSequence(),
                AgentLoopEventType.Tool,
                trace.Summary,
                Agent: trace.Agent,
                Tool: trace.Tool,
                ToolCall: trace));
        }

        public IReadOnlyList<AgentToolCallTrace> SnapshotToolCalls()
        {
            lock (_gate)
            {
                return _toolCalls.ToArray();
            }
        }

        public bool TryWrite(AgentLoopStreamEvent item) => writer.TryWrite(item);
    }
}
