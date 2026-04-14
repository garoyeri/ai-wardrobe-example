using Api.Services;
using Microsoft.Extensions.AI;
using OllamaSharp;
using Shared.Contracts;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();

builder.Services.AddOpenApi();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var ollamaConnectionString = builder.Configuration.GetConnectionString("ollama")
    ?? throw new InvalidOperationException("Missing Aspire-provided connection string 'ConnectionStrings:ollama'.");
var ollamaEndpoint = TryGetEndpointFromConnectionString(ollamaConnectionString)
    ?? throw new InvalidOperationException("Invalid Aspire Ollama connection string. Expected format includes 'Endpoint=...'.");
var ollamaModel = builder.Configuration["Ollama:Model"] ?? "llama3.2:1b";

builder.Services.AddChatClient(new OllamaApiClient(new Uri(ollamaEndpoint), ollamaModel));
builder.Services.AddSingleton<IClosetService, ClosetService>();
builder.Services.AddSingleton<IWeatherService, WeatherService>();
builder.Services.AddSingleton<IOutfitRecommendationService, OutfitRecommendationService>();
builder.Services.AddSingleton<IAgentExplanationService, AgentExplanationService>();
builder.Services.AddSingleton<IAgentLoopService, AgentLoopService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseCors();

var closet = app.MapGroup("/api/closet");
closet.MapGet("/items", (IClosetService service) => Results.Ok(service.List()));
closet.MapPost("/items/search", (IClosetService service, ClosetSearchRequest request) =>
{
    var result = service.Search(request);
    return Results.Ok(result);
});
closet.MapPost("/items", (IClosetService service, UpsertClosetItemRequest request) =>
{
    if (request.Roles.Count == 0)
    {
        return Results.BadRequest("At least one outfit role is required.");
    }

    return Results.Ok(service.Add(request));
});
closet.MapPut("/items/{id:guid}", (IClosetService service, Guid id, UpsertClosetItemRequest request) =>
{
    var updated = service.Update(id, request);
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});
closet.MapDelete("/items/{id:guid}", (IClosetService service, Guid id) =>
    service.Delete(id) ? Results.NoContent() : Results.NotFound());
closet.MapPost("/reset", (IClosetService service) =>
{
    service.Reset();
    return Results.NoContent();
});

var weather = app.MapGroup("/api/weather");
weather.MapGet("/forecast", (IWeatherService service) => Results.Ok(service.Get()));
weather.MapPut("/forecast", (IWeatherService service, UpdateForecastRequest request) =>
{
    if (request.Segments.Count == 0)
    {
        return Results.BadRequest("At least one segment forecast is required.");
    }

    return Results.Ok(service.Update(request));
});
weather.MapPost("/reset", (IWeatherService service) =>
{
    service.Reset();
    return Results.NoContent();
});

app.MapPost("/api/chat/recommend", async (
    ChatRequest request,
    IClosetService closetService,
    IWeatherService weatherService,
    IAgentExplanationService explanationService,
    IOutfitRecommendationService recommendationService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt))
    {
        return Results.BadRequest("Prompt is required.");
    }

    var closetItems = closetService.List();
    var forecast = weatherService.Get();
    var explanation = await explanationService.BuildExplanationAsync(request, closetItems, forecast, cancellationToken);
    var recommendation = recommendationService.Recommend(request, closetItems, forecast, explanation);

    return Results.Ok(recommendation);
});

app.MapPost("/api/chat/agent-loop", async (
    AgentLoopRequest request,
    IAgentLoopService loopService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt))
    {
        return Results.BadRequest("Prompt is required.");
    }

    var response = await loopService.RunAsync(request, cancellationToken);
    return Results.Ok(response);
});

app.MapDefaultEndpoints();

app.Run();

static string? TryGetEndpointFromConnectionString(string connectionString)
{
    const string key = "Endpoint=";
    var start = connectionString.IndexOf(key, StringComparison.OrdinalIgnoreCase);
    if (start < 0)
    {
        return null;
    }

    start += key.Length;
    var end = connectionString.IndexOf(';', start);
    return (end >= start ? connectionString[start..end] : connectionString[start..]).Trim();
}
