namespace Shared.Contracts;

public enum OutfitRole
{
    Top = 1,
    Bottom = 2,
    Shoes = 3,
    Hat = 4,
    Jacket = 5
}

public enum MaterialWeight
{
    Light,
    Medium,
    Heavy
}

public enum FormalityLevel
{
    Casual,
    SmartCasual,
    Formal
}

public sealed record ClosetItemDto(
    string Id,
    string Name,
    IReadOnlyList<OutfitRole> Roles,
    IReadOnlyList<string> Colors,
    IReadOnlyList<string> Patterns,
    string Material,
    MaterialWeight Weight,
    bool Waterproof,
    int Warmth,
    FormalityLevel Formality);

public sealed record UpsertClosetItemRequest(
    string Name,
    IReadOnlyList<OutfitRole> Roles,
    IReadOnlyList<string> Colors,
    IReadOnlyList<string> Patterns,
    string Material,
    MaterialWeight Weight,
    bool Waterproof,
    int Warmth,
    FormalityLevel Formality);

public sealed record ClosetSearchRequest(
    IReadOnlyList<OutfitRole>? Roles = null,
    IReadOnlyList<string>? Colors = null,
    IReadOnlyList<string>? Patterns = null,
    bool? Waterproof = null,
    int? MinWarmth = null,
    int? MaxWarmth = null,
    FormalityLevel? Formality = null,
    int PageNumber = 1,
    int PageSize = 20);

public sealed record ClosetSearchResultDto(
    IReadOnlyList<ClosetItemDto> Items,
    int TotalCount,
    int PageNumber,
    int PageSize,
    bool HasMore);
