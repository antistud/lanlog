namespace Logrr.Server;

/// <summary>
/// Windows integrated sign-in (SPEC section 11), bound from <c>Logrr:Auth:Windows</c>.
/// Off by default: it only works where the host is configured for it (IIS Windows
/// Authentication, or Kestrel on a domain-joined box), so enabling it blindly would
/// hand every visitor a 401 they cannot answer.
/// </summary>
public sealed class WindowsAuthOptions
{
    /// <summary>Register the Negotiate scheme and expose <c>/auth/windows</c>.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Send anonymous visitors straight through the Windows handshake instead of showing the
    /// sign-in form. The form stays reachable at <c>/login?local=1</c> - which is what a denied
    /// or unmapped Windows identity lands on, so a bad mapping can never lock everyone out.
    /// </summary>
    public bool AutoSignIn { get; init; } = true;
}
