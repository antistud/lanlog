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

    private DeliveryDispatcher SmtpDispatcher(ISmtpSender smtp) => new(
        _h.Deliveries, _h.Destinations, _h.TicketLinks, new PlaintextProtector(),
        new NotifyOptions(), new HttpClient(new StubHandler(_ => new HttpResponseMessage())),
        () => _now, smtp);

    /// <summary>Captures SMTP sends; optionally throws to simulate a delivery failure.</summary>
    private sealed class StubSmtp(bool throwOnce = false) : ISmtpSender
    {
        public int Calls { get; private set; }
        public Destination? LastDestination { get; private set; }
        public string? LastSubject { get; private set; }
        public string? LastBody { get; private set; }
        public string? LastPassword { get; private set; }

        public Task SendAsync(Destination destination, string subject, string body, string? password, CancellationToken ct)
        {
            Calls++;
            LastDestination = destination;
            LastSubject = subject;
            LastBody = body;
            LastPassword = password;
            if (throwOnce)
            {
                throw new InvalidOperationException("smtp down");
            }
            return Task.CompletedTask;
        }
    }

    private void AddSmtp(string id = "m1") => _h.Destinations.Create(new Destination
    {
        Id = id, Name = "Ops mail", Kind = DestinationKind.Smtp, Url = "",
        SmtpHost = "smtp.example", SmtpPort = 587, SmtpSecurity = SmtpSecurity.StartTls,
        SmtpUsername = "mailer", SmtpFrom = "logrr@example", SmtpTo = "oncall@example",
        AuthSecret = new PlaintextProtector().Protect("hunter2"),
        BodyTemplate = "body", MaxAttempts = 6, CreatedUtc = _now,
    });

    private void EnqueueEmail(string id = "e1", string dest = "m1") => _h.Deliveries.Enqueue(new Delivery
    {
        Id = id, DestinationId = dest, AppId = "billing", Source = DeliverySource.Rule,
        CreatedUtc = _now, Attempt = 0, NextAttemptUtc = _now, Status = DeliveryStatus.Pending,
        RequestBody = "the body", Subject = "Error: payment failed",
    });

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
    public async Task Rejected_payload_dead_letters_at_once_without_tripping_the_circuit()
    {
        AddDestination();
        Enqueue();
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("the request body doesn't match the schema"),
        });

        await Dispatcher(handler).ProcessDueAsync(CancellationToken.None);

        // The body a queued delivery carries is frozen, so retrying a 400 re-sends the same
        // rejected payload forever. Dead-letter it instead, and leave the breaker alone: the
        // endpoint is healthy, the payload is not.
        var delivery = _h.Deliveries.Get("x1")!;
        Assert.Equal(DeliveryStatus.DeadLettered, delivery.Status);
        Assert.Equal(400, delivery.ResponseStatus);
        Assert.Contains("schema", delivery.ResponseSnippet);
        Assert.Equal(0, _h.Destinations.Get("d1")!.ConsecutiveFailures);
        Assert.Null(_h.Destinations.Get("d1")!.CircuitOpenUntilUtc);
    }

    [Theory]
    [InlineData(HttpStatusCode.RequestTimeout)]
    [InlineData(HttpStatusCode.TooManyRequests)]
    public async Task Retryable_4xx_still_backs_off(HttpStatusCode status)
    {
        AddDestination();
        Enqueue();
        var handler = new StubHandler(_ => new HttpResponseMessage(status));

        await Dispatcher(handler).ProcessDueAsync(CancellationToken.None);

        // 408 and 429 both mean "same request, later" - the one 4xx family worth retrying.
        var delivery = _h.Deliveries.Get("x1")!;
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.True(delivery.NextAttemptUtc > _now);
        Assert.Equal(1, _h.Destinations.Get("d1")!.ConsecutiveFailures);
    }

    [Fact]
    public async Task Test_delivery_is_sent_through_an_open_circuit()
    {
        AddDestination();
        _h.Deliveries.Enqueue(new Delivery
        {
            Id = "t1", DestinationId = "d1", Source = DeliverySource.Test,
            CreatedUtc = _now, Attempt = 0, NextAttemptUtc = _now,
            Status = DeliveryStatus.Pending, RequestBody = "{}",
        });
        for (var i = 0; i < 5; i++)
        {
            _h.Destinations.RecordFailure("d1", _now);
        }
        Assert.NotNull(_h.Destinations.Get("d1")!.CircuitOpenUntilUtc);

        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK));
        await Dispatcher(handler).ProcessDueAsync(CancellationToken.None);

        // The operator's probe goes out even though the breaker is open (a rule-fired delivery
        // in the same state stays held - see Open_circuit_holds_delivery_without_sending).
        Assert.Single(handler.Requests);
        Assert.Equal(DeliveryStatus.Delivered, _h.Deliveries.Get("t1")!.Status);

        // And because it succeeded, the circuit is closed again for everything else.
        Assert.Null(_h.Destinations.Get("d1")!.CircuitOpenUntilUtc);
        Assert.Equal(0, _h.Destinations.Get("d1")!.ConsecutiveFailures);
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

    [Fact]
    public async Task Smtp_destination_sends_email_with_rendered_subject_and_password()
    {
        AddSmtp();
        EnqueueEmail();
        var smtp = new StubSmtp();

        await SmtpDispatcher(smtp).ProcessDueAsync(CancellationToken.None);

        Assert.Equal(1, smtp.Calls);
        Assert.Equal("Error: payment failed", smtp.LastSubject);
        Assert.Equal("the body", smtp.LastBody);
        Assert.Equal("hunter2", smtp.LastPassword); // decrypted from AuthSecret

        var delivery = _h.Deliveries.Get("e1")!;
        Assert.Equal(DeliveryStatus.Delivered, delivery.Status);
        // Email has no response body, so no ticket link is created.
        Assert.Empty(_h.TicketLinks.ListByApp("billing"));
    }

    [Fact]
    public async Task Smtp_send_failure_schedules_a_retry()
    {
        AddSmtp();
        EnqueueEmail();

        await SmtpDispatcher(new StubSmtp(throwOnce: true)).ProcessDueAsync(CancellationToken.None);

        var delivery = _h.Deliveries.Get("e1")!;
        Assert.Equal(DeliveryStatus.Pending, delivery.Status);
        Assert.Equal(1, delivery.Attempt);
        Assert.True(delivery.NextAttemptUtc > _now);
        Assert.Equal(1, _h.Destinations.Get("m1")!.ConsecutiveFailures);
    }

    public void Dispose() => _h.Dispose();
}
