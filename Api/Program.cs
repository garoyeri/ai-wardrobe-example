using Api.Services;
using Api.Extensions;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
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

builder.AddQdrantClient("vector-db");

var ollamaModel = builder.Configuration["Ollama:Model"]
    ?? throw new InvalidOperationException("Missing Ollama model configuration 'Ollama:Model'.");
var embeddingsModel = builder.Configuration["Ollama:Embeddings"]
    ?? throw new InvalidOperationException("Missing Ollama embeddings model configuration 'Ollama:Embeddings'.");

builder.AddOllamaApiClient("ollama", c => { c.SelectedModel = ollamaModel; })
    .AddKeyedChatClient("chat", c => { c.EnableSensitiveData = true; })
    .UseFunctionInvocation()
    .UseOpenTelemetry(sourceName: OllamaSource, configure: c => { c.EnableSensitiveData = true; });
builder.AddOllamaApiClient("ollama", c => { c.SelectedModel = embeddingsModel; })
    .AddKeyedEmbeddingGenerator("embeddings")
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
