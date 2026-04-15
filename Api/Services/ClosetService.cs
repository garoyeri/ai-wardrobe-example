using Shared.Contracts;

namespace Api.Services;

public interface IClosetService
{
    IReadOnlyList<ClosetItemDto> List();
    ClosetSearchResultDto Search(ClosetSearchRequest request);
    ClosetItemDto Add(UpsertClosetItemRequest request);
    ClosetItemDto? Update(string id, UpsertClosetItemRequest request);
    bool Delete(string id);
    void Reset();
}

public sealed class ClosetService : IClosetService
{
    private const int MaxPageSize = 50;
    private static readonly IReadOnlyDictionary<OutfitRole, string> RolePrefixes = new Dictionary<OutfitRole, string>
    {
        [OutfitRole.Top] = "tops",
        [OutfitRole.Bottom] = "bttm",
        [OutfitRole.Shoes] = "shoe",
        [OutfitRole.Hat] = "hats",
        [OutfitRole.Jacket] = "jckt"
    };

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
        var item = ToItem(NextIdForRoles(request.Roles), request);
        _items.Add(item);
        return item;
    }

    public ClosetItemDto? Update(string id, UpsertClosetItemRequest request)
    {
        var normalizedId = NormalizeId(id);
        var index = _items.FindIndex(x => x.Id == normalizedId);
        if (index < 0)
        {
            return null;
        }

        var updated = ToItem(normalizedId, request);
        _items[index] = updated;
        return updated;
    }

    public bool Delete(string id)
    {
        var normalizedId = NormalizeId(id);
        return _items.RemoveAll(x => x.Id == normalizedId) > 0;
    }

    public void Reset()
    {
        _items.Clear();
        _items.AddRange(Seed());
    }

    private ClosetItemDto ToItem(string id, UpsertClosetItemRequest request) =>
        new(
            NormalizeId(id),
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

    private static string NormalizeId(string value) => value.Trim().ToLowerInvariant();

    private string NextIdForRoles(IReadOnlyList<OutfitRole> roles)
    {
        var prefix = GetPrefix(roles);
        var max = _items
            .Select(item => TryExtractSequence(item.Id, prefix))
            .Where(sequence => sequence.HasValue)
            .Select(sequence => sequence!.Value)
            .DefaultIfEmpty(0)
            .Max();

        if (max >= 9999)
            throw new InvalidOperationException($"Cannot generate more IDs for prefix '{prefix}'.");

        return $"{prefix}{max + 1:0000}";
    }

    private static string GetPrefix(IReadOnlyList<OutfitRole> roles)
    {
        var primary = roles.FirstOrDefault();
        return RolePrefixes.TryGetValue(primary, out var prefix) ? prefix : "item";
    }

    private static int? TryExtractSequence(string id, string prefix)
    {
        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || id.Length != 8)
            return null;

        if (int.TryParse(id[4..], out var sequence))
            return sequence;

        return null;
    }

    private static string CreateSeedId(string prefix, int number) => $"{prefix}{number:0000}";

    private static List<ClosetItemDto> Seed()
    {
        var items = new List<ClosetItemDto>();
        var rand = new Random(42); // Fixed seed for reproducibility

        // Helper method to get random colors and patterns
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
        string[] topColors = [
            "white",     // White T-Shirt
            "navy",      // Navy Polo
            "charcoal",  // Charcoal Henley
            "cream",     // Cream Button-Up
            "blue",      // Blue Chambray
            "gray",      // Gray Sweater
            "black",     // Black Turtleneck
            "olive",     // Olive Crew Neck
            "burgundy",  // Burgundy Cardigan
            "white",     // White Linen Shirt
            "navy",      // Striped Breton (navy/white breton stripes)
            "red",       // Red Sweater
            "tan",       // Tan Cable Knit
            "navy",      // Navy Peacoat Top
            "green",     // Green Hoodie
            "pink",      // Floral Blouse
            "white",     // White Oxford
            "charcoal",  // Charcoal Thermal
            "navy",      // Navy Cashmere
            "blue",      // Blue Denim Shirt
            "gray",      // Gray Hoodie
            "cream",     // Cream Sweater
            "black",     // Black Leather Jacket Liner
            "red",       // Plaid Flannel (classic red plaid)
            "white",     // Solid Tee
            "navy",      // Navy Sweater
            "white",     // White Tank
            "charcoal",  // Charcoal V-Neck
            "burgundy",  // Burgundy Pullover
            "beige",     // Beige Blazer
            "blue",      // Blue Striped Shirt
            "gray",      // Gray Cardigan
            "olive",     // Olive Shirt
            "cream",     // Cream Cable Sweater
            "white",     // White Crop Top
            "navy",      // Navy Vest
            "charcoal",  // Charcoal Hoodie
            "red",       // Red Polo
            "tan",       // Tan Shirt
            "black",     // Black Sweater
            "gray",      // Gray Turtleneck
            "blue",      // Blue Flannel
            "navy",      // Navy Thermal
            "charcoal",  // Charcoal Crew
            "white",     // White Camisole
            "burgundy",  // Burgundy Tank
            "olive",     // Olive Sweater
            "cream",     // Cream Cardigan
            "navy",      // Navy Henley
            "gray",      // Gray Sweater Vest
        ];
        for (int i = 0; i < 50; i++)
        {
            items.Add(new ClosetItemDto(
                CreateSeedId(RolePrefixes[OutfitRole.Top], i + 1),
                topNames[i],
                [OutfitRole.Top],
                [topColors[i]],
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
        string[] bottomColors = [
            "navy",      // Navy Chinos
            "black",     // Black Jeans
            "gray",      // Gray Trousers
            "tan",       // Tan Shorts
            "charcoal",  // Charcoal Slacks
            "blue",      // Blue Jeans
            "black",     // Black Leggings
            "olive",     // Cargo Pants (classic olive/military color)
            "white",     // White Linen Pants
            "navy",      // Navy Shorts
            "beige",     // Khaki Chinos (khaki = beige)
            "navy",      // Dark Jeans (dark wash = navy)
            "gray",      // Gray Joggers
            "olive",     // Olive Pants
            "black",     // Black Shorts
            "blue",      // Denim Shorts (denim = blue)
            "charcoal",  // Wool Trousers (classic charcoal wool)
            "beige",     // Cotton Pants (neutral beige)
            "navy",      // Navy Cargo
            "beige",     // Beige Chinos
            "black",     // Black Trousers
            "blue",      // Blue Shorts
            "gray",      // Gray Jeans
            "tan",       // Tan Pants
            "charcoal",  // Charcoal Shorts
        ];
        for (int i = 0; i < 25; i++)
        {
            items.Add(new ClosetItemDto(
                CreateSeedId(RolePrefixes[OutfitRole.Bottom], i + 1),
                bottomNames[i],
                [OutfitRole.Bottom],
                [bottomColors[i]],
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
        string[] jacketColors = [
            "tan",       // Tan Trench Coat
            "blue",      // Blue Denim Jacket
            "black",     // Black Leather Jacket
            "navy",      // Navy Blazer
            "gray",      // Gray Wool Coat
            "olive",     // Olive Bomber
            "charcoal",  // Charcoal Cardigan Jacket
            "beige",     // Beige Cardigan
            "blue",      // Blue Windbreaker
            "black",     // Black Puffer
            "navy",      // Navy Wool Coat
            "tan",       // Tan Cardigan
            "gray",      // Gray Blazer
            "blue",      // Blue Denim Overshirt
            "burgundy",  // Burgundy Sweater Jacket
        ];
        for (int i = 0; i < 15; i++)
        {
            items.Add(new ClosetItemDto(
                CreateSeedId(RolePrefixes[OutfitRole.Jacket], i + 1),
                jacketNames[i],
                [OutfitRole.Jacket],
                [jacketColors[i]],
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
        string[] hatColors = [
            "cream",  // Cream Sun Hat
            "black",  // Black Baseball Cap
            "navy",   // Navy Beanie
            "tan",    // Tan Fedora
            "gray",   // Gray Wool Hat
        ];
        for (int i = 0; i < 5; i++)
        {
            items.Add(new ClosetItemDto(
                CreateSeedId(RolePrefixes[OutfitRole.Hat], i + 1),
                hatNames[i],
                [OutfitRole.Hat],
                [hatColors[i]],
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
        string[] shoeColors = [
            "black",  // Black Chelsea Boots
            "white",  // White Sneakers
            "brown",  // Brown Loafers
            "navy",   // Navy Trainers
            "tan",    // Tan Desert Boots
        ];
        for (int i = 0; i < 5; i++)
        {
            items.Add(new ClosetItemDto(
                CreateSeedId(RolePrefixes[OutfitRole.Shoes], i + 1),
                shoeNames[i],
                [OutfitRole.Shoes],
                [shoeColors[i]],
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
