namespace Logrr.Realtime;

/// <summary>Realtime tuning (SPEC §8, §12).</summary>
public sealed class RealtimeOptions
{
    public int FrameIntervalMs { get; set; } = 250;
    public int MaxEventsPerFrame { get; set; } = 200;
    public int SubscriptionBufferSize { get; set; } = 2000;
    public int MaxSubscriptions { get; set; } = 50;
    public int MaxSubscriptionsPerUser { get; set; } = 5;
    public int ReconnectBackfillLimit { get; set; } = 500;
}
