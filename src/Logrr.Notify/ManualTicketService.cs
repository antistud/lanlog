using Logrr.Core;

namespace Logrr.Notify;

/// <summary>
/// Manual "Create ticket" submissions and the destination Test button (SPEC §10.6, §10.7),
/// both routed through the same durable queue and retry machinery as rule-fired deliveries.
/// </summary>
public sealed class ManualTicketService(
    DeliveryStore deliveries,
    DestinationStore destinations,
    NotifyOptions options,
    Func<DateTimeOffset> clock)
{
    public readonly record struct SubmitResult(string? DeliveryId, string? Error);

    /// <summary>Operator-initiated ticket from an event (or free text).</summary>
    public SubmitResult Submit(string destinationId, string? appId, string? appName,
        LogEvent? ev, string? eventId, string? title, string? body, string? userName)
    {
        var destination = destinations.Get(destinationId);
        if (destination is null)
        {
            return new SubmitResult(null, "destination not found");
        }

        var ctx = new WebhookContext
        {
            Event = ev,
            EventId = eventId,
            AppId = appId,
            AppName = appName,
            UserName = userName,
            Title = title,
            Body = body,
            BaseUrl = options.PublicBaseUrl,
        };

        return Enqueue(destination, ctx, DeliverySource.Manual, appId, ev?.EventType);
    }

    /// <summary>Send a synthetic event through the full template + transport path.</summary>
    public SubmitResult Test(string destinationId)
    {
        var destination = destinations.Get(destinationId);
        if (destination is null)
        {
            return new SubmitResult(null, "destination not found");
        }

        var now = clock();
        var sample = new LogEvent
        {
            Timestamp = now,
            Level = Contracts.LogLevel.Error,
            Template = "Test event from Logrr for {Destination}",
            Message = $"Test event from Logrr for {destination.Name}",
            Exception = "System.Exception: synthetic test exception\n  at Logrr.Test()",
            EventType = EventTypeHash.Compute("Test event from Logrr for {Destination}", ""),
            Properties = new Dictionary<string, object?> { ["Destination"] = destination.Name },
        };

        var ctx = new WebhookContext
        {
            Event = sample,
            EventId = "00000000:0",
            Title = $"[Test] {destination.Name}",
            Body = "This is a Logrr test delivery.",
            BaseUrl = options.PublicBaseUrl,
        };

        return Enqueue(destination, ctx, DeliverySource.Test, appId: null, sample.EventType);
    }

    private SubmitResult Enqueue(Destination destination, WebhookContext ctx, DeliverySource source, string? appId, long? eventType)
    {
        var now = clock();
        var rendered = WebhookRenderer.Render(destination.BodyTemplate, ctx, destination.IsJson);
        var subject = destination.Kind == DestinationKind.Smtp
            ? WebhookRenderer.Render(destination.SmtpSubjectTemplate, ctx, jsonMode: false).Body
            : null;

        var id = Guid.NewGuid().ToString("N");
        deliveries.Enqueue(new Delivery
        {
            Id = id,
            DestinationId = destination.Id,
            AppId = appId,
            Source = source,
            EventType = eventType,
            CreatedUtc = now,
            Attempt = 0,
            NextAttemptUtc = now,
            Status = rendered.Ok ? DeliveryStatus.Pending : DeliveryStatus.Failed,
            RequestBody = rendered.Body,
            Subject = subject,
            Error = rendered.Ok ? null : rendered.Error,
        });

        return new SubmitResult(id, rendered.Ok ? null : rendered.Error);
    }
}
