namespace Logrr.Server;

/// <summary>
/// In-process pub/sub so UI surfaces refresh on real activity rather than blind polling
/// (SPEC §8.6). Raised from the ingest commit hook and the delivery dispatcher; consumed by
/// Blazor components, which marshal to their circuit with <c>InvokeAsync</c>.
/// </summary>
public sealed class LiveSignals
{
    /// <summary>Fired after a batch commits, with the app that received it.</summary>
    public event Action<string>? AppActivity;

    /// <summary>Fired when the delivery queue's state may have changed.</summary>
    public event Action? DeliveriesChanged;

    public void RaiseAppActivity(string appId) => AppActivity?.Invoke(appId);

    public void RaiseDeliveriesChanged() => DeliveriesChanged?.Invoke();
}
