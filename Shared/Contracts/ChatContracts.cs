namespace Shared.Contracts;

public sealed record ChatRequest(string Prompt, bool BoldMode = false);

public sealed record OutfitSelectionDto(
    ClosetItemDto? Top,
    ClosetItemDto? Bottom,
    ClosetItemDto? Shoes,
    ClosetItemDto? Hat,
    ClosetItemDto? Jacket,
    bool UsesHybridTopBottom);

public sealed record OutfitRecommendationDto(
    OutfitSelectionDto Selection,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Reasons,
    string AgentExplanation);

public sealed record AgentLoopRequest(
    string Prompt,
    bool BoldMode = false,
    string? ConversationId = null,
    int MaxToolCalls = 6,
    int PageSize = 12);

public sealed record OutfitCandidateProposal(
    Guid? TopId,
    Guid? BottomId,
    Guid? ShoesId,
    Guid? HatId,
    Guid? JacketId,
    bool UsesHybridTopBottom,
    string Rationale);

public sealed record AgentToolCallTrace(
    string Agent,
    string Tool,
    string Arguments,
    int ResultCount,
    string Summary);

public sealed record AgentHandoffTrace(
    string From,
    string To,
    string Note);

public sealed record AgentLoopResponse(
    string ConversationId,
    OutfitRecommendationDto Recommendation,
    OutfitCandidateProposal Candidate,
    IReadOnlyList<AgentToolCallTrace> ToolCalls,
    IReadOnlyList<AgentHandoffTrace> Handoffs,
    string Summary);
