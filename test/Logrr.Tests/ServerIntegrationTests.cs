using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using Logrr.Contracts;
using Logrr.Server.Security;
using Logrr.Storage.Control;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Logrr.Tests;

public class ServerIntegrationTests : IDisposable
{
    private readonly string _dataPath = Path.Combine(Path.GetTempPath(), "logrr-it-" + Guid.NewGuid().ToString("N"));
    private readonly WebApplicationFactory<Program> _factory;
    private readonly string _secret;

    public ServerIntegrationTests()
    {
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.UseSetting("Logrr:Storage:DataPath", _dataPath));

        // Seed an app + token directly so the test can ingest with a real API key.
        using var scope = _factory.Services.CreateScope();
        var apps = scope.ServiceProvider.GetRequiredService<AppStore>();
        var tokens = scope.ServiceProvider.GetRequiredService<TokenStore>();
        apps.Create(new AppRecord
        {
            Id = "billing", Name = "Billing", MinimumLevel = LogLevel.Verbose,
            IsEnabled = true, CreatedUtc = DateTimeOffset.UtcNow,
        });
        _secret = TokenSecret.Generate("billing");
        tokens.Create(new TokenRecord
        {
            Id = Guid.NewGuid().ToString("N"), AppId = "billing", Prefix = TokenSecret.Prefix(_secret),
            Hash = TokenSecret.Hash(_secret), Scopes = TokenScopes.Ingest | TokenScopes.Read,
            CreatedUtc = DateTimeOffset.UtcNow,
        });
    }

    private HttpClient Client()
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Logrr-ApiKey", _secret);
        return client;
    }

    [Fact]
    public async Task Clef_ingest_flows_through_to_query()
    {
        var client = Client();

        var clef = string.Join('\n',
            """{"@t":"__T0__","@mt":"Payment {Amount} failed for {UserId}","@l":"Error","Amount":49.99,"UserId":1042}""",
            """{"@t":"__T1__","@mt":"login ok","@l":"Information"}""")
            .Replace("__T0__", DateTimeOffset.UtcNow.ToString("O"))
            .Replace("__T1__", DateTimeOffset.UtcNow.ToString("O"));

        var post = await client.PostAsync("/api/events/raw",
            new StringContent(clef, Encoding.UTF8, "application/vnd.serilog.clef"));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);
        var result = await post.Content.ReadFromJsonAsync<IngestResult>();
        Assert.Equal(2, result!.Accepted);

        // Poll until the batch writer has flushed and the events are queryable.
        EventQueryResponse? page = null;
        for (var i = 0; i < 30 && (page is null || page.Events.Count < 2); i++)
        {
            await Task.Delay(200);
            page = await client.GetFromJsonAsync<EventQueryResponse>("/api/v1/apps/billing/events?limit=10");
        }

        Assert.NotNull(page);
        Assert.Equal(2, page!.Events.Count);
        Assert.Contains(page.Events, e => e.Level == LogLevel.Error && e.Message.Contains("1042"));
    }

    [Fact]
    public async Task LogrrClient_package_ships_events_end_to_end()
    {
        var httpClient = _factory.CreateClient();
        using var logrr = new Logrr.Client.LogrrClient(new Logrr.Client.LogrrClientOptions
        {
            Endpoint = httpClient.BaseAddress!.ToString().TrimEnd('/'),
            ApiKey = _secret,
        }, httpClient);

        logrr.Log(Logrr.Client.LogrrLevel.Error, "Package ship {Amount} for {UserId}",
            new InvalidOperationException("boom"),
            new Dictionary<string, object?> { ["Amount"] = 12.5, ["UserId"] = 99L });
        await logrr.FlushAsync();

        var reader = Client();
        EventQueryResponse? page = null;
        for (var i = 0; i < 30 && (page is null || page.Events.Count == 0); i++)
        {
            await Task.Delay(200);
            page = await reader.GetFromJsonAsync<EventQueryResponse>("/api/v1/apps/billing/events?limit=10");
        }

        Assert.NotNull(page);
        var ev = Assert.Single(page!.Events);
        Assert.Equal(LogLevel.Error, ev.Level);
        Assert.Equal("Package ship {Amount} for {UserId}", ev.Template);
        Assert.Contains("boom", ev.Exception);
    }

    [Fact]
    public async Task Trace_endpoint_returns_the_whole_trace_for_the_tokens_app()
    {
        var client = Client();
        var t0 = DateTimeOffset.UtcNow.AddSeconds(-1);

        var clef = string.Join('\n',
            $$"""{"@t":"{{t0.ToString("O")}}","@mt":"GET /orders","@tr":"trace-a","@sp":"span-1","@st":"{{t0.AddMilliseconds(-200).ToString("O")}}"}""",
            $$"""{"@t":"{{t0.AddMilliseconds(-100).ToString("O")}}","@mt":"loading orders","@tr":"trace-a","@sp":"span-1"}""",
            $$"""{"@t":"{{t0.ToString("O")}}","@mt":"something else","@tr":"trace-b"}""");

        var post = await client.PostAsync("/api/events/raw",
            new StringContent(clef, Encoding.UTF8, "application/vnd.serilog.clef"));
        Assert.Equal(HttpStatusCode.Created, post.StatusCode);

        TraceResponse? trace = null;
        for (var i = 0; i < 30 && (trace is null || trace.Events.Count < 2); i++)
        {
            await Task.Delay(200);
            trace = await client.GetFromJsonAsync<TraceResponse>(
                $"/api/v1/traces/trace-a?near={Uri.EscapeDataString(t0.ToString("O"))}");
        }

        Assert.NotNull(trace);
        // Oldest first, and only this trace.
        Assert.Equal(["loading orders", "GET /orders"], trace!.Events.Select(e => e.Event.Message));
        Assert.All(trace.Events, e => Assert.Equal("billing", e.AppId));

        var span = trace.Events.Single(e => e.Event.Message == "GET /orders").Event;
        Assert.Equal("span-1", span.SpanId);
        Assert.True(span.Properties!.Value.TryGetProperty(Logrr.Core.SpanFields.SpanStart, out _));
    }

    [Fact]
    public async Task Trace_endpoint_refuses_an_app_the_token_cannot_read()
    {
        var client = Client();
        var resp = await client.GetAsync("/api/v1/traces/trace-a?appId=someone-else");
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
    }

    [Fact]
    public async Task Ingest_without_token_is_unauthorized()
    {
        var client = _factory.CreateClient(); // no key
        var post = await client.PostAsync("/api/events/raw", new StringContent("{}"));
        Assert.Equal(HttpStatusCode.Unauthorized, post.StatusCode);
    }

    [Fact]
    public async Task Health_endpoint_reports_status()
    {
        var client = _factory.CreateClient();
        var health = await client.GetFromJsonAsync<HealthDto>("/health");
        Assert.NotNull(health);
        Assert.Contains(health!.Status, new[] { "ok", "degraded" });
    }

    [Fact]
    public async Task Bad_filter_returns_400()
    {
        var client = Client();
        var resp = await client.GetAsync("/api/v1/apps/billing/events?filter=Level%20%3E%3E%203");
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    public void Dispose()
    {
        _factory.Dispose();
        try { Directory.Delete(_dataPath, recursive: true); } catch { /* best effort */ }
    }
}
