using Api.Services;
using Api.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using OllamaSharp;
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

app.MapClosetEndpoints();
app.MapWeatherEndpoints();
app.MapChatEndpoints(streamJsonOptions);

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
