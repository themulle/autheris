namespace Autheris.Application.Policy;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.Sql.Services;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Diagnostics;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

public sealed class AccessDecisionPoint : IAccessDecisionPoint
{
    private readonly IOptions<GatewayOptions> _options;
    private readonly ITableMetadataRepository? _metadataRepo;
    private readonly IConsentResolutionService? _consentService;
    private readonly IConsentRepository? _consentRepo;
    private readonly IPolicyEnforcementService _policyEnforcementService;
    private readonly IClientIpResolver? _clientIpResolver;
    private readonly IMandatoryRowFilterResolver _mandatoryFilters;
    private readonly IRebacEvaluator? _rebacEvaluator;
    private readonly Autheris.Application.DataSources.IDataSourceTransportGate _transportGate;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;

    public AccessDecisionPoint(
        IOptions<GatewayOptions> options,
        IPolicyEnforcementService policyEnforcementService,
        IConsentResolutionService? consentService = null,
        IConsentRepository? consentRepo = null,
        IClientIpResolver? clientIpResolver = null,
        IMandatoryRowFilterResolver? mandatoryFilters = null,
        IRebacEvaluator? rebacEvaluator = null,
        ITableMetadataRepository? metadataRepo = null,
        Autheris.Application.DataSources.IDataSourceTransportGate? transportGate = null,
        TimeProvider? timeProvider = null,
        ILogger<AccessDecisionPoint>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(policyEnforcementService);
        _options = options;
        _policyEnforcementService = policyEnforcementService;
        _consentService = consentService;
        _consentRepo = consentRepo;
        _clientIpResolver = clientIpResolver;
        _mandatoryFilters = mandatoryFilters ?? NullMandatoryRowFilterResolver.Instance;
        _rebacEvaluator = rebacEvaluator;
        _metadataRepo = metadataRepo;
        _transportGate = transportGate ?? Autheris.Application.DataSources.TransportGateFallback.Current;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<AccessDecisionPoint>.Instance;
    }

    public async ValueTask<AccessDecision> DecideAsync(
        AccessRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var tableId = request.Table;
        var @namespace = tableId.Schema;
        var table = tableId.TableName;
        var tenantId = request.Tenant.Value ?? tableId.Domain;

        var tableMeta = request.TableMetadata;
        if (tableMeta == null && _metadataRepo != null)
        {
            tableMeta = await _metadataRepo.GetTableMetadataAsync(tableId, cancellationToken).ConfigureAwait(false);
        }

        if (tableMeta == null || !tableMeta.Table.IsActive)
        {
            _logger.LogWarning(
                "Iceberg table {Namespace}.{Table} not found or inactive in tenant {Tenant}; answering as denied.",
                LogSanitizer.Sanitize(@namespace),
                LogSanitizer.Sanitize(table),
                LogSanitizer.Sanitize(tenantId));
            return AccessDecision.Denied(tableId, [$"Access to table '{@namespace}.{table}' denied by policy."], request.Transport);
        }

        await _transportGate.EnsureCatalogSourceAsync(
            DataSourceAllowlistGuard.LogicalDataSource(tableMeta),
            cancellationToken).ConfigureAwait(false);

        if (_consentService == null || _consentRepo == null)
        {
            _logger.LogError(
                "Iceberg table {TableId} requested but consent services are not configured; denying (fail-closed).",
                LogSanitizer.Sanitize(tableId.ToString()));
            return AccessDecision.Denied(tableId, [$"Access to table '{@namespace}.{table}' denied by policy."], request.Transport);
        }

        var principal = request.Principal;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return AccessDecision.Denied(tableId, [$"Unauthorized access to table '{@namespace}.{table}'."], request.Transport);
        }

        if (principal.GetUserSid() is not { } userSid)
        {
            return AccessDecision.Denied(tableId, ["missing subject identifier"], request.Transport);
        }

