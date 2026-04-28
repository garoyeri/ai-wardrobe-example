namespace Shared.Contracts;

public sealed record AgentLoopRequest(
    string Prompt,
    string? ConversationId = null,
    int MaxToolCalls = 6,
    int PageSize = 12);

public sealed record OutfitCandidateProposal(
    string? TopId,
    string? BottomId,
    string? ShoesId,
    string? HatId,
    string? JacketId,
    string CompletenessNotes);

public sealed record AgentToolCallTrace(
    string Agent,
    string Tool,
    string Arguments,
    int ResultCount,
    string Summary);

public sealed record AgentLoopResponse(
    string ConversationId,
    string AgentResponse,
    IReadOnlyList<AgentToolCallTrace> ToolCalls,
    string Summary);

public static class AgentLoopEventType
{
    public const string Status = "status";
    public const string Lifecycle = "lifecycle";
    public const string AgentMessage = "agent-message";
    public const string AgentDelta = "agent-delta";
    public const string Validation = "validation";
    public const string Tool = "tool";
    public const string Summary = "summary";
    public const string Debug = "debug";
    public const string Complete = "complete";
    public const string Error = "error";
}

public sealed record AgentLoopStreamEvent(
    string ConversationId,
    int Sequence,
    string EventType,
    string Message,
    string? Agent = null,
    string? Executor = null,
    string? Tool = null,
    string? Stage = null,
    int? Attempt = null,
    object? Data = null,
    AgentToolCallTrace? ToolCall = null,
    AgentLoopResponse? Response = null);
