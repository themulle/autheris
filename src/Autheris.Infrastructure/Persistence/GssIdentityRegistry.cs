using System.Security;

namespace Autheris.Infrastructure.Persistence;

/// <summary>
/// The native GSS path (gss-ntlmssp, libgssapi_krb5) reads the identity from process-global environment variables (<c>NTLMUSER</c>,
/// <c>KRB5_CONFIG</c>, <c>KRB5_CLIENT_KTNAME</c>, <c>KRB5CCNAME</c>). Two data sources with different accounts or configurations on that
/// path would silently authenticate as whichever source wrote last (identity confusion / impersonation). The first GSS source claims the
/// process identity; a different account or configuration is refused (fail closed).
/// </summary>
internal sealed class GssIdentityRegistry
{
    public static GssIdentityRegistry Shared { get; } = new();

    private readonly Lock _sync = new();
    private (string Domain, string User)? _ntlmClaim;
    private (string? ConfigPath, string? KeytabPath, string? CachePath)? _kerberosClaim;

    /// <summary>True once a GSS source (NTLM or Kerberos) has claimed the process identity.</summary>
    public bool HasClaim
    {
        get
        {
            lock (_sync)
            {
                return _ntlmClaim is not null || _kerberosClaim is not null;
            }
        }
    }

    /// <summary>Claims the identity for the native NTLM GSS path or confirms the existing identical claim; throws for a different account or mechanism mismatch.</summary>
    public void Claim(string? domain, string user)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(user);
        string d = domain ?? string.Empty;
        lock (_sync)
        {
            if (_kerberosClaim is not null)
            {
                throw new SecurityException(
                    "The native GSS environment cannot mix Kerberos and native NTLM Windows Authentication across data sources in the same process; " +
                    "another data source already claimed Kerberos authentication. Use managed NTLM (WindowsAuth.UseManagedNtlm=true) for NTLM sources.");
            }

            if (_ntlmClaim is not { } existing)
            {
                _ntlmClaim = (d, user);
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

    /// <summary>Claims the Kerberos configuration and credentials for the process or confirms the existing identical claim; throws on mismatch or mechanism conflict.</summary>
    public void ClaimKerberos(string? configPath, string? keytabPath, string? cachePath)
    {
        lock (_sync)
        {
            if (_ntlmClaim is not null)
            {
                throw new SecurityException(
                    "The native GSS environment cannot mix native NTLM and Kerberos Windows Authentication across data sources in the same process; " +
                    "another data source already claimed NTLM authentication.");
            }

            var normalizedConfig = string.IsNullOrWhiteSpace(configPath) ? null : Path.GetFullPath(configPath);
            var normalizedKeytab = string.IsNullOrWhiteSpace(keytabPath) ? null : Path.GetFullPath(keytabPath);
            var normalizedCache = string.IsNullOrWhiteSpace(cachePath) ? null : Path.GetFullPath(cachePath);

            if (_kerberosClaim is not { } existing)
            {
                _kerberosClaim = (normalizedConfig, normalizedKeytab, normalizedCache);
                return;
            }

            if (!string.Equals(existing.ConfigPath ?? string.Empty, normalizedConfig ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.KeytabPath ?? string.Empty, normalizedKeytab ?? string.Empty, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(existing.CachePath ?? string.Empty, normalizedCache ?? string.Empty, StringComparison.OrdinalIgnoreCase))
            {
                throw new SecurityException(
                    "The native GSS Kerberos environment uses process-wide variables (KRB5_CONFIG, KRB5_CLIENT_KTNAME, KRB5CCNAME) and supports " +
                    "one identity per process; another data source already uses different Kerberos configuration/credentials.");
            }
        }
    }
}
