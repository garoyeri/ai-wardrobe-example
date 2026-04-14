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

    private static List<ClosetItemDto> Seed()
    {
        var items = new List<ClosetItemDto>();
        var rand = new Random(42); // Fixed seed for reproducibility

        // Helper method to get random colors and patterns
        string[] colors = ["black", "navy", "charcoal", "white", "cream", "gray", "tan", "beige", "olive", "burgundy", "navy", "blue", "green", "pink", "red"];
        string[] patterns = ["solid", "striped", "plaid", "floral", "solid"];
        string[] materials = ["cotton", "wool", "polyester", "linen", "denim", "leather"];
        MaterialWeight[] weights = [MaterialWeight.Light, MaterialWeight.Light, MaterialWeight.Medium, MaterialWeight.Medium, MaterialWeight.Heavy];
        FormalityLevel[] formalities = [FormalityLevel.Casual, FormalityLevel.SmartCasual, FormalityLevel.Formal];

        // 50 Tops (50%)
        string[] topNames = [
            "White T-Shirt", "Navy Polo", "Charcoal Henley", "Cream Button-Up", "Blue Chambray",
            "Gray Sweater", "Black Turtleneck", "Olive Crew Neck", "Burgundy Cardigan", "White Linen Shirt",
            "Striped Breton", "Red Sweater", "Tan Cable Knit", "Navy Peacoat Top", "Green Hoodie",
            "Floral Blouse", "White Oxford", "Charcoal Thermal", "Navy Cashmere", "Blue Denim Shirt",
            "Gray Hoodie", "Cream Sweater", "Black Leather Jacket Liner", "Plaid Flannel", "Solid Tee",
            "Navy Sweater", "White Tank", "Charcoal V-Neck", "Burgundy Pullover", "Beige Blazer",
            "Blue Striped Shirt", "Gray Cardigan", "Olive Shirt", "Cream Cable Sweater", "White Crop Top",
            "Navy Vest", "Charcoal Hoodie", "Red Polo", "Tan Shirt", "Black Sweater",
            "Gray Turtleneck", "Blue Flannel", "Navy Thermal", "Charcoal Crew", "White Camisole",
            "Burgundy Tank", "Olive Sweater", "Cream Cardigan", "Navy Henley", "Gray Sweater Vest"
        ];
        for (int i = 0; i < 50; i++)
        {
            items.Add(new ClosetItemDto(
                Guid.NewGuid(),
                topNames[i],
                [OutfitRole.Top],
                [colors[rand.Next(colors.Length)]],
                [patterns[rand.Next(patterns.Length)]],
                materials[rand.Next(materials.Length)],
                weights[rand.Next(weights.Length)],
                false,
                rand.Next(1, 6),
                formalities[rand.Next(formalities.Length)]
            ));
        }

        // 25 Bottoms (25%)
        string[] bottomNames = [
            "Navy Chinos", "Black Jeans", "Gray Trousers", "Tan Shorts", "Charcoal Slacks",
            "Blue Jeans", "Black Leggings", "Cargo Pants", "White Linen Pants", "Navy Shorts",
            "Khaki Chinos", "Dark Jeans", "Gray Joggers", "Olive Pants", "Black Shorts",
            "Denim Shorts", "Wool Trousers", "Cotton Pants", "Navy Cargo", "Beige Chinos",
            "Black Trousers", "Blue Shorts", "Gray Jeans", "Tan Pants", "Charcoal Shorts"
        ];
        for (int i = 0; i < 25; i++)
        {
            items.Add(new ClosetItemDto(
                Guid.NewGuid(),
                bottomNames[i],
                [OutfitRole.Bottom],
                [colors[rand.Next(colors.Length)]],
                [patterns[rand.Next(patterns.Length)]],
                materials[rand.Next(materials.Length)],
                weights[rand.Next(weights.Length)],
                false,
                rand.Next(1, 6),
                formalities[rand.Next(formalities.Length)]
            ));
        }

        // 15 Jackets (15%)
        string[] jacketNames = [
            "Tan Trench Coat", "Blue Denim Jacket", "Black Leather Jacket", "Navy Blazer", "Gray Wool Coat",
            "Olive Bomber", "Charcoal Cardigan Jacket", "Beige Cardigan", "Blue Windbreaker", "Black Puffer",
            "Navy Wool Coat", "Tan Cardigan", "Gray Blazer", "Blue Denim Overshirt", "Burgundy Sweater Jacket"
        ];
        for (int i = 0; i < 15; i++)
        {
            items.Add(new ClosetItemDto(
                Guid.NewGuid(),
                jacketNames[i],
                [OutfitRole.Jacket],
                [colors[rand.Next(colors.Length)]],
                [patterns[rand.Next(patterns.Length)]],
                materials[rand.Next(materials.Length)],
                weights[rand.Next(weights.Length)],
                rand.Next(2) == 0, // 50% waterproof
                rand.Next(2, 6),
                formalities[rand.Next(formalities.Length)]
            ));
        }

        // 5 Hats (5%)
        string[] hatNames = [
            "Cream Sun Hat", "Black Baseball Cap", "Navy Beanie", "Tan Fedora", "Gray Wool Hat"
        ];
        for (int i = 0; i < 5; i++)
        {
            items.Add(new ClosetItemDto(
                Guid.NewGuid(),
                hatNames[i],
                [OutfitRole.Hat],
                [colors[rand.Next(colors.Length)]],
                [patterns[rand.Next(patterns.Length)]],
                materials[rand.Next(materials.Length)],
                weights[rand.Next(weights.Length)],
                false,
                rand.Next(1, 4),
                formalities[rand.Next(formalities.Length)]
            ));
        }

        // 5 Shoes (5%)
        string[] shoeNames = [
            "Black Chelsea Boots", "White Sneakers", "Brown Loafers", "Navy Trainers", "Tan Desert Boots"
        ];
        for (int i = 0; i < 5; i++)
        {
            items.Add(new ClosetItemDto(
                Guid.NewGuid(),
                shoeNames[i],
                [OutfitRole.Shoes],
                [colors[rand.Next(colors.Length)]],
                [patterns[rand.Next(patterns.Length)]],
                materials[rand.Next(materials.Length)],
                weights[rand.Next(weights.Length)],
                rand.Next(3) == 0, // ~33% waterproof shoes
                rand.Next(1, 5),
                formalities[rand.Next(formalities.Length)]
            ));
        }

        return items;
    }
}
