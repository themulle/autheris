namespace Autheris.Api.Security;

using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed class RejectAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public RejectAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder)
    {
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.Items.TryGetValue(GatewayAuthSchemeSelector.RejectReasonItemKey, out var reason) && reason is string code)
        {
            // Fixed reason code only: no header value reaches the audit record, the log or the response.
            Logger.LogWarning("Authentication rejected: {Reason}.", code);
            if (Context.RequestServices.GetService(typeof(Autheris.Application.Audit.IAuditWriter)) is Autheris.Application.Audit.IAuditWriter audit)
            {
                await audit.RecordBestEffortAsync(new Autheris.Domain.Model.AuditLogEntry
                {
                    TenantId = Autheris.Domain.Common.TenantId.Platform,
                    EventType = Autheris.Domain.Audit.AuditEventTypes.AuthFailed,
                    Decision = "DENY",
                    TraceId = Context.TraceIdentifier,
                    DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { reason = code })
                }, Context.RequestAborted).ConfigureAwait(false);
            }

            return AuthenticateResult.Fail(code);
        }

        return AuthenticateResult.Fail("Authentication rejected. Unknown issuer or untrusted token.");
    }
}
