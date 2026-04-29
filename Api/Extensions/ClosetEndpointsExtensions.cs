using Api.Services;
using Shared.Contracts;

namespace Api.Extensions;

public static class ClosetEndpointsExtensions
{
    public static IEndpointRouteBuilder MapClosetEndpoints(this IEndpointRouteBuilder app)
    {
        var closet = app.MapGroup("/api/closet");

        closet.MapGet("/items", async (IClosetService service, CancellationToken cancellationToken) =>
            Results.Ok(await service.ListAsync(cancellationToken)));

        closet.MapGet("/items/{id}", async (IClosetService service, string id, CancellationToken cancellationToken) =>
        {
            var item = await service.GetAsync(id, cancellationToken);
            return item is null ? Results.NotFound() : Results.Ok(item);
        });

        closet.MapPost("/items/search", async (IClosetService service, ClosetSearchRequest request, CancellationToken cancellationToken) =>
        {
            var result = await service.SearchAsync(request, cancellationToken);
            return Results.Ok(result);
        });

        closet.MapPost("/items", async (IClosetService service, UpsertClosetItemRequest request, CancellationToken cancellationToken) =>
        {
            if (!Enum.IsDefined(request.Role))
            {
                return Results.BadRequest("A valid outfit role is required.");
            }

            if (string.IsNullOrWhiteSpace(request.Pattern))
            {
                return Results.BadRequest("A pattern is required.");
            }

            return Results.Ok(await service.AddAsync(request, cancellationToken));
        });

        closet.MapPut("/items/{id}", async (IClosetService service, string id, UpsertClosetItemRequest request, CancellationToken cancellationToken) =>
        {
            var updated = await service.UpdateAsync(id, request, cancellationToken);
            return updated is null ? Results.NotFound() : Results.Ok(updated);
        });

        closet.MapDelete("/items/{id}", async (IClosetService service, string id, CancellationToken cancellationToken) =>
            await service.DeleteAsync(id, cancellationToken) ? Results.NoContent() : Results.NotFound());

        closet.MapPost("/reset", async (IClosetService service, CancellationToken cancellationToken) =>
        {
            await service.ResetAsync(cancellationToken);
            return Results.NoContent();
        });

        return app;
    }
}
