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
    Func<DateTimeOffset> clock)
{
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
        if (destination.CircuitOpenUntilUtc is { } until && until > now)
        {
            deliveries.Update(delivery with { NextAttemptUtc = until });
            return;
        }

        try
        {
            using var request = BuildRequest(destination, delivery.RequestBody ?? "");
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(destination.TimeoutSeconds));

            using var response = await http.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
            var body = await SafeReadAsync(response, timeoutCts.Token).ConfigureAwait(false);

            if (response.IsSuccessStatusCode)
            {
                await OnSuccess(delivery, destination, (int)response.StatusCode, body, now).ConfigureAwait(false);
            }
            else
            {
                OnFailure(delivery, destination, (int)response.StatusCode, $"HTTP {(int)response.StatusCode}", body, now);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
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
