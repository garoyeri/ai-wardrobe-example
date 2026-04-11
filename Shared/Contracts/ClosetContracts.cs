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
    Guid Id,
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
