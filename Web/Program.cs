using Web.Components;
using Web.Services;

var builder = WebApplication.CreateBuilder(args);
var logger = LoggerFactory.Create(logging => logging.AddConsole()).CreateLogger("Startup");

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var apiBaseAddress = builder.Configuration["services:api:http:0"]
    ?? builder.Configuration["services:api:https:0"]
    ?? throw new InvalidOperationException(
        "Api endpoint is not configured. Ensure AppHost uses .WithReference(api) for the Web project.");

logger.LogInformation("Configured Wardrobe API base address: {ApiBaseAddress}", apiBaseAddress);

builder.Services.AddHttpClient<WardrobeApiClient>(client =>
{
    client.BaseAddress = new Uri(apiBaseAddress);
});

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapGet("/diagnostics/backend", async (WardrobeApiClient apiClient, CancellationToken cancellationToken) =>
{
    try
    {
        var forecast = await apiClient.GetForecastAsync(cancellationToken);
        return Results.Ok(new
        {
            reachable = true,
            segments = forecast?.Segments.Count ?? 0,
            date = forecast?.Date
        });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            title: "Backend call failed",
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable);
    }
});

app.Run();
