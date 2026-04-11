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