        var groupSids = principal.GetGroupSids();
        var roles = principal.GetUserRoles();
        var allSubjects = new List<Sid>(groupSids.Count + 1);
        allSubjects.AddRange(groupSids);
        allSubjects.Add(userSid);
        var now = _timeProvider.GetUtcNow();
        var tenant = !string.IsNullOrWhiteSpace(request.Tenant.Value) ? request.Tenant : new TenantId(tenantId);
        var loaded = await _consentRepo.GetActiveConsentsForSubjectsAsync(allSubjects, tableId, now, tenant, cancellationToken).ConfigureAwait(false);

        var activeConsents = new List<Consent>(loaded.Count);
        foreach (var c in loaded)
        {
            if (c.TenantId == tenant && c.TableIdentifier == tableId && c.IsActive(now))
            {
                activeConsents.Add(c);
            }
        }

        var scopes = TokenAccessScope.GetScopeSet(principal.Claims);
        var decision = _consentService.ResolveAccess(
            userSid,
            groupSids,
            roles,
            tableId,
            activeConsents,
            tableMeta.Dialect,
            allUserSids: null,
            callerScopes: scopes.Count > 0 ? scopes : null);

        if (!decision.IsAllowed)
        {
            _logger.LogWarning(
                "Consent denied for user {User} accessing Iceberg table {TableId}",
                LogSanitizer.Sanitize(userSid.Value),
                LogSanitizer.Sanitize(tableId.ToString()));
            return AccessDecision.Denied(tableId, [$"Access to table '{@namespace}.{table}' denied by policy."], request.Transport);
        }

        var mandatory = await _mandatoryFilters.ResolveAsync(
            new MandatoryFilterQuery(userSid, groupSids, roles, tenant, tableMeta),
            cancellationToken).ConfigureAwait(false);
        if (mandatory.IsDenied || mandatory.PredicateSql != null)
        {
            _logger.LogWarning(
                "Virtual filters apply to user {User} on Iceberg table {TableId}; raw access refused",
                LogSanitizer.Sanitize(userSid.Value),
                LogSanitizer.Sanitize(tableId.ToString()));
            return AccessDecision.Denied(
                tableId,
                [$"Direct Iceberg catalog access not permitted: Table '{@namespace}.{table}' is restricted by virtual filters."],
                request.Transport);
        }

        if (RebacTableGate.IsEnforcedOnQueryPaths(_options.Value))
        {
            var rebacAllowed = await RebacTableGate.IsAllowedAsync(_rebacEvaluator, tenant, userSid, tableId, cancellationToken).ConfigureAwait(false);
            if (!rebacAllowed)
            {
                _logger.LogWarning(
                    "ReBAC denied user {User} access to Iceberg table {TableId}",
                    LogSanitizer.Sanitize(userSid.Value),
                    LogSanitizer.Sanitize(tableId.ToString()));
                return AccessDecision.Denied(tableId, [$"Access to table '{@namespace}.{table}' denied by ReBAC policy."], request.Transport);
            }
        }

