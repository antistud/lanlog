using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Logrr.Notify;

/// <summary>
/// Drains the delivery queue (SPEC §10.5): sends each pending webhook with exponential
/// backoff, a per-destination circuit breaker, HMAC signing, and ticket-id extraction from
/// the response. Durable — a webhook outage delays tickets, it does not lose them.
/// </summary>
public sealed class DeliveryDispatcher(
    DeliveryStore deliveries,
    DestinationStore destinations,
    TicketLinkStore ticketLinks,
    ISecretProtector protector,
    NotifyOptions options,
    HttpClient http,
    Func<DateTimeOffset> clock,
    ISmtpSender? smtpSender = null)
{
    private readonly ISmtpSender _smtp = smtpSender ?? new MailKitSmtpSender();

    /// <summary>Run the dispatch loop until cancelled (hosted by the server).</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await ProcessDueAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // A bad round must not kill the dispatcher; next tick retries.
            }
        }
    }

    /// <summary>Process one round of due deliveries, bounded by the configured concurrency.</summary>
    public async Task ProcessDueAsync(CancellationToken ct)
    {
        var now = clock();

        // Global hourly cap across all destinations (SPEC §12).
        if (deliveries.CountSince(now.AddHours(-1)) >= options.GlobalMaxDeliveriesPerHour)
        {
            return;
        }

        var due = deliveries.ClaimDue(now, options.DispatcherConcurrency * 4);
        if (due.Count == 0)
        {
            return;
        }

        using var gate = new SemaphoreSlim(options.DispatcherConcurrency);
        var tasks = due.Select(async delivery =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await AttemptAsync(delivery, ct).ConfigureAwait(false);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private async Task AttemptAsync(Delivery delivery, CancellationToken ct)
    {
        var now = clock();
        var destination = destinations.Get(delivery.DestinationId);
        if (destination is null)
        {
            deliveries.Update(delivery with
            {
                Status = DeliveryStatus.DeadLettered,
                Error = "destination no longer exists",
            });
            return;
        }

        // Circuit breaker: hold rather than hammer while the circuit is open (SPEC §10.5).
        // A Test is exempt: it is an operator standing in front of the destination asking "is it
        // fixed yet?", and holding that for 15 minutes answers the wrong question. The breaker
        // exists to spare a struggling endpoint an automated flood, not to block one deliberate
        // probe -- and a probe that succeeds closes the circuit, which is the point of running it.
        if (delivery.Source != DeliverySource.Test
            && destination.CircuitOpenUntilUtc is { } until && until > now)
        {
            deliveries.Update(delivery with { NextAttemptUtc = until });
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(destination.TimeoutSeconds));
        try
        {
            if (destination.Kind == DestinationKind.Smtp)
            {
                var password = protector.Unprotect(destination.AuthSecret);
                await _smtp.SendAsync(destination, delivery.Subject ?? "(no subject)",
                    delivery.RequestBody ?? "", password, timeoutCts.Token).ConfigureAwait(false);
                // Email has no response body to mine for a ticket; 250 is SMTP's "OK".
                await OnSuccess(delivery, destination, 250, "", now).ConfigureAwait(false);
            }
            else
            {
                using var request = BuildRequest(destination, delivery.RequestBody ?? "");
                using var response = await http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
                var body = await SafeReadAsync(response, timeoutCts.Token).ConfigureAwait(false);

                if (response.IsSuccessStatusCode)
                {
                    await OnSuccess(delivery, destination, (int)response.StatusCode, body, now).ConfigureAwait(false);
                }
                else if (IsPermanent(response.StatusCode))
                {
                    OnPermanentFailure(delivery, (int)response.StatusCode,
                        $"HTTP {(int)response.StatusCode} - the destination rejected this request; " +
                        "retrying the same body cannot succeed", body);
                }
                else
                {
                    OnFailure(delivery, destination, (int)response.StatusCode, $"HTTP {(int)response.StatusCode}", body, now);
                }
            }
        }
        // Webhook failures are narrow network faults; an SMTP send can fault in many ways
        // (auth, TLS, socket, protocol), and any of them is simply a delivery failure to retry.
        catch (Exception ex) when (destination.Kind == DestinationKind.Smtp
            || ex is HttpRequestException or OperationCanceledException or IOException)
        {
            OnFailure(delivery, destination, null, ex.GetType().Name + ": " + ex.Message, null, now);
        }
    }

    private Task OnSuccess(Delivery delivery, Destination destination, int status, string body, DateTimeOffset now)
    {
        destinations.RecordSuccess(destination.Id);

        var ticketId = JsonPath.Extract(body, destination.TicketIdPath);
        var ticketUrl = JsonPath.Extract(body, destination.TicketUrlPath);

        deliveries.Update(delivery with
        {
            Status = DeliveryStatus.Delivered,
            ResponseStatus = status,
            ResponseSnippet = Snippet(body),
            TicketId = ticketId,
            TicketUrl = ticketUrl,
            Error = null,
        });

        if (ticketId is not null || ticketUrl is not null)
        {
            ticketLinks.Create(new TicketLink
            {
                Id = Guid.NewGuid().ToString("N"),
                AppId = delivery.AppId ?? "",
                EventType = delivery.EventType,
                DeliveryId = delivery.Id,
                TicketId = ticketId,
                TicketUrl = ticketUrl,
                CreatedUtc = now,
            });
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// A 4xx says the request itself is wrong, and a queued delivery's body is frozen at enqueue
    /// time -- so every retry re-sends the identical payload to the endpoint that just rejected
    /// it. 408 and 429 are the exceptions: both mean "same request, later". Everything else is
    /// dead on arrival, and treating it as a transient fault burns the retry schedule and trips
    /// the circuit breaker over what is really a bad template.
    /// </summary>
    private static bool IsPermanent(HttpStatusCode status) =>
        (int)status is >= 400 and < 500
        && status is not (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests);

    /// <summary>
    /// Dead-letter without touching the destination's failure count: a rejected payload says
    /// nothing about whether the endpoint is healthy, and counting it would open the circuit and
    /// stall every other rule that delivers there.
    /// </summary>
    private void OnPermanentFailure(Delivery delivery, int status, string error, string? body)
    {
        deliveries.Update(delivery with
        {
            Attempt = delivery.Attempt + 1,
            Status = DeliveryStatus.DeadLettered,
            ResponseStatus = status,
            ResponseSnippet = Snippet(body),
            Error = error,
        });
    }

    private void OnFailure(Delivery delivery, Destination destination, int? status, string error, string? body, DateTimeOffset now)
    {
        destinations.RecordFailure(destination.Id, now);

        var nextAttempt = delivery.Attempt + 1;
        var maxAttempts = Math.Min(destination.MaxAttempts, NotifyOptions.Backoff.Length);

        if (nextAttempt >= maxAttempts)
        {
            deliveries.Update(delivery with
            {
                Attempt = nextAttempt,
                Status = DeliveryStatus.DeadLettered,
                ResponseStatus = status,
                ResponseSnippet = Snippet(body),
                Error = error,
            });
            return;
        }

        var delay = NotifyOptions.Backoff[Math.Min(nextAttempt, NotifyOptions.Backoff.Length - 1)];
        deliveries.Update(delivery with
        {
            Attempt = nextAttempt,
            Status = DeliveryStatus.Pending,
            NextAttemptUtc = now.Add(delay),
            ResponseStatus = status,
            ResponseSnippet = Snippet(body),
            Error = error,
        });
    }

    private HttpRequestMessage BuildRequest(Destination destination, string body)
    {
        var request = new HttpRequestMessage(new HttpMethod(destination.Method), destination.Url)
        {
            Content = new StringContent(body, Encoding.UTF8),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(destination.ContentType);

        foreach (var (name, value) in destination.Headers)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        var secret = protector.Unprotect(destination.AuthSecret);
        switch (destination.AuthMode)
        {
            case AuthMode.Bearer when secret is not null:
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
                break;
            case AuthMode.Basic when secret is not null:
                var basic = Convert.ToBase64String(Encoding.UTF8.GetBytes(secret));
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic", basic);
                break;
            case AuthMode.HmacSha256 when secret is not null:
                SignHmac(request, body, secret);
                break;
        }

        return request;
    }

    /// <summary>Sign the body so the receiver can verify authenticity and reject replays.</summary>
    private void SignHmac(HttpRequestMessage request, string body, string secret)
    {
        var timestamp = clock().ToUnixTimeSeconds().ToString();
        var payload = Encoding.UTF8.GetBytes(timestamp + "." + body);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        var signature = Convert.ToHexStringLower(hmac.ComputeHash(payload));
        request.Headers.TryAddWithoutValidation("X-Logrr-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-Logrr-Signature", "sha256=" + signature);
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException)
        {
            return "";
        }
    }

    private static string? Snippet(string? body) =>
        string.IsNullOrEmpty(body) ? null : body.Length <= 500 ? body : body[..500];
}
