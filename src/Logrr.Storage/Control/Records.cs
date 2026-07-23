using Logrr.Contracts;

namespace Logrr.Storage.Control;

/// <summary>UI/API roles (SPEC §11).</summary>
public enum UserRole
{
    User = 0,
    Admin = 1,
}

/// <summary>A row of the <c>apps</c> table (SPEC §4.4, §5.1).</summary>
public sealed record AppRecord
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string? Description { get; init; }
    public int RetentionDays { get; init; } = 14;
    public int MaxSizeMb { get; init; } = 2048;
    public LogLevel MinimumLevel { get; init; } = LogLevel.Verbose;
    public IReadOnlyList<string> IndexedProperties { get; init; } = [];
    public bool IsEnabled { get; init; } = true;
    public DateTimeOffset CreatedUtc { get; init; }
}

/// <summary>A row of the <c>tokens</c> table (SPEC §5.2). The full secret is never stored.</summary>
public sealed record TokenRecord
{
    public required string Id { get; init; }
    public required string AppId { get; init; }
    public string? Name { get; init; }
    public required string Prefix { get; init; }
    public required byte[] Hash { get; init; }
    public TokenScopes Scopes { get; init; }
    public DateTimeOffset? ExpiresUtc { get; init; }
    public DateTimeOffset? LastUsedUtc { get; init; }
    public DateTimeOffset? RevokedUtc { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }

    public bool IsActive(DateTimeOffset now) =>
        RevokedUtc is null && (ExpiresUtc is null || ExpiresUtc > now);
}

/// <summary>A row of the <c>users</c> table (SPEC §11).</summary>
public sealed record UserRecord
{
    public required string Id { get; init; }
    public required string Username { get; init; }
    public byte[]? PasswordHash { get; init; }
    public byte[]? PasswordSalt { get; init; }
    public UserRole Role { get; init; }
    public bool MustChangePassword { get; init; }
    public IReadOnlyList<string>? AppAccess { get; init; }
    public DateTimeOffset CreatedUtc { get; init; }
}
