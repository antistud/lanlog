using Logrr.Notify;
using Logrr.Storage;
using Logrr.Storage.Control;

namespace Logrr.Tests;

/// <summary>Shared fixture: a temp control DB plus the Notify stores wired to it.</summary>
internal sealed class NotifyTestHarness : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "logrr-notify-" + Guid.NewGuid().ToString("N"));
    public ControlDatabase Db { get; }
    public DestinationStore Destinations { get; }
    public RuleStore Rules { get; }
    public DeliveryStore Deliveries { get; }
    public OccurrenceStore Occurrences { get; }
    public TicketLinkStore TicketLinks { get; }

    public NotifyTestHarness()
    {
        var paths = new StoragePaths(Root);
        Db = new ControlDatabase(paths);
        Db.Initialize();
        Destinations = new DestinationStore(Db);
        Rules = new RuleStore(Db);
        Deliveries = new DeliveryStore(Db);
        Occurrences = new OccurrenceStore(Db);
        TicketLinks = new TicketLinkStore(Db);
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}

/// <summary>Pass-through protector for tests (production uses ASP.NET Data Protection).</summary>
internal sealed class PlaintextProtector : ISecretProtector
{
    public byte[]? Protect(string? plaintext) =>
        plaintext is null ? null : System.Text.Encoding.UTF8.GetBytes(plaintext);

    public string? Unprotect(byte[]? ciphertext) =>
        ciphertext is null ? null : System.Text.Encoding.UTF8.GetString(ciphertext);
}

/// <summary>Captures outbound requests and returns a scripted response.</summary>
internal sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public List<string> Bodies { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
        return responder(request);
    }
}
