namespace Autheris.Application.Plugins.Auth;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autheris.Application.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options.Plugins;
using Autheris.Domain.Plugins.Auth;
using Microsoft.Extensions.Logging;

public sealed partial class AuthPluginRoleEscalationGuard
{
    private static readonly string[] DefaultProhibitedRoles = ["ClusterAdmin", "GovernanceAdmin", "SecurityAdmin"];

    private static readonly HashSet<string> ReservedSystemSids = new(StringComparer.OrdinalIgnoreCase)
    {
        "S-1-5-18",
        "S-1-5-19",
        "S-1-5-20",
        "S-1-5-32-544",
        "S-1-5-32-545"
    };

    [GeneratedRegex(@"^[a-zA-Z0-9_\-\.]{1,128}$")]
    private static partial Regex RoleNameRegex();

    [GeneratedRegex(@"^S-1-[0-9]+(-[0-9]+)+$", RegexOptions.IgnoreCase)]
    private static partial Regex StandardSidRegex();

    [GeneratedRegex(@"^[a-fA-F0-9]{8}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{4}-[a-fA-F0-9]{12}$")]
    private static partial Regex GuidRegex();

    private readonly AuthPluginOptions _options;
    private readonly HashSet<string> _prohibitedRoles;
    private readonly ILogger<AuthPluginRoleEscalationGuard>? _logger;
    private readonly IAuditWriter? _auditWriter;

    public AuthPluginRoleEscalationGuard(
        AuthPluginOptions options,
        ILogger<AuthPluginRoleEscalationGuard>? logger = null,
        IAuditWriter? auditWriter = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger;
        _auditWriter = auditWriter;

        _prohibitedRoles = new HashSet<string>(DefaultProhibitedRoles, StringComparer.OrdinalIgnoreCase);
        if (_options.ProhibitedRoles != null)
        {
            foreach (var role in _options.ProhibitedRoles)
            {
                if (!string.IsNullOrWhiteSpace(role))
                {
                    _prohibitedRoles.Add(role.Trim());
                }
            }
        }
    }

    public PluginRoleEnrichmentResult Sanitize(
        string pluginName,
        PluginRoleEnrichmentResult rawResult,
        PluginSecurityContext context)
    {
        ArgumentNullException.ThrowIfNull(rawResult);
        ArgumentNullException.ThrowIfNull(context);

        var sanitizedRoles = new List<string>();
        foreach (var role in rawResult.AddedRoles)
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                continue;
            }

            var trimmedRole = role.Trim();

            // SEC-BL08-05: Prohibited superadmin check
            if (_prohibitedRoles.Contains(trimmedRole))
            {
                _logger?.LogWarning("Auth plugin '{PluginName}' attempted to assign prohibited superadmin role '{Role}'. Stripping role (SEC-BL08-05).",
                    pluginName, trimmedRole);
                AuthPluginDiagnostics.RecordSecurityAlert(AuthPluginAuditEventTypes.RoleEscalationAttempt, pluginName);
                RecordAlertAudit(AuthPluginAuditEventTypes.RoleEscalationAttempt, pluginName, context, new { attemptedRole = trimmedRole, reason = "ProhibitedRole" });
                continue;
            }

            // SEC-BL08-05: Strict syntax regex allowlist
            if (!RoleNameRegex().IsMatch(trimmedRole))
            {
                _logger?.LogWarning("Auth plugin '{PluginName}' returned malformed role '{Role}'. Stripping role (SEC-BL08-05).",
                    pluginName, trimmedRole);
                AuthPluginDiagnostics.RecordSecurityAlert(AuthPluginAuditEventTypes.RoleEscalationAttempt, pluginName);
                RecordAlertAudit(AuthPluginAuditEventTypes.RoleEscalationAttempt, pluginName, context, new { attemptedRole = trimmedRole, reason = "InvalidSyntax" });
                continue;
            }

            sanitizedRoles.Add(trimmedRole);
        }

        var sanitizedSids = new List<Sid>();
        foreach (var sid in rawResult.AddedGroupSids)
        {
            var sidStr = sid.Value?.Trim();
            if (string.IsNullOrWhiteSpace(sidStr))
            {
                continue;
            }

            // SEC-BL08-06: Reserved system SID check
            if (ReservedSystemSids.Contains(sidStr))
            {
                _logger?.LogWarning("Auth plugin '{PluginName}' attempted to assign reserved system SID '{Sid}'. Stripping SID (SEC-BL08-06).",
                    pluginName, sidStr);
                AuthPluginDiagnostics.RecordSecurityAlert(AuthPluginAuditEventTypes.SystemSidAttempt, pluginName);
                RecordAlertAudit(AuthPluginAuditEventTypes.SystemSidAttempt, pluginName, context, new { attemptedSid = sidStr, reason = "ReservedSystemSid" });
                continue;
            }

            // SEC-BL08-06: SID structure validation
            if (!IsValidSidStructure(sidStr))
            {
                _logger?.LogWarning("Auth plugin '{PluginName}' returned malformed SID '{Sid}'. Stripping SID (SEC-BL08-06).",
                    pluginName, sidStr);
                AuthPluginDiagnostics.RecordSecurityAlert(AuthPluginAuditEventTypes.SystemSidAttempt, pluginName);
                RecordAlertAudit(AuthPluginAuditEventTypes.SystemSidAttempt, pluginName, context, new { attemptedSid = sidStr, reason = "InvalidSidStructure" });
                continue;
            }

            sanitizedSids.Add(new Sid(sidStr));
        }

        // SEC-BL08-07: Cardinality caps
        int maxRoles = Math.Clamp(_options.MaxEnrichedRolesPerCaller, 1, 256);
        if (sanitizedRoles.Count > maxRoles)
        {
            _logger?.LogWarning("Auth plugin '{PluginName}' added {Count} roles, exceeding maximum quota of {MaxRoles}. Truncating to quota (SEC-BL08-07).",
                pluginName, sanitizedRoles.Count, maxRoles);
            sanitizedRoles = sanitizedRoles.Take(maxRoles).ToList();
        }

        int maxSids = Math.Clamp(_options.MaxEnrichedGroupSidsPerCaller, 1, 1024);
        if (sanitizedSids.Count > maxSids)
        {
            _logger?.LogWarning("Auth plugin '{PluginName}' added {Count} group SIDs, exceeding maximum quota of {MaxSids}. Truncating to quota (SEC-BL08-07).",
                pluginName, sanitizedSids.Count, maxSids);
            sanitizedSids = sanitizedSids.Take(maxSids).ToList();
        }

        return new PluginRoleEnrichmentResult(sanitizedRoles, sanitizedSids);
    }

    private static bool IsValidSidStructure(string sidStr)
    {
        if (sidStr.StartsWith("S-", StringComparison.OrdinalIgnoreCase))
        {
            return StandardSidRegex().IsMatch(sidStr);
        }

        return GuidRegex().IsMatch(sidStr);
    }

    private void RecordAlertAudit(string eventType, string pluginName, PluginSecurityContext context, object details)
    {
        if (_auditWriter == null) return;

        try
        {
            var entry = new AuditLogEntry
            {
                EventType = eventType,
                ActorSid = context.UserSid,
                TenantId = context.TenantId,
                Decision = "DENY",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    plugin = pluginName,
                    details
                })
            };

            _ = _auditWriter.RecordBestEffortAsync(entry);
        }
        catch
        {
            // Best-effort alert audit logging must not interrupt pipeline
        }
    }
}
