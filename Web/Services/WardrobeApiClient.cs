using System.Net.Http.Json;
using Shared.Contracts;

namespace Web.Services;

public sealed class WardrobeApiClient(HttpClient httpClient)
{
    public async Task<IReadOnlyList<ClosetItemDto>> GetClosetItemsAsync(CancellationToken cancellationToken = default)
        => await httpClient.GetFromJsonAsync<IReadOnlyList<ClosetItemDto>>("/api/closet/items", cancellationToken)
            ?? [];

    public async Task<ClosetItemDto?> AddClosetItemAsync(UpsertClosetItemRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync("/api/closet/items", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClosetItemDto>(cancellationToken);
    }

    public async Task<ClosetItemDto?> UpdateClosetItemAsync(Guid id, UpsertClosetItemRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PutAsJsonAsync($"/api/closet/items/{id}", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClosetItemDto>(cancellationToken);
    }

    public async Task DeleteClosetItemAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.DeleteAsync($"/api/closet/items/{id}", cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task ResetClosetAsync(CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsync("/api/closet/reset", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<DailyForecastDto?> GetForecastAsync(CancellationToken cancellationToken = default)
        => await httpClient.GetFromJsonAsync<DailyForecastDto>("/api/weather/forecast", cancellationToken);

    public async Task<DailyForecastDto?> UpdateForecastAsync(UpdateForecastRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PutAsJsonAsync("/api/weather/forecast", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<DailyForecastDto>(cancellationToken);
    }

    public async Task ResetForecastAsync(CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsync("/api/weather/reset", null, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public async Task<OutfitRecommendationDto?> RecommendOutfitAsync(ChatRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync("/api/chat/recommend", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<OutfitRecommendationDto>(cancellationToken);
    }
}
