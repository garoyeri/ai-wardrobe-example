using Shared.Contracts;

namespace Api.Services;

public interface IClosetService
{
    IReadOnlyList<ClosetItemDto> List();
    ClosetSearchResultDto Search(ClosetSearchRequest request);
    ClosetItemDto Add(UpsertClosetItemRequest request);
    ClosetItemDto? Update(Guid id, UpsertClosetItemRequest request);
    bool Delete(Guid id);
    void Reset();
}

public sealed class ClosetService : IClosetService
{
    private const int MaxPageSize = 50;
    private readonly List<ClosetItemDto> _items = Seed();

    public IReadOnlyList<ClosetItemDto> List() => _items.OrderBy(x => x.Name).ToArray();

    public ClosetSearchResultDto Search(ClosetSearchRequest request)
    {
        var pageNumber = Math.Max(1, request.PageNumber);
        var pageSize = Math.Clamp(request.PageSize, 1, MaxPageSize);

        IEnumerable<ClosetItemDto> query = _items;

        if (request.Roles is { Count: > 0 })
        {
            query = query.Where(item => request.Roles.All(role => item.Roles.Contains(role)));
        }

        if (request.Colors is { Count: > 0 })
        {
            var colors = request.Colors.Select(NormalizeTag).Where(static x => x.Length > 0).ToArray();
            query = query.Where(item => colors.Any(color => item.Colors.Select(NormalizeTag).Contains(color)));
        }

        if (request.Patterns is { Count: > 0 })
        {
            var patterns = request.Patterns.Select(NormalizeTag).Where(static x => x.Length > 0).ToArray();
            query = query.Where(item => patterns.Any(pattern => item.Patterns.Select(NormalizeTag).Contains(pattern)));
        }

        if (request.Waterproof.HasValue)
        {
            query = query.Where(item => item.Waterproof == request.Waterproof.Value);
        }

        if (request.MinWarmth.HasValue)
        {
            query = query.Where(item => item.Warmth >= request.MinWarmth.Value);
        }

        if (request.MaxWarmth.HasValue)
        {
            query = query.Where(item => item.Warmth <= request.MaxWarmth.Value);
        }

        if (request.Formality.HasValue)
        {
            query = query.Where(item => item.Formality == request.Formality.Value);
        }

        var total = query.Count();
        var items = query
            .OrderBy(item => item.Name)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToArray();

        var hasMore = (pageNumber * pageSize) < total;
        return new ClosetSearchResultDto(items, total, pageNumber, pageSize, hasMore);
    }

    public ClosetItemDto Add(UpsertClosetItemRequest request)
    {
        var item = ToItem(Guid.NewGuid(), request);
        _items.Add(item);
        return item;
    }

    public ClosetItemDto? Update(Guid id, UpsertClosetItemRequest request)
    {
        var index = _items.FindIndex(x => x.Id == id);
        if (index < 0)
        {
            return null;
        }

        var updated = ToItem(id, request);
        _items[index] = updated;
        return updated;
    }

    public bool Delete(Guid id) => _items.RemoveAll(x => x.Id == id) > 0;

    public void Reset()
    {
        _items.Clear();
        _items.AddRange(Seed());
    }

    private static ClosetItemDto ToItem(Guid id, UpsertClosetItemRequest request) =>
        new(
            id,
            request.Name.Trim(),
            request.Roles,
            request.Colors,
            request.Patterns,
            request.Material.Trim(),
            request.Weight,
            request.Waterproof,
            Math.Clamp(request.Warmth, 1, 5),
            request.Formality);

    private static string NormalizeTag(string value) => value.Trim().ToLowerInvariant();

    private static List<ClosetItemDto> Seed() =>
    [
        new(Guid.NewGuid(), "Navy Chino Pants", [OutfitRole.Bottom], ["navy"], ["solid"], "cotton", MaterialWeight.Medium, false, 3, FormalityLevel.SmartCasual),
        new(Guid.NewGuid(), "White Oxford Shirt", [OutfitRole.Top], ["white"], ["solid"], "cotton", MaterialWeight.Light, false, 2, FormalityLevel.SmartCasual),
        new(Guid.NewGuid(), "Charcoal Merino Sweater", [OutfitRole.Top], ["charcoal"], ["solid"], "wool", MaterialWeight.Medium, false, 4, FormalityLevel.SmartCasual),
        new(Guid.NewGuid(), "Tan Trench Coat", [OutfitRole.Jacket], ["tan"], ["solid"], "gabardine", MaterialWeight.Medium, true, 4, FormalityLevel.SmartCasual),
        new(Guid.NewGuid(), "Blue Denim Jacket", [OutfitRole.Jacket], ["blue"], ["solid"], "denim", MaterialWeight.Medium, false, 3, FormalityLevel.Casual),
        new(Guid.NewGuid(), "Black Chelsea Boots", [OutfitRole.Shoes], ["black"], ["solid"], "leather", MaterialWeight.Medium, true, 3, FormalityLevel.SmartCasual),
        new(Guid.NewGuid(), "White Sneakers", [OutfitRole.Shoes], ["white"], ["solid"], "leather", MaterialWeight.Light, false, 2, FormalityLevel.Casual),
        new(Guid.NewGuid(), "Cream Sun Hat", [OutfitRole.Hat], ["cream"], ["solid"], "straw", MaterialWeight.Light, false, 1, FormalityLevel.Casual),
        new(Guid.NewGuid(), "Floral Midi Dress", [OutfitRole.Top, OutfitRole.Bottom], ["pink", "green"], ["floral"], "linen", MaterialWeight.Light, false, 2, FormalityLevel.Casual)
    ];
}
