using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
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
    private const int ContextCharBudget = 5000;
    private static int ConversationCounter;

    private readonly IClosetService _closetService;
    private readonly IWeatherService _weatherService;
    private readonly ILogger<AgentLoopService> _logger;
    private readonly IConversationCancellationManager _cancellationManager;
    private readonly Dictionary<string, ConversationState> _conversationState = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _conversationGate = new();

    private readonly ChatClientAgent _weatherAgent;
    private readonly ChatClientAgent _stylistAgent;
    private readonly ChatClientAgent _summaryAgent;

    public AgentLoopService(
        IChatClient chatClient,
        IClosetService closetService,
        IWeatherService weatherService,
        IConversationCancellationManager cancellationManager,
        ILogger<AgentLoopService> logger)
    {
        _closetService = closetService;
        _weatherService = weatherService;
        _cancellationManager = cancellationManager;
        _logger = logger;

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
                **Purpose**
                You are a wardrobe stylist assistant helping users choose an outfit for the day to be safe and comfortable.
                
                **Guidelines**
                Consider the user prompt requirements and the weather constraints.
                Ask the user for more details if the prompt is vague, for example about the occasion, style preferences, or specific clothing items.
                Return ONLY a comma-separated list of closet item IDs and nothing else, when searching the closet, the response will have an ID field.
                Required closet item roles: top, bottom, shoes.
                Include jacket when rain is likely or minimum temperature is 10C or below.
                Hat is optional.
                Use the `searchCloset` tool to search the closet for candidate items based on the user prompt and the weather constraints.
                Use the `getClosetItemById` tool to get details about specific closet items by ID.

                Consider choosing a color palette for the outfit and using it as a filter when searching for each item.
                For example, if the user has a preference for navy and white, try to include those colors in the top, bottom, or shoes.

                Once you have a candidate outfit, return it in the prescribed format:                
                Example output: tops0001,bttm0003,shoe0004,jckt0002,hats0001
                """,
            name: "stylist-agent",
            tools:
            [
                AIFunctionFactory.Create(SearchClosetTool, name: "searchCloset"),
                AIFunctionFactory.Create(GetClosetItemByIdTool, name: "getClosetItemById")
            ]);

        _summaryAgent = new ChatClientAgent(
            chatClient,
            instructions: """
                Summarize long wardrobe planning chats.
                Keep weather constraints, user style preferences, hard requirements, and unresolved issues.
                Output concise bullet points.
                """,
            name: "context-summary-agent");
    }

    public async Task<AgentLoopResponse> RunAsync(AgentLoopRequest request, CancellationToken cancellationToken = default)
    {
        AgentLoopResponse? response = null;

        await foreach (var item in StreamAsync(request, cancellationToken))
        {
            if (item.EventType == AgentLoopEventType.Complete)
            {
                response = item.Response;
            }
        }

        return response ?? throw new InvalidOperationException("Workflow completed without a final response.");
    }

    public async IAsyncEnumerable<AgentLoopStreamEvent> StreamAsync(
        AgentLoopRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Prompt))
        {
            throw new ArgumentException("Prompt is required.", nameof(request));
        }

        var conversationId = string.IsNullOrWhiteSpace(request.ConversationId)
            ? NextConversationId()
            : request.ConversationId.Trim();

        // Get or create a CancellationTokenSource for this conversation
        var conversationCts = _cancellationManager.GetOrCreateSource(conversationId);

        // Create a linked token source that combines both the external cancellation token
        // and the conversation-specific cancellation token
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, conversationCts.Token);
        var combinedToken = linkedCts.Token;

        try
        {
            var sequence = 0;
            AgentLoopResponse? finalResponse = null;

            var state = GetOrCreateConversationState(conversationId);
            var (contextSummary, recentTranscript, summaryApplied) = await BuildContextWindowAsync(state, request.Prompt, combinedToken);

            if (summaryApplied)
            {
                yield return new AgentLoopStreamEvent(
                    conversationId,
                    ++sequence,
                    AgentLoopEventType.Summary,
                    "Conversation context was summarized to stay inside the context window.",
                    Agent: _summaryAgent.Name,
                    Stage: "context",
                    Data: new { state.RollingSummary });
            }

            var workflowInput = new OutfitWorkflowInput(
                ConversationId: conversationId,
                Prompt: request.Prompt,
                ContextSummary: contextSummary,
                RecentTranscript: recentTranscript,
                PageSize: Math.Clamp(request.PageSize, 4, 20),
                MaxAttempts: Math.Clamp(request.MaxToolCalls, 2, 8));

            var weatherExecutor = new WeatherExecutor(_weatherAgent);
            var initialStylistExecutor = new InitialStylistExecutor(_stylistAgent, _closetService);
            var retryStylistExecutor = new RetryStylistExecutor(_stylistAgent, _closetService);
            var validateExecutor = new ValidateOutfitExecutor(_closetService, _weatherService);
            var outputExecutor = new OutputExecutor(_closetService);

            var workflow = new WorkflowBuilder(weatherExecutor)
                .AddEdge(weatherExecutor, initialStylistExecutor)
                .AddEdge(initialStylistExecutor, validateExecutor)
                .AddEdge<ValidationResult>(validateExecutor, retryStylistExecutor, condition: result => result?.NeedsRetry ?? true)
                .AddEdge(retryStylistExecutor, validateExecutor)
                .AddEdge<ValidationResult>(validateExecutor, outputExecutor, condition: result => !(result?.NeedsRetry ?? true))
                .WithOutputFrom(outputExecutor)
                .Build();

            await using var run = await InProcessExecution.RunStreamingAsync(workflow, workflowInput);
            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            yield return new AgentLoopStreamEvent(
                conversationId,
                ++sequence,
                AgentLoopEventType.Status,
                "Workflow started in streaming mode.",
                Stage: "workflow");

            await foreach (var evt in run.WatchStreamAsync().WithCancellation(combinedToken))
            {
                switch (evt)
                {
                    case WorkflowStartedEvent:
                        yield return new AgentLoopStreamEvent(
                            conversationId,
                            ++sequence,
                            AgentLoopEventType.Lifecycle,
                            "Workflow execution started.",
                            Stage: "workflow");
                        break;
                    case ExecutorInvokedEvent invoked:
                        yield return new AgentLoopStreamEvent(
                            conversationId,
                            ++sequence,
                            AgentLoopEventType.Lifecycle,
                            $"Executor started: {invoked.ExecutorId}",
                            Executor: invoked.ExecutorId,
                            Stage: "executor");
                        break;
                    case ExecutorCompletedEvent completed:
                        yield return new AgentLoopStreamEvent(
                            conversationId,
                            ++sequence,
                            AgentLoopEventType.Lifecycle,
                            $"Executor completed: {completed.ExecutorId}",
                            Executor: completed.ExecutorId,
                            Stage: "executor");
                        break;
                    case AgentResponseUpdateEvent update:
                        var delta = update.Data?.ToString();
                        if (!string.IsNullOrWhiteSpace(delta))
                        {
                            yield return new AgentLoopStreamEvent(
                                conversationId,
                                ++sequence,
                                AgentLoopEventType.AgentDelta,
                                delta,
                                Agent: update.ExecutorId,
                                Executor: update.ExecutorId,
                                Stage: "agent");
                        }
                        break;
                    case AgentResponseEvent responseEvent:
                        if (responseEvent.Data is not null)
                        {
                            yield return new AgentLoopStreamEvent(
                                conversationId,
                                ++sequence,
                                AgentLoopEventType.AgentMessage,
                                responseEvent.Data.ToString() ?? string.Empty,
                                Agent: responseEvent.ExecutorId,
                                Executor: responseEvent.ExecutorId,
                                Stage: "agent");
                        }
                        break;
                    case WorkflowDebugEvent debug:
                        yield return new AgentLoopStreamEvent(
                            conversationId,
                            ++sequence,
                            debug.DebugType,
                            debug.Message,
                            Agent: debug.Agent,
                            Executor: debug.Executor,
                            Tool: debug.Tool,
                            Stage: debug.Stage,
                            Attempt: debug.Attempt,
                            Data: debug.Data);
                        break;
                    case WorkflowOutputEvent output:
                        if (output.Data is OutfitWorkflowOutput finalOutput)
                        {
                            var response = new AgentLoopResponse(
                                conversationId,
                                finalOutput.AgentResponse,
                                finalOutput.ToolCalls,
                                finalOutput.Summary);

                            finalResponse = response;

                            yield return new AgentLoopStreamEvent(
                                conversationId,
                                ++sequence,
                                AgentLoopEventType.Complete,
                                "Workflow completed.",
                                Agent: "stylist-agent",
                                Stage: "output",
                                Response: response,
                                Data: finalOutput);
                        }
                        break;
                    case WorkflowErrorEvent error:
                        yield return new AgentLoopStreamEvent(
                            conversationId,
                            ++sequence,
                            AgentLoopEventType.Error,
                            error.Exception?.Message ?? "Unknown workflow error",
                            Stage: "workflow",
                            Data: error.Exception?.ToString());
                        break;
                }
            }

            if (finalResponse is null)
            {
                throw new InvalidOperationException("Workflow completed without an output payload.");
            }

            state.AppendTurn(request.Prompt, finalResponse.AgentResponse);
        }
        finally
        {
            // Clean up the cancellation token source when the stream completes
            _cancellationManager.Cleanup(conversationId);
        }
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
            parsedRole,
            string.IsNullOrWhiteSpace(parsedColor) ? null : [parsedColor],
            parsedPattern,
            parsedWaterproof,
            parsedMinWarmth,
            parsedMaxWarmth,
            parsedFormality,
            parsedPageNumber,
            parsedPageSize);

        var result = _closetService.Search(request);
        return JsonSerializer.Serialize(result);
    }

    [Description("Get a single closet item by ID.")]
    public string GetClosetItemByIdTool([Description("Closet item ID")] string itemId)
    {
        var item = _closetService.List().FirstOrDefault(x => x.Id == itemId || string.Equals(x.Name, itemId, StringComparison.OrdinalIgnoreCase));
        return item is null ? "null" : JsonSerializer.Serialize(item);
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

    private async Task<(string Summary, string RecentTranscript, bool SummaryApplied)> BuildContextWindowAsync(
        ConversationState state,
        string nextPrompt,
        CancellationToken cancellationToken)
    {
        var transcript = state.FormatRecentTurns(maxTurns: 8);
        var combinedLength = state.RollingSummary.Length + transcript.Length + nextPrompt.Length;
        if (combinedLength <= ContextCharBudget)
        {
            return (state.RollingSummary, transcript, false);
        }

        var summaryPrompt = $"""
            Existing summary:
            {state.RollingSummary}

            Transcript:
            {transcript}

            Condense this into a compact state that preserves hard user requirements, preferences, weather constraints, and unresolved points.
            """;

        var summary = await _summaryAgent.RunAsync(summaryPrompt, cancellationToken: cancellationToken);
        state.RollingSummary = summary.ToString();
        state.TrimTurns(4);

        return (state.RollingSummary, state.FormatRecentTurns(maxTurns: 4), true);
    }

    private ConversationState GetOrCreateConversationState(string conversationId)
    {
        lock (_conversationGate)
        {
            if (!_conversationState.TryGetValue(conversationId, out var state))
            {
                state = new ConversationState();
                _conversationState[conversationId] = state;
            }

            return state;
        }
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

    private sealed class ConversationState
    {
        private readonly List<TurnSnapshot> _turns = [];

        public string RollingSummary { get; set; } = string.Empty;

        public void AppendTurn(string prompt, string response)
        {
            _turns.Add(new TurnSnapshot(prompt, response));
        }

        public string FormatRecentTurns(int maxTurns)
        {
            var builder = new StringBuilder();
            foreach (var turn in _turns.TakeLast(maxTurns))
            {
                builder.AppendLine($"User: {turn.Prompt}");
                builder.AppendLine($"Assistant: {turn.Response}");
            }

            return builder.ToString();
        }

        public void TrimTurns(int keepLast)
        {
            if (_turns.Count <= keepLast)
            {
                return;
            }

            var removeCount = _turns.Count - keepLast;
            _turns.RemoveRange(0, removeCount);
        }
    }

    private sealed record TurnSnapshot(string Prompt, string Response);
}

internal sealed record OutfitWorkflowInput(
    string ConversationId,
    string Prompt,
    string ContextSummary,
    string RecentTranscript,
    int PageSize,
    int MaxAttempts);

internal sealed record WeatherAdviceResult(
    OutfitWorkflowInput Input,
    string AdviceText,
    AgentToolCallTrace ToolTrace);

internal sealed record StylistDraft(
    OutfitWorkflowInput Input,
    string WeatherAdvice,
    int Attempt,
    string? PreviousFeedback,
    IReadOnlyList<string> CandidateIds,
    string RawResponse,
    IReadOnlyList<AgentToolCallTrace> ToolCalls);

internal sealed record ValidationResult(
    OutfitWorkflowInput Input,
    string WeatherAdvice,
    int Attempt,
    bool IsValid,
    bool NeedsRetry,
    string Feedback,
    OutfitCandidateProposal Proposal,
    string RawStylistResponse,
    IReadOnlyList<AgentToolCallTrace> ToolCalls);

internal sealed record OutfitWorkflowOutput(
    string AgentResponse,
    IReadOnlyList<AgentToolCallTrace> ToolCalls,
    string Summary);

internal sealed class WorkflowDebugEvent(
    string debugType,
    string message,
    string? agent = null,
    string? executor = null,
    string? tool = null,
    string? stage = null,
    int? attempt = null,
    object? data = null) : WorkflowEvent(data)
{
    public string DebugType { get; } = debugType;
    public string Message { get; } = message;
    public string? Agent { get; } = agent;
    public string? Executor { get; } = executor;
    public string? Tool { get; } = tool;
    public string? Stage { get; } = stage;
    public int? Attempt { get; } = attempt;
}

internal sealed class WeatherExecutor(ChatClientAgent weatherAgent) : Executor<OutfitWorkflowInput, WeatherAdviceResult>("WeatherExecutor")
{
    public override async ValueTask<WeatherAdviceResult> HandleAsync(
        OutfitWorkflowInput input,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var prompt = $"""
            User prompt: {input.Prompt}
            Recent conversation summary:
            {input.ContextSummary}

            Recent transcript:
            {input.RecentTranscript}

            Analyze the latest weather and summarize outfit constraints for this turn.
            """;

        var response = await weatherAgent.RunAsync(prompt, cancellationToken: cancellationToken);
        var advice = response.ToString();

        var trace = new AgentToolCallTrace(
            Agent: "weather-agent",
            Tool: "evaluateWeatherRisk",
            Arguments: "{}",
            ResultCount: 1,
            Summary: "Weather constraints were refreshed for the current user turn.");

        await context.AddEventAsync(new WorkflowDebugEvent(
            AgentLoopEventType.AgentMessage,
            advice,
            agent: "weather-agent",
            executor: "WeatherExecutor",
            stage: "weather",
            data: trace));

        return new WeatherAdviceResult(input, advice, trace);
    }
}

internal static class StylistExecutorSupport
{
    private static readonly Regex IdPattern = new(@"\b[a-z]{4}\d{4}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static async Task<StylistDraft> RunStylistAttemptAsync(
        ChatClientAgent stylistAgent,
        IClosetService closetService,
        OutfitWorkflowInput input,
        string weatherAdvice,
        int attempt,
        string? previousFeedback,
        IReadOnlyList<AgentToolCallTrace> toolCalls,
        string executorName,
        IWorkflowContext context,
        CancellationToken cancellationToken)
    {
        var retryInstruction = string.IsNullOrWhiteSpace(previousFeedback)
            ? ""
            : $"Validation feedback from previous attempt: {previousFeedback}";

        var prompt = $"""
            User prompt: {input.Prompt}
            Weather advice: {weatherAdvice}
            Attempt: {attempt} of {input.MaxAttempts}
            Max closet page size hint: {input.PageSize}
            {retryInstruction}

            Completion rules:
            - Return ONLY a comma-separated list of closet item IDs.
            - Required IDs: one top, one bottom, one shoes.
            - Include a jacket ID when rain is likely or minimum temperature is 10C or below.
            - Hat ID is optional.
            - Do not return JSON, markdown, prose, or explanations.
            - If validation says some IDs are already valid, keep them and only replace missing/invalid slots.

            Return only IDs like: tops0001,bttm0003,shoe0004,jckt0002,hats0001
            """;

        var response = await stylistAgent.RunAsync(prompt, cancellationToken: cancellationToken);
        var raw = response.ToString();
        var candidateIds = ParseCandidateIds(raw);

        await context.AddEventAsync(new WorkflowDebugEvent(
            AgentLoopEventType.AgentMessage,
            raw,
            agent: "stylist-agent",
            executor: executorName,
            stage: "stylist",
            attempt: attempt,
            data: new { candidateIds }));

        return new StylistDraft(input, weatherAdvice, attempt, previousFeedback, candidateIds, raw, toolCalls);
    }

    private static IReadOnlyList<string> ParseCandidateIds(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        var matches = IdPattern.Matches(raw)
            .Select(match => match.Value.Trim().ToLowerInvariant())
            .Where(id => id.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (matches.Length > 0)
        {
            return matches;
        }

        return raw
            .Split([',', '\n', '\r', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(token => token.Trim('"', '\'', '[', ']', '{', '}', '(', ')').ToLowerInvariant())
            .Where(token => token.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

internal sealed class InitialStylistExecutor(ChatClientAgent stylistAgent, IClosetService closetService) : Executor<WeatherAdviceResult, StylistDraft>("InitialStylistExecutor")
{
    public override async ValueTask<StylistDraft> HandleAsync(
        WeatherAdviceResult input,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        return await StylistExecutorSupport.RunStylistAttemptAsync(
            stylistAgent,
            closetService,
            input.Input,
            input.AdviceText,
            attempt: 1,
            previousFeedback: null,
            [input.ToolTrace],
            executorName: "InitialStylistExecutor",
            context,
            cancellationToken);
    }
}

internal sealed class RetryStylistExecutor(ChatClientAgent stylistAgent, IClosetService closetService) : Executor<ValidationResult, StylistDraft>("RetryStylistExecutor")
{
    public override async ValueTask<StylistDraft> HandleAsync(
        ValidationResult validation,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        return await StylistExecutorSupport.RunStylistAttemptAsync(
            stylistAgent,
            closetService,
            validation.Input,
            validation.WeatherAdvice,
            validation.Attempt + 1,
            validation.Feedback,
            validation.ToolCalls,
            executorName: "RetryStylistExecutor",
            context,
            cancellationToken);
    }
}

internal sealed class ValidateOutfitExecutor(IClosetService closetService, IWeatherService weatherService) : Executor<StylistDraft, ValidationResult>("ValidateOutfitExecutor")
{
    public override async ValueTask<ValidationResult> HandleAsync(
        StylistDraft draft,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var items = closetService.List();
        var byId = items.ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

        var forecast = weatherService.Get();
        var hasRain = forecast.Segments.Any(s => s.Precipitation is PrecipitationKind.Rain or PrecipitationKind.Drizzle or PrecipitationKind.Snow);
        var minTemp = forecast.Segments.Min(s => s.TemperatureC);
        var jacketRequired = hasRain || minTemp <= 10;
        var providedIds = draft.CandidateIds
            .Where(id => byId.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var top = ResolveByRole(byId, providedIds, OutfitRole.Top);
        var bottom = ResolveByRole(byId, providedIds, OutfitRole.Bottom);
        var shoes = ResolveByRole(byId, providedIds, OutfitRole.Shoes);
        var jacket = ResolveByRole(byId, providedIds, OutfitRole.Jacket);
        var hat = ResolveByRole(byId, providedIds, OutfitRole.Hat);

        var missing = new List<string>();

        if (top is null) missing.Add("top");
        if (bottom is null) missing.Add("bottom");
        if (shoes is null) missing.Add("shoes");
        if (jacketRequired && jacket is null) missing.Add($"jacket (required: rain={hasRain}, minTemp={minTemp}C)");

        var completenessNotes = missing.Count == 0
            ? "All required outfit slots were resolved from stylist-provided IDs."
            : $"Missing required slots: {string.Join(", ", missing)}.";

        var normalizedProposal = new OutfitCandidateProposal(
            TopId: top?.Id,
            BottomId: bottom?.Id,
            ShoesId: shoes?.Id,
            HatId: hat?.Id,
            JacketId: jacket?.Id,
            CompletenessNotes: completenessNotes);

        var valid = missing.Count == 0;
        var needsRetry = !valid && draft.Attempt < draft.Input.MaxAttempts;
        var feedback = valid
            ? $"COMPLETE: candidate resolved from IDs [{string.Join(",", providedIds)}]. top={DescribeSelection(byId, normalizedProposal.TopId)}, bottom={DescribeSelection(byId, normalizedProposal.BottomId)}, shoes={DescribeSelection(byId, normalizedProposal.ShoesId)}, jacket={(jacketRequired ? DescribeSelection(byId, normalizedProposal.JacketId) : "optional")}, hat={DescribeSelection(byId, normalizedProposal.HatId)}"
            : $"INCOMPLETE: missing or invalid slots - {string.Join(", ", missing)}. Provided IDs=[{string.Join(",", providedIds)}]. Current candidate: top={DescribeSelection(byId, normalizedProposal.TopId)}, bottom={DescribeSelection(byId, normalizedProposal.BottomId)}, shoes={DescribeSelection(byId, normalizedProposal.ShoesId)}, jacket={DescribeSelection(byId, normalizedProposal.JacketId)}, hat={DescribeSelection(byId, normalizedProposal.HatId)}";

        await context.AddEventAsync(new WorkflowDebugEvent(
            AgentLoopEventType.Validation,
            feedback,
            agent: "stylist-agent",
            executor: "ValidateOutfitExecutor",
            stage: "validation",
            attempt: draft.Attempt,
            data: new { valid, needsRetry, missing, normalizedProposal }));

        return new ValidationResult(
            draft.Input,
            draft.WeatherAdvice,
            draft.Attempt,
            valid,
            needsRetry,
            feedback,
            normalizedProposal,
            draft.RawResponse,
            draft.ToolCalls);
    }

    private static ClosetItemDto? ResolveByRole(
        Dictionary<string, ClosetItemDto> byId,
        IReadOnlyList<string> ids,
        OutfitRole role)
    {
        foreach (var id in ids)
        {
            if (byId.TryGetValue(id, out var item) && item.Role == role)
            {
                return item;
            }
        }

        return null;
    }

    private static string DescribeSelection(Dictionary<string, ClosetItemDto> byId, string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return "null";
        }

        return byId.TryGetValue(id, out var item)
            ? $"{item.Name} ({item.Id})"
            : id;
    }
}

internal sealed class OutputExecutor(IClosetService closetService) : Executor<ValidationResult, OutfitWorkflowOutput>("OutputExecutor")
{
    public override async ValueTask<OutfitWorkflowOutput> HandleAsync(
        ValidationResult result,
        IWorkflowContext context,
        CancellationToken cancellationToken = default)
    {
        var finalMessage = result.IsValid
            ? BuildSuccessMessage(result)
            : BuildFailureMessage(result);

        var output = new OutfitWorkflowOutput(
            AgentResponse: finalMessage,
            ToolCalls: result.ToolCalls,
            Summary: $"Workflow completed after {result.Attempt} stylist attempt(s). Validation: {result.Feedback}");

        await context.AddEventAsync(new WorkflowDebugEvent(
            AgentLoopEventType.Debug,
            "Yielding final workflow output.",
            agent: "stylist-agent",
            executor: "OutputExecutor",
            stage: "output",
            attempt: result.Attempt,
            data: output));

        return output;
    }

    private string BuildSuccessMessage(ValidationResult result)
    {
        var proposal = result.Proposal;
        var byId = closetService.List().ToDictionary(item => item.Id, StringComparer.OrdinalIgnoreCase);

        return $"""
            Final recommendation: outfit selected.

            Selected outfit based on the latest weather guidance:
            - Top: {FormatSelection(byId, proposal.TopId, "(not selected)")}
            - Bottom: {FormatSelection(byId, proposal.BottomId, "(not selected)")}
            - Shoes: {FormatSelection(byId, proposal.ShoesId, "(not selected)")}
            - Jacket: {FormatSelection(byId, proposal.JacketId, "(not required)")}
            - Hat: {FormatSelection(byId, proposal.HatId, "(optional, not selected)")}

            Rationale:
            - Weather guidance: {result.WeatherAdvice}
            - Validation result: {result.Feedback}
            - Completion notes: {proposal.CompletenessNotes}
            """;
    }

    private static string FormatSelection(
        Dictionary<string, ClosetItemDto> byId,
        string? id,
        string fallback)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return fallback;
        }

        return byId.TryGetValue(id, out var item)
            ? $"{item.Name} ({item.Id})"
            : id;
    }

    private static string BuildFailureMessage(ValidationResult result)
    {
        return $"""
            Final recommendation: no valid outfit selected.

            I could not produce a fully valid outfit in {result.Input.MaxAttempts} attempt(s).

            Rationale:
            - Weather guidance: {result.WeatherAdvice}
            - Last validation result: {result.Feedback}

            Last stylist response:
            {result.RawStylistResponse}
            """;
    }

}
