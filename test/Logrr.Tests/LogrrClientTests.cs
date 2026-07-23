using System.Net;
using System.Text.Json;
using Logrr.Client;
using Logrr.Client.Logging;
using Logrr.Core;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Logrr.Tests;

public class LogrrClientTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 23, 14, 0, 0, TimeSpan.Zero);

    private static LogrrClientOptions Options(LogrrLevel min = LogrrLevel.Verbose) => new()
    {
        Endpoint = "https://logrr.test",
        ApiKey = "lg_billing_7Kq2xR9mNp4vB1sT6wZaLd",
        MinimumLevel = min,
        FlushInterval = TimeSpan.FromMinutes(5), // don't let the timer fire mid-test
    };

    private static ClefParseResult ParseFirstLine(string body)
    {
        var line = body.Split('\n')[0];
        using var doc = JsonDocument.Parse(line);
        return ClefParser.Parse(doc.RootElement, Now);
    }

    [Fact]
    public async Task Client_emits_parseable_clef_with_auth_header()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var client = new LogrrClient(Options(), new HttpClient(handler));

        client.Log(LogrrLevel.Error, "Payment {Amount} failed for {UserId}",
            new InvalidOperationException("boom"),
            new Dictionary<string, object?> { ["Amount"] = 49.99, ["UserId"] = 1042L });
        await client.FlushAsync();

        var req = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, req.Method);
        Assert.EndsWith("/api/events/raw", req.RequestUri!.ToString());
        Assert.Equal("lg_billing_7Kq2xR9mNp4vB1sT6wZaLd", req.Headers.GetValues("X-Logrr-ApiKey").Single());
        Assert.Equal("application/vnd.serilog.clef", req.Content!.Headers.ContentType!.MediaType);

        var result = ParseFirstLine(handler.Bodies.Single());
        Assert.True(result.Ok);
        var e = result.Event!;
        Assert.Equal(Contracts.LogLevel.Error, e.Level);
        Assert.Equal("Payment {Amount} failed for {UserId}", e.Template);
        Assert.Contains("boom", e.Exception);
        Assert.Equal(49.99, e.Properties["Amount"]);
        Assert.Equal(1042L, e.Properties["UserId"]);
    }

    [Fact]
    public async Task Events_below_minimum_level_are_dropped()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var client = new LogrrClient(Options(LogrrLevel.Warning), new HttpClient(handler));

        client.Log(LogrrLevel.Information, "chatty");
        await client.FlushAsync();

        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Multiple_events_ship_as_one_batch()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var client = new LogrrClient(Options(), new HttpClient(handler));

        for (var i = 0; i < 3; i++) { client.Log(LogrrLevel.Information, "event {N}", properties: new Dictionary<string, object?> { ["N"] = i }); }
        await client.FlushAsync();

        var body = Assert.Single(handler.Bodies);
        Assert.Equal(3, body.Split('\n').Length);
    }

    [Fact]
    public async Task Server_failure_is_swallowed_and_counted()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));
        using var client = new LogrrClient(Options(), new HttpClient(handler));

        client.Log(LogrrLevel.Error, "boom");
        await client.FlushAsync(); // must not throw

        Assert.Equal(1, client.DroppedCount);
    }

    [Fact]
    public async Task Mel_provider_preserves_template_properties_and_source()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.Created));
        using var provider = new LogrrLoggerProvider(Options(), new HttpClient(handler));
        var logger = provider.CreateLogger("Billing.Processor");

        logger.LogError(new InvalidOperationException("boom"),
            "Payment {Amount} failed for {UserId}", 49.99, 1042);
        await provider.FlushAsync();

        var result = ParseFirstLine(handler.Bodies.Single());
        Assert.True(result.Ok);
        var e = result.Event!;
        Assert.Equal(Contracts.LogLevel.Error, e.Level);
        Assert.Equal("Payment {Amount} failed for {UserId}", e.Template);
        Assert.Equal("Billing.Processor", e.Source);
        Assert.Contains("boom", e.Exception);
        Assert.Equal(49.99, e.Properties["Amount"]);
        Assert.Equal(1042L, e.Properties["UserId"]);
    }
}
