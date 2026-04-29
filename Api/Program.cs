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

builder.AddOllamaApiClient("model")
    .AddKeyedChatClient("chat", c => { c.EnableSensitiveData = true; })
    .UseFunctionInvocation()
    .UseOpenTelemetry(sourceName: OllamaSource, configure: c => { c.EnableSensitiveData = true; });
builder.AddOllamaApiClient("embeddings")
    .AddKeyedEmbeddingGenerator("embeddings");
    //.UseOpenTelemetry(sourceName: OllamaSource, configure: c => { c.EnableSensitiveData = true; });

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
