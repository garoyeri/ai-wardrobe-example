using Api.Services;
using Shared.Contracts;

namespace Api.Extensions;

public static class WeatherEndpointsExtensions
{
    public static IEndpointRouteBuilder MapWeatherEndpoints(this IEndpointRouteBuilder app)
    {
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

        return app;
    }
}
