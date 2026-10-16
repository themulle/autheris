using System.Security;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// The native GSS path (gss-ntlmssp) reads the identity from process-global environment variables (<c>NTLMUSER</c>, <c>NETBIOS_DOMAIN_NAME</c>,
/// <c>NTLM_USER_FILE</c>). Two data sources with different accounts on that path would silently authenticate as whichever source wrote last
/// (identity confusion). The first GSS source claims the process identity; a different account is refused (fail closed) with a hint to use
/// managed NTLM, which keeps credentials per connection.
/// </summary>
internal sealed class GssIdentityRegistry
{
    public static GssIdentityRegistry Shared { get; } = new();

    private readonly Lock _sync = new();
    private (string Domain, string User)? _claim;

    /// <summary>True once a GSS source has claimed the process identity.</summary>
    public bool HasClaim
    {
        get
        {
            lock (_sync)
            {
                return _claim is not null;
            }
        }
    }

    /// <summary>Claims the identity for the GSS path or confirms the existing identical claim; throws for a different account.</summary>
    public void Claim(string? domain, string user)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        string d = domain ?? string.Empty;
        lock (_sync)
        {
            if (_claim is not { } existing)
            {
                _claim = (d, user);
                return;
            }

            if (!string.Equals(existing.Domain, d, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.User, user, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityException(
                    "The native GSS (gss-ntlmssp) path uses process-wide identity variables and supports one Windows account per process; " +
                    "another data source already uses a different account. Use managed NTLM (WindowsAuth.UseManagedNtlm=true) for data sources with different accounts.");
            }
        }
    }
}
