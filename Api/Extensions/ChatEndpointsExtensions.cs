using Api.Services;
using Microsoft.AspNetCore.Http.Features;
using Shared.Contracts;
using System.Text.Json;

namespace Api.Extensions;

public static class ChatEndpointsExtensions
{
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder app, JsonSerializerOptions streamJsonOptions)
    {
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

        return app;
    }
}
