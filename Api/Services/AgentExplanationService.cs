using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Shared.Contracts;

namespace Api.Services;

public interface IAgentExplanationService
{
    Task<string> BuildExplanationAsync(ChatRequest request, IReadOnlyList<ClosetItemDto> closet, DailyForecastDto forecast, CancellationToken cancellationToken = default);
}

public sealed class AgentExplanationService(IChatClient chatClient, ILogger<AgentExplanationService> logger) : IAgentExplanationService
{
    private readonly ChatClientAgent _stylistAgent = new(chatClient, instructions:
        "You are a practical wardrobe stylist. Keep recommendations concise and grounded in closet inventory.");

    private readonly ChatClientAgent _weatherAgent = new(chatClient, instructions:
        "You are a weather analyst. Explain weather risks and constraints for clothing choices.");

    private readonly ChatClientAgent _rulesAgent = new(chatClient, instructions:
        "You are a fashion rules reviewer. Check layering, pattern harmony, and rain/sun guidance.");

    public async Task<string> BuildExplanationAsync(ChatRequest request, IReadOnlyList<ClosetItemDto> closet, DailyForecastDto forecast, CancellationToken cancellationToken = default)
    {
        try
        {
            var closetSummary = string.Join(", ", closet.Select(c => $"{c.Name} roles[{string.Join('/', c.Roles)}] colors[{string.Join('/', c.Colors)}] patterns[{string.Join('/', c.Patterns)}] warmth:{c.Warmth} waterproof:{c.Waterproof}"));
            var weatherSummary = string.Join(", ", forecast.Segments.Select(s => $"{s.Segment}: {s.TemperatureC}C, {s.Precipitation}, sunny={s.IsSunny}"));

            var weatherNote = await _weatherAgent.RunAsync($"Forecast: {weatherSummary}. Give 2 concise constraints for outfit planning.", cancellationToken: cancellationToken);
            var styleNote = await _stylistAgent.RunAsync($"User prompt: {request.Prompt}. Bold mode: {request.BoldMode}. Closet: {closetSummary}. Suggest styling intent in 2 sentences.", cancellationToken: cancellationToken);
            var rulesNote = await _rulesAgent.RunAsync($"Given forecast ({weatherSummary}) and closet ({closetSummary}), list 3 checks: required layers, rain handling, pattern harmony.", cancellationToken: cancellationToken);

            return $"Weather agent: {weatherNote}\nStylist agent: {styleNote}\nRules agent: {rulesNote}";
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Agent explanation failed; falling back to deterministic explanation.");
            return "Agent explanation is unavailable right now, but deterministic outfit rules were still applied.";
        }
    }
}
