using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.AI;
using Qdrant.Client;
using Qdrant.Client.Grpc;
using Shared.Contracts;

namespace Api.Services;

public interface IClosetService
{
    Task<IReadOnlyList<ClosetItemDto>> ListAsync(CancellationToken cancellationToken = default);
    Task<ClosetSearchResultDto> SearchAsync(ClosetSearchRequest request, CancellationToken cancellationToken = default);
    Task<ClosetItemDto?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task<ClosetItemDto> AddAsync(UpsertClosetItemRequest request, CancellationToken cancellationToken = default);
    Task<ClosetItemDto?> UpdateAsync(string id, UpsertClosetItemRequest request, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default);
    Task ResetAsync(CancellationToken cancellationToken = default);
}

public sealed class ClosetService : IClosetService
{
    private const string CollectionName = "closet";
    private const ulong EmbeddingDimensions = 512;
    private const int MaxPageSize = 50;
    private const int EmbeddingBatchSize = 32;
    private const float SemanticScoreThreshold = 0.70f;
    // mxbai-embed-large: documents are plain text, queries use this prefix
    // nomic-embed-text: use "search_document: " and "search_query: " respectively
    private const string EmbeddingDocumentPrefix = "";
    private const string EmbeddingQueryPrefix = "Represent this sentence for searching relevant passages: ";

    private static readonly IReadOnlyDictionary<OutfitRole, string> RolePrefixes = new Dictionary<OutfitRole, string>
    {
        [OutfitRole.Top] = "tops",
        [OutfitRole.Bottom] = "bttm",
        [OutfitRole.Shoes] = "shoe",
        [OutfitRole.Hat] = "hats",
        [OutfitRole.Jacket] = "jckt"
    };

    private readonly QdrantClient _qdrant;
    private readonly IEmbeddingGenerator<string, Embedding<float>> _embeddings;
    private readonly ILogger<ClosetService> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public ClosetService(
        QdrantClient qdrant,
        [FromKeyedServices("embeddings")] IEmbeddingGenerator<string, Embedding<float>> embeddings,
        ILogger<ClosetService> logger)
    {
        _qdrant = qdrant;
        _embeddings = embeddings;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ClosetItemDto>> ListAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var items = await ScrollAllAsync(filter: null, cancellationToken);
        return items.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<ClosetSearchResultDto> SearchAsync(ClosetSearchRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var pageNumber = Math.Max(1, request.PageNumber);
        var pageSize = Math.Clamp(request.PageSize, 1, MaxPageSize);

        var filter = BuildFilter(request);
        var matched = await SearchByDescriptionAsync(request.Description, filter, cancellationToken);

        var ordered = matched.OrderBy(x => x.Name, StringComparer.OrdinalIgnoreCase).ToArray();
        var total = ordered.Length;
        var page = ordered.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToArray();
        var hasMore = pageNumber * pageSize < total;
        return new ClosetSearchResultDto(page, total, pageNumber, pageSize, hasMore);
    }

    public async Task<ClosetItemDto?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var normalized = NormalizeId(id);
        var points = await _qdrant.RetrieveAsync(
            CollectionName,
            GuidFromId(normalized),
            withPayload: true,
            withVectors: false,
            cancellationToken: cancellationToken);
        return points.Count == 0 ? null : PayloadToItem(points[0].Payload);
    }

    public async Task<ClosetItemDto> AddAsync(UpsertClosetItemRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        var prefix = RolePrefixes.TryGetValue(request.Role, out var p) ? p : "item";
        var existing = await ScrollAllAsync(filter: null, cancellationToken);
        var max = existing
            .Select(x => TryExtractSequence(x.Id, prefix))
            .Where(seq => seq.HasValue)
            .Select(seq => seq!.Value)
            .DefaultIfEmpty(0)
            .Max();
        if (max >= 9999)
        {
            throw new InvalidOperationException($"Cannot generate more IDs for prefix '{prefix}'.");
        }

        var item = ToItem($"{prefix}{max + 1:0000}", request);
        await UpsertItemsAsync([item], cancellationToken);
        return item;
    }