        string? abacRowFilter = null;
        IReadOnlyDictionary<string, ColumnAccessLevel>? abacColumnAccess = null;
        if (_policyEnforcementService.HasPolicies(tenant))
        {
            var attributes = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var claim in principal.Claims)
            {
                if (string.Equals(claim.Type, "action", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(claim.Type, "gql.action", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                CasbinEnforcementService.AddClaimAttribute(attributes, claim.Type, claim.Value);
            }

            attributes["action"] = "read";

            var secContext = new SecurityEvaluationContext(
                UserSid: userSid,
                GroupSids: groupSids,
                Tenant: tenant,
                TargetTable: tableId,
                RequestedColumns: tableMeta.Columns.Select(c => c.ColumnName).ToList(),
                ClientIp: _clientIpResolver?.ResolveClientIp() ?? System.Net.IPAddress.None,
                Timestamp: now,
                PurposeId: ResolvePurpose(principal.Claims),
                Attributes: attributes);

            var abac = await _policyEnforcementService.EvaluatePolicyAsync(secContext, cancellationToken).ConfigureAwait(false);
            if (!abac.IsAllowed)
            {
                _logger.LogWarning(
                    "Casbin ABAC denied user {User} raw access to Iceberg table {TableId}",
                    LogSanitizer.Sanitize(userSid.Value),
                    LogSanitizer.Sanitize(tableId.ToString()));
                return AccessDecision.Denied(tableId, [$"Access to table '{@namespace}.{table}' denied by policy."], request.Transport);
            }

            abacRowFilter = abac.CombinedRowFilterSql;
            abacColumnAccess = abac.ColumnAccess;
        }

        if (request.Operation is AccessOperation.SchemaRead or AccessOperation.VendCredentials)
        {
            bool restricted = !string.IsNullOrWhiteSpace(abacRowFilter) ||
                              !string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql) ||
                              decision.ColumnAccess.Values.Any(v => v != ColumnAccessLevel.Clear) ||
                              !decision.HasUnconstrainedColumnAllow ||
                              tableMeta.Columns.Any(c => decision.GetEffectiveColumnAccess(c.ColumnName, tableMeta) != ColumnAccessLevel.Clear) ||
                              tableMeta.ColumnMaskingRules.Keys.Any(c => decision.GetEffectiveColumnAccess(c, tableMeta) != ColumnAccessLevel.Clear);
            if (restricted)
            {
                return AccessDecision.Denied(
                    tableId,
                    [$"Direct Iceberg catalog access not permitted: Table '{@namespace}.{table}' requires row-level filtering, column masking or column restrictions which cannot be enforced via raw metadata vending."],
                    request.Transport);
            }
        }

        // The ABAC row filter and masking rules apply to every operation, not only metadata vending.
        return AccessDecision.Allowed(
            tableId,
            MergeColumnAccess(decision, abacColumnAccess, tableMeta),
            request.Transport,
            RowFilterCombiner.And(decision.CombinedRowFilterSql, abacRowFilter));
    }

    /// <summary>
    /// Merges the consent decision, the catalog masking rules and the ABAC column rules; the most restrictive
    /// level per column wins (Deny, then Mask, then Clear).
    /// </summary>
    private static IReadOnlyDictionary<string, ColumnAccessLevel> MergeColumnAccess(
        TableAccessDecision decision,
        IReadOnlyDictionary<string, ColumnAccessLevel>? abacColumnAccess,
        TableMetadata tableMeta)
    {
        var merged = new Dictionary<string, ColumnAccessLevel>(decision.ColumnAccess, StringComparer.OrdinalIgnoreCase);

        foreach (var column in tableMeta.Columns)
        {
            var effective = decision.GetEffectiveColumnAccess(column.ColumnName, tableMeta);
            if (effective != ColumnAccessLevel.Clear)
            {
                Restrict(merged, column.ColumnName, effective);
            }
        }

        foreach (var name in tableMeta.ColumnMaskingRules.Keys)
        {
            var effective = decision.GetEffectiveColumnAccess(name, tableMeta);
            if (effective != ColumnAccessLevel.Clear)
            {
                Restrict(merged, name, effective);
            }
        }

        if (abacColumnAccess != null)
        {
            foreach (var (name, level) in abacColumnAccess)
            {
                if (level != ColumnAccessLevel.Clear)
                {
                    Restrict(merged, name, level);
                }
            }
        }

        return merged;
    }

    private static void Restrict(Dictionary<string, ColumnAccessLevel> merged, string column, ColumnAccessLevel level)
    {
        if (!merged.TryGetValue(column, out var existing) || level < existing)
        {
            merged[column] = level;
        }
    }

    private static string? ResolvePurpose(IEnumerable<Claim> claims)
    {
        var values = DistinctClaimValues(claims, "purpose");
        if (values.Count == 0)
        {
            values = DistinctClaimValues(claims, "purpose_id");
        }

        return values.Count == 1 ? values[0] : null;
    }

    private static List<string> DistinctClaimValues(IEnumerable<Claim> claims, string type)
    {
        var values = new List<string>(1);
        foreach (var claim in claims)
        {
            if (claim.Type == type && !string.IsNullOrWhiteSpace(claim.Value) && !values.Contains(claim.Value))
            {
                values.Add(claim.Value);
            }
        }

        return values;
    }
}
