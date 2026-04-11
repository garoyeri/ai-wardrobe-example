using Shared.Contracts;

namespace Api.Services;

public interface IClosetService
{
    IReadOnlyList<ClosetItemDto> List();
    ClosetItemDto Add(UpsertClosetItemRequest request);
    ClosetItemDto? Update(Guid id, UpsertClosetItemRequest request);
    bool Delete(Guid id);
    void Reset();
}

public sealed class ClosetService : IClosetService
{
    private readonly List<ClosetItemDto> _items = Seed();

    public IReadOnlyList<ClosetItemDto> List() => _items.OrderBy(x => x.Name).ToArray();

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