    public async Task<ClosetItemDto?> UpdateAsync(string id, UpsertClosetItemRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var normalized = NormalizeId(id);
        var existing = await GetAsync(normalized, cancellationToken);
        if (existing is null)
        {
            return null;
        }

        var updated = ToItem(normalized, request);
        await UpsertItemsAsync([updated], cancellationToken);
        return updated;
    }

    public async Task<bool> DeleteAsync(string id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);
        var normalized = NormalizeId(id);
        var existing = await GetAsync(normalized, cancellationToken);
        if (existing is null)
        {
            return false;
        }

        await _qdrant.DeleteAsync(CollectionName, GuidFromId(normalized), cancellationToken: cancellationToken);
        return true;
    }

    public async Task ResetAsync(CancellationToken cancellationToken = default)
    {
        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (await _qdrant.CollectionExistsAsync(CollectionName, cancellationToken))
            {
                _logger.LogInformation("Dropping Qdrant collection {Collection}", CollectionName);
                await _qdrant.DeleteCollectionAsync(CollectionName, cancellationToken: cancellationToken);
            }

            await CreateCollectionAsync(cancellationToken);
            await SeedAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;

            var exists = await _qdrant.CollectionExistsAsync(CollectionName, cancellationToken);
            if (!exists)
            {
                _logger.LogInformation("Closet collection not present; creating and seeding {Collection}", CollectionName);
                await CreateCollectionAsync(cancellationToken);
                await SeedAsync(cancellationToken);
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private Task CreateCollectionAsync(CancellationToken cancellationToken) =>
        _qdrant.CreateCollectionAsync(
            CollectionName,
            new VectorParams { Size = EmbeddingDimensions, Distance = Distance.Cosine },
            cancellationToken: cancellationToken);

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        var seedItems = BuildSeedItems();
        _logger.LogInformation("Seeding closet with {Count} items and computing embeddings.", seedItems.Count);
        await UpsertItemsAsync(seedItems, cancellationToken);
    }

    private async Task UpsertItemsAsync(IReadOnlyList<ClosetItemDto> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return;

        for (var start = 0; start < items.Count; start += EmbeddingBatchSize)
        {
            var batch = items.Skip(start).Take(EmbeddingBatchSize).ToArray();
            var descriptions = batch.Select(it => $"{EmbeddingDocumentPrefix}{it.Description}").ToArray();
            var generated = await _embeddings.GenerateAsync(descriptions, cancellationToken: cancellationToken);

            var points = new List<PointStruct>(batch.Length);
            for (var i = 0; i < batch.Length; i++)
            {
                points.Add(BuildPoint(batch[i], generated[i].Vector.ToArray()));
            }

            await _qdrant.UpsertAsync(CollectionName, points, cancellationToken: cancellationToken);
        }
    }

    private async Task<ClosetItemDto[]> SearchByDescriptionAsync(string? description, Filter? filter, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(description))
        {
            return await ScrollAllAsync(filter, cancellationToken);
        }

        var embedding = await _embeddings.GenerateAsync($"{EmbeddingQueryPrefix}{description}", cancellationToken: cancellationToken);
        var allResults = await _qdrant.SearchAsync(
            CollectionName,
            embedding.Vector.ToArray(),
            filter: filter,
            limit: MaxPageSize,
            payloadSelector: true,
            vectorsSelector: false,
            cancellationToken: cancellationToken);

        return [.. allResults
            .Where(p => p.Score >= SemanticScoreThreshold)
            .Select(p => PayloadToItem(p.Payload))];
    }

    private async Task<ClosetItemDto[]> ScrollAllAsync(Filter? filter, CancellationToken cancellationToken)
    {
        var items = new List<ClosetItemDto>();
        PointId? offset = null;

        while (true)
        {
            var response = await _qdrant.ScrollAsync(
                CollectionName,
                filter: filter,
                limit: 256,
                offset: offset,
                payloadSelector: true,
                vectorsSelector: false,
                cancellationToken: cancellationToken);

            foreach (var point in response.Result)
            {
                items.Add(PayloadToItem(point.Payload));
            }

            var next = response.NextPageOffset;
            if (next is null || next.PointIdOptionsCase == PointId.PointIdOptionsOneofCase.None)
            {
                break;
            }

            offset = next;
        }

        return items.ToArray();
    }

    private static PointStruct BuildPoint(ClosetItemDto item, float[] vector)
    {
        var point = new PointStruct
        {
            Id = GuidFromId(item.Id),
            Vectors = vector,
        };

        point.Payload["id"] = item.Id;
        point.Payload["name"] = item.Name;
        point.Payload["role"] = (int)item.Role;
        point.Payload["colors"] = item.Colors.Select(NormalizeTag).Where(c => c.Length > 0).ToArray();
        point.Payload["pattern"] = NormalizeTag(item.Pattern);
        point.Payload["material"] = item.Material;
        point.Payload["weight"] = (int)item.Weight;
        point.Payload["waterproof"] = item.Waterproof;
        point.Payload["warmth"] = item.Warmth;
        point.Payload["formality"] = (int)item.Formality;
        return point;
    }

    private static ClosetItemDto PayloadToItem(IReadOnlyDictionary<string, Value> payload)
    {
        var colors = payload.TryGetValue("colors", out var colorsValue) && colorsValue.KindCase == Value.KindOneofCase.ListValue
            ? colorsValue.ListValue.Values.Select(v => v.StringValue).ToArray()
            : Array.Empty<string>();

        return new ClosetItemDto(
            Id: GetString(payload, "id"),
            Name: GetString(payload, "name"),
            Role: (OutfitRole)(int)GetLong(payload, "role"),
            Colors: colors,
            Pattern: GetString(payload, "pattern"),
            Material: GetString(payload, "material"),
            Weight: (MaterialWeight)(int)GetLong(payload, "weight"),
            Waterproof: GetBool(payload, "waterproof"),
            Warmth: (int)GetLong(payload, "warmth"),
            Formality: (FormalityLevel)(int)GetLong(payload, "formality"));
    }

    private static string GetString(IReadOnlyDictionary<string, Value> payload, string key) =>
        payload.TryGetValue(key, out var value) && value.KindCase == Value.KindOneofCase.StringValue ? value.StringValue : string.Empty;

    private static long GetLong(IReadOnlyDictionary<string, Value> payload, string key) =>
        payload.TryGetValue(key, out var value) && value.KindCase == Value.KindOneofCase.IntegerValue ? value.IntegerValue : 0L;

    private static bool GetBool(IReadOnlyDictionary<string, Value> payload, string key) =>
        payload.TryGetValue(key, out var value) && value.KindCase == Value.KindOneofCase.BoolValue && value.BoolValue;

    private static Filter? BuildFilter(ClosetSearchRequest request)
    {
        var conditions = new List<Condition>();

        if (request.Role.HasValue)
        {
            conditions.Add(Conditions.Match("role", (int)request.Role.Value));
        }

        if (request.Colors is { Count: > 0 })
        {
            var colors = request.Colors.Select(NormalizeTag).Where(c => c.Length > 0).ToArray();
            if (colors.Length > 0)
            {
                conditions.Add(Conditions.Match("colors", colors));
            }
        }

        if (!string.IsNullOrWhiteSpace(request.Pattern))
        {
            conditions.Add(Conditions.MatchKeyword("pattern", NormalizeTag(request.Pattern)));
        }

        if (request.Waterproof.HasValue)
        {
            conditions.Add(Conditions.Match("waterproof", request.Waterproof.Value));
        }

        if (request.MinWarmth.HasValue || request.MaxWarmth.HasValue)
        {
            var range = new Qdrant.Client.Grpc.Range();
            if (request.MinWarmth.HasValue)
            {
                range.Gte = request.MinWarmth.Value;
            }
            if (request.MaxWarmth.HasValue)
            {
                range.Lte = request.MaxWarmth.Value;
            }
            conditions.Add(Conditions.Range("warmth", range));
        }

        if (request.Formality.HasValue)
        {
            conditions.Add(Conditions.Match("formality", (int)request.Formality.Value));
        }

        if (conditions.Count == 0)
        {
            return null;
        }

        var filter = new Filter();
        filter.Must.AddRange(conditions);
        return filter;
    }

    private static ClosetItemDto ToItem(string id, UpsertClosetItemRequest request) =>
        new(
            NormalizeId(id),
            request.Name.Trim(),
            request.Role,
            request.Colors,
            request.Pattern,
            request.Material.Trim(),
            request.Weight,
            request.Waterproof,
            Math.Clamp(request.Warmth, 1, 5),
            request.Formality);

    private static string NormalizeTag(string value) => value.Trim().ToLowerInvariant();

    private static string NormalizeId(string value) => value.Trim().ToLowerInvariant();

    private static int? TryExtractSequence(string id, string prefix)
    {
        if (!id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || id.Length != 8)
            return null;

        return int.TryParse(id[4..], out var sequence) ? sequence : null;
    }

    private static Guid GuidFromId(string id)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes(id));
        return new Guid(bytes);
    }

    private static string CreateSeedId(string prefix, int number) => $"{prefix}{number:0000}";

    private static List<ClosetItemDto> BuildSeedItems()
    {
        var items = new List<ClosetItemDto>();
        var rand = new Random(42);

        string[] patterns = ["solid", "striped", "plaid", "floral", "solid"];
        string[] materials = ["cotton", "wool", "polyester", "linen", "denim", "leather"];
        MaterialWeight[] weights = [MaterialWeight.Light, MaterialWeight.Light, MaterialWeight.Medium, MaterialWeight.Medium, MaterialWeight.Heavy];
        FormalityLevel[] formalities = [FormalityLevel.Casual, FormalityLevel.SmartCasual, FormalityLevel.Formal];

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
            "white", "navy", "charcoal", "cream", "blue",
            "gray", "black", "olive", "burgundy", "white",
            "navy", "red", "tan", "navy", "green",
            "pink", "white", "charcoal", "navy", "blue",
            "gray", "cream", "black", "red", "white",
            "navy", "white", "charcoal", "burgundy", "beige",
            "blue", "gray", "olive", "cream", "white",
            "navy", "charcoal", "red", "tan", "black",
            "gray", "blue", "navy", "charcoal", "white",
            "burgundy", "olive", "cream", "navy", "gray"
        ];
        for (int i = 0; i < 50; i++)
        {
            items.Add(new ClosetItemDto(
                CreateSeedId(RolePrefixes[OutfitRole.Top], i + 1),
                topNames[i],
                OutfitRole.Top,
                [topColors[i]],
                patterns[rand.Next(patterns.Length)],
                materials[rand.Next(materials.Length)],
                weights[rand.Next(weights.Length)],
                false,
                rand.Next(1, 6),
                formalities[rand.Next(formalities.Length)]
            ));
        }

        string[] bottomNames = [
            "Navy Chinos", "Black Jeans", "Gray Trousers", "Tan Shorts", "Charcoal Slacks",
            "Blue Jeans", "Black Leggings", "Cargo Pants", "White Linen Pants", "Navy Shorts",
            "Khaki Chinos", "Dark Jeans", "Gray Joggers", "Olive Pants", "Black Shorts",
            "Denim Shorts", "Wool Trousers", "Cotton Pants", "Navy Cargo", "Beige Chinos",
            "Black Trousers", "Blue Shorts", "Gray Jeans", "Tan Pants", "Charcoal Shorts"
        ];
        string[] bottomColors = [
            "navy", "black", "gray", "tan", "charcoal",
            "blue", "black", "olive", "white", "navy",
            "beige", "navy", "gray", "olive", "black",
            "blue", "charcoal", "beige", "navy", "beige",
            "black", "blue", "gray", "tan", "charcoal"
        ];
        for (int i = 0; i < 25; i++)
        {
            items.Add(new ClosetItemDto(
                CreateSeedId(RolePrefixes[OutfitRole.Bottom], i + 1),
                bottomNames[i],
                OutfitRole.Bottom,
                [bottomColors[i]],
                patterns[rand.Next(patterns.Length)],
                materials[rand.Next(materials.Length)],
                weights[rand.Next(weights.Length)],
                false,
                rand.Next(1, 6),
                formalities[rand.Next(formalities.Length)]
            ));
        }

        string[] jacketNames = [
            "Tan Trench Coat", "Blue Denim Jacket", "Black Leather Jacket", "Navy Blazer", "Gray Wool Coat",
            "Olive Bomber", "Charcoal Cardigan Jacket", "Beige Cardigan", "Blue Windbreaker", "Black Puffer",
            "Navy Wool Coat", "Tan Cardigan", "Gray Blazer", "Blue Denim Overshirt", "Burgundy Sweater Jacket"
        ];
        string[] jacketColors = [
            "tan", "blue", "black", "navy", "gray",
            "olive", "charcoal", "beige", "blue", "black",
            "navy", "tan", "gray", "blue", "burgundy"
        ];
        for (int i = 0; i < 15; i++)
        {
            items.Add(new ClosetItemDto(
                CreateSeedId(RolePrefixes[OutfitRole.Jacket], i + 1),
                jacketNames[i],
                OutfitRole.Jacket,
                [jacketColors[i]],
                patterns[rand.Next(patterns.Length)],
                materials[rand.Next(materials.Length)],
                weights[rand.Next(weights.Length)],
                rand.Next(2) == 0,
                rand.Next(2, 6),
                formalities[rand.Next(formalities.Length)]
            ));
        }

        string[] hatNames = [
            "Cream Sun Hat", "Black Baseball Cap", "Navy Beanie", "Tan Fedora", "Gray Wool Hat"
        ];
        string[] hatColors = ["cream", "black", "navy", "tan", "gray"];
        for (int i = 0; i < 5; i++)
        {
            items.Add(new ClosetItemDto(
                CreateSeedId(RolePrefixes[OutfitRole.Hat], i + 1),
                hatNames[i],
                OutfitRole.Hat,
                [hatColors[i]],
                patterns[rand.Next(patterns.Length)],
                materials[rand.Next(materials.Length)],
                weights[rand.Next(weights.Length)],
                false,
                rand.Next(1, 4),
                formalities[rand.Next(formalities.Length)]
            ));
        }

        string[] shoeNames = [
            "Black Chelsea Boots", "White Sneakers", "Brown Loafers", "Navy Trainers", "Tan Desert Boots"
        ];
        string[] shoeColors = ["black", "white", "brown", "navy", "tan"];
        for (int i = 0; i < 5; i++)
        {
            items.Add(new ClosetItemDto(
                CreateSeedId(RolePrefixes[OutfitRole.Shoes], i + 1),
                shoeNames[i],
                OutfitRole.Shoes,
                [shoeColors[i]],
                patterns[rand.Next(patterns.Length)],
                materials[rand.Next(materials.Length)],
                weights[rand.Next(weights.Length)],
                rand.Next(3) == 0,
                rand.Next(1, 5),
                formalities[rand.Next(formalities.Length)]
            ));
        }

        return items;
    }
}
