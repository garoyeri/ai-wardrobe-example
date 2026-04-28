using Api.Services;
using Shared.Contracts;

namespace Api.Extensions;

public static class ClosetEndpointsExtensions
{
    public static IEndpointRouteBuilder MapClosetEndpoints(this IEndpointRouteBuilder app)
    {
        var closet = app.MapGroup("/api/closet");

        closet.MapGet("/items", (IClosetService service) => Results.Ok(service.List()));
        closet.MapPost("/items/search", (IClosetService service, ClosetSearchRequest request) =>
        {
            var result = service.Search(request);
            return Results.Ok(result);
        });
        closet.MapPost("/items", (IClosetService service, UpsertClosetItemRequest request) =>
        {
            if (!Enum.IsDefined(request.Role))
            {
                return Results.BadRequest("A valid outfit role is required.");
            }

            if (string.IsNullOrWhiteSpace(request.Pattern))
            {
                return Results.BadRequest("A pattern is required.");
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

        return app;
    }
}
