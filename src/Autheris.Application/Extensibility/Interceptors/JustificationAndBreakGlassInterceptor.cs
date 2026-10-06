namespace Autheris.Application.Extensibility.Interceptors;

using System.Text.RegularExpressions;
using Autheris.Application.Extensibility;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

public sealed partial class JustificationAndBreakGlassInterceptor : IIngressInterceptor
{
    private readonly GatewayOptions _options;
    private readonly ILogger<JustificationAndBreakGlassInterceptor> _logger;
    private readonly Autheris.Application.Interfaces.IGovernanceRepository? _governanceRepo;

    [GeneratedRegex(@"^(INC|CHG|SEC|REQ|PR)-\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TicketPattern();

    public JustificationAndBreakGlassInterceptor(
        IOptions<GatewayOptions> options,
        ILogger<JustificationAndBreakGlassInterceptor> logger,
        Autheris.Application.Interfaces.IGovernanceRepository? governanceRepo = null)
    {
        _options = options.Value;
        _logger = logger;
        _governanceRepo = governanceRepo;
    }

    /// <summary>Must match GatewayExtensibilityMiddleware.ClientIpItemKey.</summary>
    public const string ClientIpItemKey = "GatewayClientIp";

    public int Order => 10;

    public async ValueTask<IngressResult> OnIngressAsync(IngressContext context, CancellationToken cancellationToken = default)
    {
        if (!_options.Extensibility.Enabled)
        {
            return IngressResult.Continue();
        }

        var extOptions = _options.Extensibility;
        string breakGlassHeader = extOptions.BreakGlassHeaderName ?? "X-Break-Glass";
        string justificationHeader = extOptions.JustificationHeaderName ?? "X-Access-Justification";

        var isBreakGlassRequested = string.Equals(context.GetHeader(breakGlassHeader), "true", StringComparison.OrdinalIgnoreCase) ||
                                    string.Equals(context.GetHeader(breakGlassHeader), "1", StringComparison.OrdinalIgnoreCase);

        var justification = context.GetHeader(justificationHeader)?.Trim();

        if (!string.IsNullOrWhiteSpace(justification))
        {
            context.Items["AccessJustification"] = justification;
        }

        if (isBreakGlassRequested)
        {
            if (!extOptions.EnableBreakGlass)
            {
                _logger.LogWarning("Break-glass access attempt rejected: Break-glass is disabled globally.");
                return IngressResult.Deny("Break-glass emergency bypass is disabled by gateway policy.", 403);
            }

            // SEC M-06: Anonymous callers can never activate break-glass (no flag, no audit entry),
            // independent of RequireRoleForBreakGlass.
            if (context.User?.Identity?.IsAuthenticated != true)
            {
                _logger.LogWarning("Break-glass access denied: Unauthenticated request attempted to invoke break-glass.");
                return IngressResult.Deny("Unauthenticated requests cannot invoke emergency break-glass.", 401);
            }

            if (extOptions.RequireJustificationForBreakGlass)
            {
                if (string.IsNullOrWhiteSpace(justification) || !TicketPattern().IsMatch(justification))
                {
                    _logger.LogWarning(
                        "Break-glass access challenged: Missing or invalid ticket format '{Justification}'.",
                        justification ?? "<none>");

                    var challengeHeaders = new Dictionary<string, string>
                    {
                        { "X-Challenge-Reason", "Valid enterprise ticket required (e.g., INC-12345, CHG-9876, SEC-001)" }
                    };

                    return IngressResult.Challenge(
                        "Break-glass emergency access requires a valid justification ticket (e.g., INC-12345, CHG-9876).",
                        412,
                        challengeHeaders);
                }
            }

            if (extOptions.RequireRoleForBreakGlass)
            {
                if (context.User?.Identity?.IsAuthenticated != true)
                {
                    _logger.LogWarning("Break-glass access denied: Unauthenticated request attempted to invoke break-glass.");
                    return IngressResult.Deny("Unauthenticated requests cannot invoke emergency break-glass.", 401);
                }

                var user = context.User;
                bool isAuthorized = false;
                foreach (var role in extOptions.BreakGlassAllowedRoles)
                {
                    if (user.IsInRole(role) || user.HasClaim(c => c.Type == System.Security.Claims.ClaimTypes.Role && string.Equals(c.Value, role, StringComparison.OrdinalIgnoreCase)))
                    {
                        isAuthorized = true;
                        break;
                    }
                }

                if (!isAuthorized)
                {
                    _logger.LogWarning(
                        "Break-glass access denied: User '{User}' does not possess any authorized break-glass role ({Roles}).",
                        user.Identity?.Name ?? "Anonymous",
                        string.Join(", ", extOptions.BreakGlassAllowedRoles));
                    return IngressResult.Deny("User is not authorized to invoke emergency break-glass bypass.", 403);
                }
            }

            context.Items["IsBreakGlass"] = true;
            _logger.LogWarning(
                "[AUDIT: BREAK-GLASS ACTIVATED] Emergency elevated access invoked by user '{User}' with ticket '{Ticket}'.",
                context.User?.Identity?.Name ?? "Anonymous",
                justification);

            if (_governanceRepo != null)
            {
                // Audit attribution uses the canonical SID/tenant (SecurityPrincipalContext, else SID/tenant claims), never Identity.Name.
                Autheris.Domain.Security.SecurityPrincipalContext? secCtx =
                    context.Items.TryGetValue(Autheris.Domain.Security.SecurityPrincipalContext.ItemKey, out var secObj)
                        ? secObj as Autheris.Domain.Security.SecurityPrincipalContext
                        : null;
                var userSid = secCtx?.UserSid.Value
                    ?? Autheris.Domain.Common.ClaimsPrincipalExtensions.GetUserSid(context.User)?.Value
                    ?? "Anonymous";
                var tenantId = secCtx?.TenantId ?? Autheris.Domain.Common.ClaimsPrincipalExtensions.GetTenantId(context.User);
                // SEC M-06: IP comes from the connection (trusted-proxy aware), not from the raw X-Forwarded-For header.
                var clientIp = context.Items.TryGetValue(ClientIpItemKey, out var ipObj) && ipObj is string ipStr && !string.IsNullOrWhiteSpace(ipStr)
                    ? ipStr
                    : "unknown";
                var auditEntry = new Autheris.Domain.Model.AuditLogEntry
                {
                    Id = Guid.NewGuid(),
                    OccurredAt = DateTimeOffset.UtcNow,
                    TenantId = tenantId,
                    EventType = "BREAK_GLASS_ACTIVATED",
                    ActorSid = new Autheris.Domain.Common.Sid(userSid),
                    TargetTable = "GATEWAY_INGRESS",
                    Decision = "ALLOW",
                    TraceId = Guid.NewGuid().ToString("N"),
                    DetailsJson = System.Text.Json.JsonSerializer.Serialize(new
                    {
                        tenantId = tenantId.Value,
                        clientIp,
                        action = "BREAK_GLASS",
                        resource = "GATEWAY_INGRESS",
                        justificationTicket = justification,
                        breakGlassActive = true
                    })
                };

                try
                {
                    await _governanceRepo.RecordAuditEventAsync(auditEntry, cancellationToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to record break-glass audit event. Aborting emergency break-glass bypass (fail-closed).");
                    return IngressResult.Deny("Audit recording failed during emergency break-glass invocation. Access denied (fail-closed).", 500);
                }
            }
        }

        return IngressResult.Continue();
    }
}
