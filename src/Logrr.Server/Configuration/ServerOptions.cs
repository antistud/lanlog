namespace Logrr.Server;

/// <summary>Host-level settings mirrored for endpoints that need them (SPEC §12, §13).</summary>
public sealed class ServerOptions
{
    public long MinFreeDiskMb { get; set; } = 5120;
    public long MaxRequestBytes { get; set; } = 10_485_760;
    public string PublicBaseUrl { get; set; } = "https://logrr.internal";
}
