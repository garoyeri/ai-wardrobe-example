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
    OutfitRole Role,
    IReadOnlyList<string> Colors,
    string Pattern,
    string Material,
    MaterialWeight Weight,
    bool Waterproof,
    int Warmth,
    FormalityLevel Formality)
{
    public string Description =>
        $"{Name} (id {Id}) is a {Formality} {Role} made of {Weight} {Material} " +
        $"in {(Colors.Count == 0 ? "no specified color" : string.Join("/", Colors))} " +
        $"with a {Pattern} pattern. Warmth rating {Warmth}/10, " +
        $"{(Waterproof ? "waterproof" : "not waterproof")}.";
}

public sealed record UpsertClosetItemRequest(
    string Name,
    OutfitRole Role,
    IReadOnlyList<string> Colors,
    string Pattern,
    string Material,
    MaterialWeight Weight,
    bool Waterproof,
    int Warmth,
    FormalityLevel Formality);

public sealed record ClosetSearchRequest(
    OutfitRole? Role = null,
    IReadOnlyList<string>? Colors = null,
    string? Pattern = null,
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
