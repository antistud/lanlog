using System.Net;
using Logrr.Notify;
using Xunit;

namespace Logrr.Tests;

public class DeliveryDispatcherTests : IDisposable
{
    private readonly NotifyTestHarness _h = new();
    private DateTimeOffset _now = new(2026, 7, 23, 12, 0, 0, TimeSpan.Zero);

    private void AddDestination(string id = "d1") => _h.Destinations.Create(new Destination
    {
        Id = id, Name = "Generic", Url = "https://example/hook",
        BodyTemplate = "{}", TicketIdPath = "$.number", TicketUrlPath = "$.html_url",
        MaxAttempts = 6, CreatedUtc = _now,
    });

    private void Enqueue(string id = "x1", string dest = "d1") => _h.Deliveries.Enqueue(new Delivery
    {
        Id = id, DestinationId = dest, AppId = "billing", Source = DeliverySource.Rule,
        CreatedUtc = _now, Attempt = 0, NextAttemptUtc = _now, Status = DeliveryStatus.Pending,
        RequestBody = "{}",
    });

    private DeliveryDispatcher Dispatcher(StubHandler handler) => new(
        _h.Deliveries, _h.Destinations, _h.TicketLinks, new PlaintextProtector(),
        new NotifyOptions(), new HttpClient(handler), () => _now);

    [Fact]
    public async Task Successful_delivery_extracts_ticket_and_links_it()
    {
        AddDestination();
        Enqueue();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"number":42,"html_url":"https://git/issues/42"}"""),
        });

        await Dispatcher(handler).ProcessDueAsync(CancellationToken.None);

        var delivery = _h.Deliveries.Get("x1")!;
        Assert.Equal(DeliveryStatus.Delivered, delivery.Status);
        Assert.Equal("42", delivery.TicketId);
        Assert.Equal("https://git/issues/42", delivery.TicketUrl);

        var links = _h.TicketLinks.ListByApp("billing");
        Assert.Single(links);
        Assert.Equal("https://git/issues/42", links[0].TicketUrl);
    }

    [Fact]
    public async Task Ticket_link_records_event_type_for_grid_badges()
    {
        AddDestination();
        _h.Deliveries.Enqueue(new Delivery
        {
            Id = "et1", DestinationId = "d1", AppId = "billing", Source = DeliverySource.Rule,
            EventType = 987654, CreatedUtc = _now, Attempt = 0, NextAttemptUtc = _now,
            Status = DeliveryStatus.Pending, RequestBody = "{}",
        });
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created)
        {
            Content = new StringContent("""{"number":7,"html_url":"https://git/issues/7"}"""),
        });

        await Dispatcher(handler).ProcessDueAsync(CancellationToken.None);

        var map = _h.TicketLinks.TicketUrlsByEventType("billing");
        Assert.True(map.TryGetValue(987654, out var url));
        Assert.Equal("https://git/issues/7", url);
    }

    [Fact]
    public async Task Failed_delivery_schedules_a_retry_with_backoff()
    {
        AddDestination();
        Enqueue();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("boom"),
        });

        await Dispatcher(handler).ProcessDueAsync(CancellationToken.None);

        var delivery = _h.Deliveries.Get("x1")!;
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(1, delivery.Attempt);
        Assert.NotNull(delivery.NextAttemptUtc);
        Assert.True(delivery.NextAttemptUtc > _now); // not due again immediately

        // The failure counted against the destination's circuit breaker.
        Assert.Equal(1, _h.Destinations.Get("d1")!.ConsecutiveFailures);
    }

    [Fact]
    public async Task Open_circuit_holds_delivery_without_sending()
    {
        AddDestination();
        Enqueue();
        // Trip the breaker: five consecutive failures opens the circuit for 15 minutes.
        for (var i = 0; i < 5; i++)
        {
            _h.Destinations.RecordFailure("d1", _now);
        }
        Assert.NotNull(_h.Destinations.Get("d1")!.CircuitOpenUntilUtc);

        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await Dispatcher(handler).ProcessDueAsync(CancellationToken.None);

        Assert.Empty(handler.Requests); // held, not hammered
        Assert.Equal(DeliveryStatus.Pending, _h.Deliveries.Get("x1")!.Status);
    }

    [Fact]
    public async Task Hmac_mode_signs_the_request()
    {
        _h.Destinations.Create(new Destination
        {
            Id = "d2", Name = "Signed", Url = "https://example/hook", BodyTemplate = "{}",
            AuthMode = AuthMode.HmacSha256, AuthSecret = new PlaintextProtector().Protect("s3cret"),
            CreatedUtc = _now,
        });
        Enqueue("x2", "d2");

        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await Dispatcher(handler).ProcessDueAsync(CancellationToken.None);

        var req = handler.Requests.Single();
        Assert.True(req.Headers.Contains("X-Logrr-Signature"));
        Assert.True(req.Headers.Contains("X-Logrr-Timestamp"));
        Assert.StartsWith("sha256=", req.Headers.GetValues("X-Logrr-Signature").First());
    }

    public void Dispose() => _h.Dispose();
}
