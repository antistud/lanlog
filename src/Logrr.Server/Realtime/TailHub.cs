using System.Collections.Concurrent;
using Logrr.Contracts;
using Logrr.Core.Filters;
using Logrr.Realtime;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace Logrr.Server.Realtime;

/// <summary>Maps connection ids to their subscription ids so they can be swept on disconnect.</summary>
public sealed class ConnectionSubscriptions
{
    private readonly ConcurrentDictionary<string, HashSet<string>> _byConnection = new();

    public void Add(string connectionId, string subscriptionId) =>
        _byConnection.GetOrAdd(connectionId, _ => []).Add(subscriptionId);

    public IReadOnlyCollection<string> Remove(string connectionId) =>
        _byConnection.TryRemove(connectionId, out var set) ? set : [];
}

/// <summary>
/// The live-tail SignalR hub (SPEC §8.2, §9). Blazor Server reuses its own circuit for this,
/// so there is one transport and one reconnect story. Frames are pushed via
/// <c>OnFrame</c> from the broker's batcher, not per event.
/// </summary>
[Authorize]
public sealed class TailHub(RealtimeBroker broker, IHubContext<TailHub> context, ConnectionSubscriptions registry)
    : Hub
{
    public object Subscribe(SubscribeRequest request)
    {
        var connectionId = Context.ConnectionId;
        var userId = Context.UserIdentifier ?? connectionId;

        try
        {
            var id = broker.Subscribe(request, userId,
                frame => context.Clients.Client(connectionId).SendAsync("OnFrame", frame));
            registry.Add(connectionId, id);
            return new { subscriptionId = id };
        }
        catch (SubscriptionRejectedException ex)
        {
            return new { error = ex.Message };
        }
        catch (FilterParseException ex)
        {
            return new { error = $"invalid filter: {ex.Message}" };
        }
    }

    public void Unsubscribe(string subscriptionId) => broker.Unsubscribe(subscriptionId);

    public object UpdateFilter(string subscriptionId, string? minLevel, string? filter)
    {
        try
        {
            broker.UpdateFilter(subscriptionId, minLevel, filter);
            return new { ok = true };
        }
        catch (FilterParseException ex)
        {
            return new { error = $"invalid filter: {ex.Message}" };
        }
    }

    public void Pause(string subscriptionId) => broker.Pause(subscriptionId);

    public void Resume(string subscriptionId) => broker.Resume(subscriptionId);

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        foreach (var id in registry.Remove(Context.ConnectionId))
        {
            broker.Unsubscribe(id);
        }
        return base.OnDisconnectedAsync(exception);
    }
}
