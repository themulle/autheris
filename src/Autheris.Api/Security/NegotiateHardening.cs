namespace Autheris.Api.Security;

using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication.Negotiate;

/// <summary>
/// Review E-2: Hardening of the Negotiate (Windows) handler. Credentials are never persisted on the upstream
/// connection (would carry over between clients on shared connections behind a reverse proxy) and, with
/// <c>RequireKerberosOnly</c>, identities authenticated by anything other than Kerberos (NTLM) are rejected.
/// </summary>
public static class NegotiateHardening
{
    public const string KerberosAuthenticationType = "Kerberos";

    public static void Configure(NegotiateOptions options, bool requireKerberosOnly)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PersistNtlmCredentials = false;
        options.PersistKerberosCredentials = false;

        options.Events = new NegotiateEvents
        {
            OnAuthenticated = requireKerberosOnly ? RejectNonKerberos : _ => Task.CompletedTask,
            OnAuthenticationFailed = context =>
            {
                context.Fail(context.Exception ?? new InvalidOperationException("Negotiate authentication failed."));
                return Task.CompletedTask;
            }
        };
    }

    internal static Task RejectNonKerberos(AuthenticatedContext context)
    {
        var type = context.Principal?.Identity?.AuthenticationType;
        if (!string.Equals(type, KerberosAuthenticationType, StringComparison.OrdinalIgnoreCase))
        {
            context.Fail("Only Kerberos authentication is permitted (RequireKerberosOnly).");
        }

        return Task.CompletedTask;
    }
}
