using System.Net.Http.Json;
using System.Text.Json;
using Shared.Contracts;

namespace Web.Services;

public sealed class WardrobeApiClient(HttpClient httpClient)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ClosetItemDto>> GetClosetItemsAsync(CancellationToken cancellationToken = default)
        => await httpClient.GetFromJsonAsync<IReadOnlyList<ClosetItemDto>>("/api/closet/items", cancellationToken)
            ?? [];

    public async Task<ClosetSearchResultDto?> SearchClosetAsync(ClosetSearchRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync("/api/closet/items/search", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClosetSearchResultDto>(cancellationToken);
    }

    public async Task<ClosetItemDto?> AddClosetItemAsync(UpsertClosetItemRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync("/api/closet/items", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClosetItemDto>(cancellationToken);
    }

    public async Task<ClosetItemDto?> UpdateClosetItemAsync(string id, UpsertClosetItemRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PutAsJsonAsync($"/api/closet/items/{id}", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ClosetItemDto>(cancellationToken);
    }

    public async Task DeleteClosetItemAsync(string id, CancellationToken cancellationToken = default)
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

    public async Task<AgentLoopResponse?> RecommendOutfitWithAgentLoopAsync(AgentLoopRequest request, CancellationToken cancellationToken = default)
    {
        var response = await httpClient.PostAsJsonAsync("/api/chat/agent-loop", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<AgentLoopResponse>(cancellationToken);
    }

    public async IAsyncEnumerable<AgentLoopStreamEvent> StreamAgentLoopAsync(
        AgentLoopRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, "/api/chat/agent-loop/stream")
        {
            Content = JsonContent.Create(request)
        };

        using var response = await httpClient.SendAsync(
            requestMessage,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var item = JsonSerializer.Deserialize<AgentLoopStreamEvent>(line, JsonOptions);
            if (item is not null)
            {
                yield return item;
            }
        }
    }
}
