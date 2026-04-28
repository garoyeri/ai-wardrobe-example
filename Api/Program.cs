using Api.Services;
using Microsoft.Agents.AI;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.AI;
using OllamaSharp;
using Shared.Contracts;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
var streamJsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);

const string OllamaSource = "OllamaApiClient";

builder.AddServiceDefaults(configureTrace: tracing =>
{
    tracing.AddSource(OllamaSource);
});

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

var ollamaHttpClient = new HttpClient(new RetryHttpMessageHandler(maxRetries: 3, initialDelay: TimeSpan.FromMilliseconds(500)))
{
    BaseAddress = new Uri(ollamaEndpoint),
    Timeout = TimeSpan.FromSeconds(200)
};

builder.Services.AddChatClient(new OllamaApiClient(ollamaHttpClient, ollamaModel, jsonSerializerContext: null))
    .UseFunctionInvocation()
    .UseOpenTelemetry(sourceName: OllamaSource, configure: c => { c.EnableSensitiveData = true; });
builder.Services.AddSingleton<IClosetService, ClosetService>();
builder.Services.AddSingleton<IWeatherService, WeatherService>();
builder.Services.AddSingleton<IConversationCancellationManager, ConversationCancellationManager>();
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
closet.MapPut("/items/{id}", (IClosetService service, string id, UpsertClosetItemRequest request) =>
{
    var updated = service.Update(id, request);
    return updated is null ? Results.NotFound() : Results.Ok(updated);
});
closet.MapDelete("/items/{id}", (IClosetService service, string id) =>
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

app.MapPost("/api/chat/agent-loop/stream", async (
    HttpContext context,
    AgentLoopRequest request,
    IAgentLoopService loopService,
    CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Prompt))
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await context.Response.WriteAsJsonAsync(new { error = "Prompt is required." }, cancellationToken);
        return;
    }

    context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();
    context.Response.ContentType = "application/x-ndjson";
    context.Response.Headers.CacheControl = "no-cache";
    context.Response.Headers.Append("X-Accel-Buffering", "no");

    await context.Response.StartAsync(cancellationToken);

    await foreach (var item in loopService.StreamAsync(request, cancellationToken))
    {
        await JsonSerializer.SerializeAsync(context.Response.Body, item, streamJsonOptions, cancellationToken);
        await context.Response.WriteAsync("\n", cancellationToken);
        await context.Response.Body.FlushAsync(cancellationToken);
    }
});

app.MapPost("/api/chat/stop/{conversationId}", (
    string conversationId,
    IConversationCancellationManager cancellationManager) =>
{
    if (string.IsNullOrWhiteSpace(conversationId))
    {
        return Results.BadRequest("Conversation ID is required.");
    }

    var wasCancelled = cancellationManager.RequestCancel(conversationId);
    
    return Results.Ok(new 
    { 
        success = wasCancelled, 
        message = wasCancelled 
            ? "Chat conversation has been stopped." 
            : "No active conversation found with the specified ID."
    });
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
