namespace Logrr.Notify;

/// <summary>Notify tuning (SPEC §12).</summary>
public sealed class NotifyOptions
{
    public int DispatcherConcurrency { get; set; } = 4;

    /// <summary>Rules only evaluate events this recent — backfill protection (SPEC §10.7).</summary>
    public int RuleEvaluationMaxAgeMinutes { get; set; } = 15;

    public int GlobalMaxDeliveriesPerHour { get; set; } = 500;

    public int DeadLetterRetentionDays { get; set; } = 30;

    /// <summary>Base for the public event permalink used in templates (<c>{{link.event}}</c>).</summary>
    public string PublicBaseUrl { get; set; } = "https://logrr.internal";

    /// <summary>Backoff schedule before dead-lettering (SPEC §10.5).</summary>
    public static readonly TimeSpan[] Backoff =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10),
        TimeSpan.FromHours(1),
    ];

    public const int CircuitFailureThreshold = 5;
    public static readonly TimeSpan CircuitOpenDuration = TimeSpan.FromMinutes(15);

    /// <summary>Above this match rate a rule stops per-key work and emits one storm delivery.</summary>
    public const int StormRatePerSecond = 100;
}

/// <summary>
/// Encrypts/decrypts destination secrets at rest (SPEC §10.1, §11). Implemented by the host
/// with ASP.NET Data Protection; a pass-through is fine for tests.
/// </summary>
public interface ISecretProtector
{
    byte[]? Protect(string? plaintext);
    string? Unprotect(byte[]? ciphertext);
}
