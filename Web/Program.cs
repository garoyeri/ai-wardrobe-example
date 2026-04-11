using Web.Components;
using Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var apiBaseAddress = builder.Configuration["services:api:https:0"]
    ?? builder.Configuration["services:api:http:0"]
    ?? throw new InvalidOperationException(
        "Api endpoint is not configured. Ensure AppHost uses .WithReference(api) for the Web project.");

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

app.Run();
