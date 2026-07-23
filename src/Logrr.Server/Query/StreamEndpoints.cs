using System.Text.Json;
using System.Threading.Channels;
using Logrr.Contracts;
using Logrr.Core.Filters;
using Logrr.Realtime;
using Logrr.Server.Auth;

namespace Logrr.Server.Query;

public static class StreamEndpoints
{
    /// <summary>
    /// SSE live tail for CLI consumers (SPEC §7): <c>GET /api/v1/apps/{appId}/stream</c>.
    /// Token-authenticated (Read scope) or a UI session. Each event is one SSE message.
    /// </summary>
    public static void MapStreamEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/apps/{appId}/stream", async (
            string appId, HttpContext http, TokenAuthenticator auth, RealtimeBroker broker,
            string? level, string? filter) =>
        {
            // Read auth: a Read-scoped token for this app, or a signed-in UI session.
            var hasToken = http.Request.Headers.ContainsKey("X-Logrr-ApiKey")
                || http.Request.Headers.ContainsKey("X-Seq-ApiKey")
                || http.Request.Headers.ContainsKey("Authorization");
            string userId;
            if (hasToken)
            {
                var res = auth.Authenticate(http, TokenScopes.Read);
                if (!res.Ok || res.Context!.App.Id != appId)
                {
                    http.Response.StatusCode = res.Failure == AuthFailure.Forbidden ? 403 : 401;
                    return;
                }
                userId = res.Context.Token.Id;
            }
            else if (http.User.Identity?.IsAuthenticated == true)
            {
                userId = http.User.Identity!.Name ?? "session";
            }
            else
            {
                http.Response.StatusCode = 401;
                return;
            }

            http.Response.Headers.ContentType = "text/event-stream";
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers["X-Accel-Buffering"] = "no"; // don't let a proxy buffer the stream

            var channel = Channel.CreateBounded<LogEventDto>(new BoundedChannelOptions(2000)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

            string subscriptionId;
            try
            {
                subscriptionId = broker.Subscribe(
                    new SubscribeRequest { AppId = appId, MinLevel = level, Filter = filter },
                    userId,
                    frame =>
                    {
                        foreach (var e in frame.Events)
                        {
                            channel.Writer.TryWrite(e);
                        }
                        return Task.CompletedTask;
                    });
            }
            catch (FilterParseException ex)
            {
                http.Response.StatusCode = 400;
                await http.Response.WriteAsync($"invalid filter: {ex.Message}");
                return;
            }
            catch (SubscriptionRejectedException ex)
            {
                http.Response.StatusCode = 503;
                await http.Response.WriteAsync(ex.Message);
                return;
            }

            try
            {
                await http.Response.WriteAsync(": connected\n\n", http.RequestAborted);
                await http.Response.Body.FlushAsync(http.RequestAborted);

                await foreach (var e in channel.Reader.ReadAllAsync(http.RequestAborted))
                {
                    await http.Response.WriteAsync($"data: {JsonSerializer.Serialize(e)}\n\n", http.RequestAborted);
                    await http.Response.Body.FlushAsync(http.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected.
            }
            finally
            {
                broker.Unsubscribe(subscriptionId);
            }
        });
    }
}
