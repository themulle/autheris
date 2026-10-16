using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Analytics;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using HotChocolate;
using HotChocolate.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Autheris.Application.Catalog.Services;

namespace Autheris.GraphQL.Types;

public sealed class TableRecordPayload
{
    public string TableName { get; init; } = string.Empty;
    public int TotalCount { get; init; }
    public IReadOnlyList<string> JsonRows { get; init; } = [];
}

public sealed class Query
{
    public async Task<TableRecordPayload> GetTableAsync(
        string domain,
        string name,
        string schema = "dbo",
        int first = 50,
        int after = 0,
        [Service] IGatewayExecutionService executionService = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        [Service] IOptions<GatewayOptions>? gatewayOptions = null,
        CancellationToken ct = default)
    {
        // SR-M-17: bound the offset like the tree path does (GraphQL.MaxAllowedOffset).
        var maxOffset = gatewayOptions?.Value?.GraphQL?.MaxAllowedOffset >= 0
            ? gatewayOptions.Value.GraphQL.MaxAllowedOffset
            : 10_000;
        if (after < 0 || after > maxOffset)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("BAD_REQUEST")
                .SetMessage($"Argument 'after' must be between 0 and {maxOffset}.")
                .Build());
        }

        var httpContext = httpContextAccessor?.HttpContext;
        var principal = httpContext?.User ?? new ClaimsPrincipal();
        IReadOnlyDictionary<string, string[]>? headers = null;
        if (httpContext?.Request?.Headers is { Count: > 0 } reqHeaders)
        {
            headers = reqHeaders.ToDictionary(h => h.Key, h => h.Value.Where(v => v != null).Select(v => v!).ToArray(), StringComparer.OrdinalIgnoreCase);
        }

        var tableId = new TableIdentifier(domain, schema, name);
        var (rows, decision) = await executionService.ExecuteTableQueryAsync(
            principal, tableId, first, after, queryArguments: null, requestedFields: null, requestHeaders: headers, ct: ct);

        PropagateCdnCacheFlags(httpContext, decision);

        var jsonList = rows.Select(r => JsonSerializer.Serialize(r)).ToList();
        return new TableRecordPayload
        {
            TableName = tableId.ToString(),
            TotalCount = jsonList.Count,
            JsonRows = jsonList
        };
    }

    [GraphQLName("myAccess")]
    public async Task<EffectiveTableAccessDto> MyAccessAsync(
        string table,
        [Service] Autheris.Application.Governance.Interfaces.IEffectiveAccessExplanationService explanationService,
        [Service] IHttpContextAccessor httpContextAccessor,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required to query effective access.")
                .Build());
        }

        if (!TableIdentifier.TryParse(table, out var tableId))
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("BAD_REQUEST")
                .SetMessage($"Invalid table identifier format: '{table}'. Expected 'domain.schema.table' or 'domain.table'.")
                .Build());
        }

        return await explanationService.ExplainMyAccessAsync(principal, tableId, ct).ConfigureAwait(false);
    }

    [GraphQLName("myAccessRequests")]
    public async Task<IReadOnlyList<ConsentRequestDto>> MyAccessRequestsAsync(
        string? status = null,
        int? first = null,
        int? after = null,
        [Service] IConsentApprovalRepository approvalRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required to view access requests.")
                .Build());
        }

        var userSid = principal.GetUserSid();
        if (userSid == null)
        {
            return Array.Empty<ConsentRequestDto>();
        }

        var requests = await approvalRepository.GetRequestsByRequesterSidAsync(userSid.Value, status, ct).ConfigureAwait(false);
        var dtos = requests.Select(r => new ConsentRequestDto
        {
            Id = r.Id,
            TableId = r.TableId,
            TableIdentifier = r.TableIdentifier.ToString(),
            RequesterSid = r.RequesterSid.Value,
            RequestedGranteeType = r.RequestedGranteeType.ToString(),
            RequestedGranteeRef = r.RequestedGranteeRef,
            BusinessJustification = r.BusinessJustification,
            Status = r.Status,
            RequestedAt = r.RequestedAt,
            RequestedValidTo = r.RequestedValidTo,
            ItsmTicketId = r.ItsmTicketId
        });

        if (after.HasValue && after.Value > 0)
        {
            dtos = dtos.Skip(after.Value);
        }
        if (first.HasValue && first.Value > 0)
        {
            dtos = dtos.Take(first.Value);
        }

        return dtos.ToList();
    }

    [GraphQLName("pendingApprovalsForMe")]
    public async Task<IReadOnlyList<ConsentRequestDto>> PendingApprovalsForMeAsync(
        int? first = null,
        int? after = null,
        [Service] IConsentApprovalRepository approvalRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required to view pending approvals.")
                .Build());
        }

        var userSid = principal.GetUserSid();
        if (userSid == null)
        {
            return Array.Empty<ConsentRequestDto>();
        }

        var requests = await approvalRepository.GetPendingRequestsForApproverAsync(userSid.Value, ct).ConfigureAwait(false);
        var dtos = requests.Select(r => new ConsentRequestDto
        {
            Id = r.Id,
            TableId = r.TableId,
            TableIdentifier = r.TableIdentifier.ToString(),
            RequesterSid = r.RequesterSid.Value,
            RequestedGranteeType = r.RequestedGranteeType.ToString(),
            RequestedGranteeRef = r.RequestedGranteeRef,
            BusinessJustification = r.BusinessJustification,
            Status = r.Status,
            RequestedAt = r.RequestedAt,
            RequestedValidTo = r.RequestedValidTo,
            ItsmTicketId = r.ItsmTicketId
        });

        if (after.HasValue && after.Value > 0)
        {
            dtos = dtos.Skip(after.Value);
        }
        if (first.HasValue && first.Value > 0)
        {
            dtos = dtos.Take(first.Value);
        }

        return dtos.ToList();
    }

    [GraphQLName("myActiveConsents")]
    public async Task<IReadOnlyList<ConsentDto>> MyActiveConsentsAsync(
        string? table = null,
        int? first = null,
        int? after = null,
        [Service] IConsentRepository consentRepository = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required to view active consents.")
                .Build());
        }

        var userSid = principal.GetUserSid();
        if (userSid == null)
        {
            return Array.Empty<ConsentDto>();
        }

        var groupSids = principal.GetGroupSids();
        var allSubjects = groupSids.Append(userSid.Value).ToList();
        var roles = principal.GetUserRoles();
        var tenantId = principal.GetTenantId();
        var now = DateTimeOffset.UtcNow;

        IReadOnlyList<Consent> consents;
        if (!string.IsNullOrWhiteSpace(table) && TableIdentifier.TryParse(table, out var tableId))
        {
            consents = await consentRepository.GetActiveConsentsForSubjectsAsync(allSubjects, tableId, now, tenantId, ct).ConfigureAwait(false);
        }
        else
        {
            consents = await consentRepository.GetAllActiveConsentsForSubjectsAsync(allSubjects, roles, now, tenantId, ct).ConfigureAwait(false);
        }

        var dtos = consents
            .Where(c => c.IsActive(now) && c.TenantId == tenantId)
            .Select(c => new ConsentDto
            {
                Id = c.Id,
                TableIdentifier = c.TableIdentifier.ToString(),
                Effect = c.Effect.ToString(),
                GranteeType = c.GranteeType.ToString(),
                GranteeSid = c.GranteeSid?.Value,
                RoleName = c.RoleName,
                ValidFrom = c.ValidFrom,
                ValidTo = c.ValidTo,
                Columns = c.ColumnRules.Select(cr => cr.ColumnName).ToList()
            });

        if (after.HasValue && after.Value > 0)
        {
            dtos = dtos.Skip(after.Value);
        }
        if (first.HasValue && first.Value > 0)
        {
            dtos = dtos.Take(first.Value);
        }

        return dtos.ToList();
    }

    internal static void PropagateCdnCacheFlags(HttpContext? httpContext, TableAccessDecision decision)
    {
        if (httpContext == null) return;
        if (!string.IsNullOrWhiteSpace(decision.CombinedRowFilterSql))
        {
            httpContext.Items["RlsApplied"] = true;
        }
        if (decision.ColumnAccess.Values.Any(v => v == ColumnAccessLevel.Mask))
        {
            httpContext.Items["MaskingApplied"] = true;
        }
    }

    [GraphQLIgnore]
    public Task<IReadOnlyList<TableMetadataDto>> GetCatalogAsync(
        [Service] IGovernanceRepository repository,
        [Service] IHttpContextAccessor httpContextAccessor,
        [Service] IOptions<GatewayOptions>? options = null,
        CancellationToken ct = default)
        => GetCatalogAsync(null, null, null, null, null, repository, repository, repository, repository, httpContextAccessor, options, ct);

    [GraphQLIgnore]
    public Task<IReadOnlyList<TableMetadataDto>> GetCatalogAsync(
        string? domain,
        int? first,
        int? after,
        string? search,
        ITableMetadataRepository metadataRepository,
        IConsentRepository consentRepository,
        IHttpContextAccessor httpContextAccessor,
        IOptions<GatewayOptions>? options = null,
        CancellationToken ct = default)
        => GetCatalogAsync(domain, first, after, search, tag: null, metadataRepository, consentRepository, approvalRepository: null, dataOwnershipRepository: null, httpContextAccessor, options, ct);

    public async Task<IReadOnlyList<TableMetadataDto>> GetCatalogAsync(
        string? domain = null,
        int? first = null,
        int? after = null,
        string? search = null,
        string? tag = null,
        [Service] ITableMetadataRepository metadataRepository = default!,
        [Service] IConsentRepository consentRepository = default!,
        [Service] IConsentApprovalRepository? approvalRepository = null,
        [Service] IDataOwnershipRepository? dataOwnershipRepository = null,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        [Service] IOptions<GatewayOptions>? options = null,
        CancellationToken ct = default)
    {
        var httpContext = httpContextAccessor?.HttpContext;
        var principal = httpContext?.User;
        var isOpenSchema = options?.Value?.IsOpenSchemaAllowed == true;

        if (string.IsNullOrWhiteSpace(domain))
        {
            if (httpContext?.Items.TryGetValue("DomainScope", out var ds) == true && ds is string scopeStr && !string.IsNullOrWhiteSpace(scopeStr))
            {
                domain = scopeStr;
            }
            else if (httpContext?.Request?.Headers.TryGetValue("X-Domain-Scope", out var headerScope) == true && !string.IsNullOrWhiteSpace(headerScope))
            {
                domain = headerScope.ToString();
            }
        }

        Autheris.Application.Governance.Contracts.SchemaContractDefinition? contractDef = null;
        if (httpContext?.Items.TryGetValue(Autheris.Application.Governance.Contracts.SchemaContractConstants.ContractItemKey, out var contractItem) == true &&
            contractItem is string contractName &&
            httpContext.RequestServices?.GetService(typeof(Autheris.Application.Governance.Contracts.ISchemaContractManager)) is Autheris.Application.Governance.Contracts.ISchemaContractManager contractMgr)
        {
            contractDef = contractMgr.GetContract(contractName);
        }

        if (isOpenSchema)
        {
            var tables = await metadataRepository.GetAllTablesAsync(ct);
            if (contractDef != null)
            {
                tables = Autheris.Application.Services.CatalogVisibility.FilterByContract(tables, contractDef);
            }
            var dtos = tables.Select(t => new TableMetadataDto
            {
                Domain = t.Identifier.Domain,
                Schema = t.Table.SchemaName,
                TableName = t.Table.TableName,
                DisplayName = t.Table.DisplayName,
                Sensitivity = t.Table.Sensitivity,
                Description = t.Table.Description,
                Columns = t.Columns.Select(c => c.ColumnName).ToList(),
                Tags = t.Table.Tags ?? Array.Empty<string>(),
                AccessStatus = Autheris.Application.Services.CatalogVisibility.ComputeAccessStatus(t.Identifier, Array.Empty<Consent>(), null, isAdmin: true)
            });
            return FilterAndPaginateCatalog(dtos, domain, search, tag, first, after);
        }

        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required for catalog queries.")
                .Build());
        }

        var userSidNullable = principal.GetUserSid();
        if (userSidNullable == null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("No valid user SID in the authentication token.")
                .Build());
        }

        var userSid = userSidNullable.Value;
        var groupSids = principal.GetGroupSids();
        var roles = principal.GetUserRoles();

        // SR-L-21: fail-closed on an invalid tenant claim (never echoes the claim value).
        TenantId tenantId;
        try
        {
            tenantId = principal.GetTenantId();
        }
        catch (System.Security.SecurityException)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("No valid tenant in the authentication token.")
                .Build());
        }

        // Caller class from the one canonical resolver: only the canonical ClusterAdmin keeps the global view; a GovernanceAdmin is
        // tenant-scoped (in Gateway:Tenancy:Mode=Single without tenant claim it is unrestricted; a claim-less principal in Multi mode is no
        // administrator). AllowAuthenticatedCatalogDiscovery no longer widens anything: every non-administrator takes the consent path.
        var callerScope = CallerScopeResolver.Resolve(
            principal, options?.Value?.Tenancy?.EffectiveMode ?? TenancyMode.Multi);
        var isClusterAdmin = callerScope == CallerScope.Global;
        var isGovernanceAdmin = callerScope is CallerScope.Unrestricted or CallerScope.Tenant;
        var isAdmin = isClusterAdmin || isGovernanceAdmin;

        var allTables = await metadataRepository.GetAllTablesAsync(ct);
        if (contractDef != null)
        {
            allTables = Autheris.Application.Services.CatalogVisibility.FilterByContract(allTables, contractDef);
        }

        // F-CONS-04: Non-admins only see tables for which they have at least one active ALLOW consent (or visibility SUMMARY/FULL)
        var allSubjects = groupSids.Append(userSid).ToList();
        IReadOnlyList<Consent> activeConsents = isAdmin
            ? Array.Empty<Consent>()
            : await consentRepository.GetAllActiveConsentsForSubjectsAsync(allSubjects, roles, DateTimeOffset.UtcNow, tenantId, ct);

        IReadOnlyList<ConsentRequest> pendingRequests = Array.Empty<ConsentRequest>();
        if (!isAdmin && approvalRepository != null)
        {
            try
            {
                pendingRequests = await approvalRepository.GetRequestsByRequesterSidAsync(userSid, "PENDING", ct).ConfigureAwait(false);
            }
            catch
            {
                // Fail-safe
            }
        }

        IReadOnlyList<TableMetadata> visibleTables;
        if (isClusterAdmin)
        {
            visibleTables = allTables;
        }
        else if (isGovernanceAdmin)
        {
            // SR-L-21: the tenant-scoped administrator view is confined to the caller's tenant (consent-derived ownership + shared tables).
            var tenantConsentTables = await consentRepository.GetTablesWithConsentsForTenantAsync(tenantId, ct);
            visibleTables = Autheris.Application.Services.CatalogVisibility.FilterToTenant(
                allTables, tenantId, tenantConsentTables, options?.Value?.Catalog?.SharedTables);
        }
        else
        {
            visibleTables = Autheris.Application.Services.CatalogVisibility.FilterForSubject(
                allTables, activeConsents, tenantId, Autheris.Domain.Security.TokenAccessScope.GetScopeSet(principal.Claims));
        }

        var catalogList = new List<TableMetadataDto>();
        foreach (var t in visibleTables)
        {
            var status = Autheris.Application.Services.CatalogVisibility.ComputeAccessStatus(t.Identifier, activeConsents, pendingRequests, isAdmin);
            catalogList.Add(new TableMetadataDto
            {
                Domain = t.Identifier.Domain,
                Schema = t.Table.SchemaName,
                TableName = t.Table.TableName,
                DisplayName = t.Table.DisplayName,
                Sensitivity = t.Table.Sensitivity,
                Description = t.Table.Description,
                Columns = t.Columns.Select(c => c.ColumnName).ToList(),
                Tags = t.Table.Tags ?? Array.Empty<string>(),
                AccessStatus = status
            });
        }

        return FilterAndPaginateCatalog(catalogList, domain, search, tag, first, after);
    }

    private static IReadOnlyList<TableMetadataDto> FilterAndPaginateCatalog(
        IEnumerable<TableMetadataDto> tables,
        string? domain,
        string? search,
        int? first,
        int? after) => FilterAndPaginateCatalog(tables, domain, search, null, first, after);

    private static IReadOnlyList<TableMetadataDto> FilterAndPaginateCatalog(
        IEnumerable<TableMetadataDto> tables,
        string? domain,
        string? search,
        string? tag,
        int? first,
        int? after)
    {
        var query = tables;

        if (!string.IsNullOrWhiteSpace(domain))
        {
            query = query.Where(t => string.Equals(t.Domain, domain, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            query = query.Where(t => t.Tags.Any(tg => string.Equals(tg, tag.Trim(), StringComparison.OrdinalIgnoreCase)));
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            query = query.Where(t =>
                t.TableName.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                (t.DisplayName != null && t.DisplayName.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                (t.Description != null && t.Description.Contains(search, StringComparison.OrdinalIgnoreCase)) ||
                t.Columns.Any(c => c.Contains(search, StringComparison.OrdinalIgnoreCase)));
        }

        if (after.HasValue && after.Value > 0)
        {
            query = query.Skip(after.Value);
        }

        if (first.HasValue && first.Value > 0)
        {
            query = query.Take(first.Value);
        }

        return query.ToList();
    }

    public async Task<ConsentRevocationImpactReport> CalculateConsentRevocationImpactAsync(
        Guid consentId,
        [Service] ILineageImpactAnalyzerService lineageService,
        [Service] IHttpContextAccessor httpContextAccessor,
        CancellationToken ct = default)
    {
        var callerContext = GetCallerSecurityContext(httpContextAccessor);
        if (callerContext.UserSid.Value == "S-1-5-21-ANONYMOUS" || httpContextAccessor?.HttpContext?.User?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required for lineage and impact analyses.")
                .Build());
        }

        return await lineageService.CalculateConsentRevocationImpactAsync(
            callerContext.Tenant,
            consentId,
            callerContext,
            ct);
    }

    public async Task<TableConsumersReport> GetTableConsumersAsync(
        string domain,
        string schema,
        string tableName,
        int timeWindowDays = 30,
        [Service] ILineageImpactAnalyzerService lineageService = null!,
        [Service] IHttpContextAccessor httpContextAccessor = null!,
        [Service] IConsentRepository? consentRepository = null,
        [Service] IDataOwnershipRepository? ownershipRepository = null,
        CancellationToken ct = default)
    {
        var callerContext = GetCallerSecurityContext(httpContextAccessor);
        if (callerContext.UserSid.Value == "S-1-5-21-ANONYMOUS" || httpContextAccessor?.HttpContext?.User?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required for consumer analyses.")
                .Build());
        }

        var tableId = new TableIdentifier(domain, schema, tableName);

        // GQL-6: Consent and Tenant Authorization Filter
        bool isPrivilegedAdmin = callerContext.IsClusterAdmin || callerContext.IsGovernanceAdmin;
        if (!isPrivilegedAdmin)
        {
            bool isTableApprover = ownershipRepository != null &&
                await ownershipRepository.IsAuthorizedApproverForTableAsync(tableId, callerContext.UserSid, ct).ConfigureAwait(false);

            if (!isTableApprover)
            {
                if (consentRepository == null)
                {
                    throw new GraphQLException(ErrorBuilder.New()
                        .SetCode("FORBIDDEN")
                        .SetMessage($"Access denied to consumer analyses for table '{domain}.{schema}.{tableName}'.")
                        .Build());
                }

                var allSubjects = callerContext.GroupSids.Append(callerContext.UserSid).ToList();
                var activeConsents = await consentRepository.GetActiveConsentsForSubjectsAsync(
                    allSubjects, tableId, DateTimeOffset.UtcNow, callerContext.Tenant, ct).ConfigureAwait(false);

                bool hasActiveAllow = activeConsents.Any(c => c.Effect == ConsentEffect.Allow);
                bool hasUnconditionalDeny = activeConsents.Any(c => c.Effect == ConsentEffect.Deny && c.RowFilters.Count == 0 && c.ColumnRules.Count == 0);

                if (!hasActiveAllow || hasUnconditionalDeny)
                {
                    throw new GraphQLException(ErrorBuilder.New()
                        .SetCode("FORBIDDEN")
                        .SetMessage($"Access denied to consumer analyses for table '{domain}.{schema}.{tableName}'.")
                        .Build());
                }
            }
        }

        return await lineageService.GetTableConsumersAsync(tableId, timeWindowDays, callerContext, ct);
    }

    public async Task<GdprDisclosureReport> GetGdprDataDisclosureReportAsync(
        string? domain = null,
        string? schema = null,
        string? tableName = null,
        string? subjectSid = null,
        int timeWindowDays = 365,
        [Service] ILineageImpactAnalyzerService lineageService = null!,
        [Service] IHttpContextAccessor httpContextAccessor = null!,
        CancellationToken ct = default)
    {
        var callerContext = GetCallerSecurityContext(httpContextAccessor);
        if (callerContext.UserSid.Value == "S-1-5-21-ANONYMOUS" || httpContextAccessor?.HttpContext?.User?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required for GDPR access reports.")
                .Build());
        }

        TableIdentifier? tableId = !string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(schema) && !string.IsNullOrWhiteSpace(tableName)
            ? new TableIdentifier(domain, schema, tableName)
            : null;
        Sid? sid = !string.IsNullOrWhiteSpace(subjectSid) ? new Sid(subjectSid) : (Sid?)null;

        bool canAccessForeignReports = callerContext.Roles.Any(r =>
            r.Equals("PrivacyAdmin", StringComparison.OrdinalIgnoreCase) ||
            r.Equals("DataProtectionOfficer", StringComparison.OrdinalIgnoreCase) ||
            r.Equals("GovernanceAdmin", StringComparison.OrdinalIgnoreCase) ||
            r.Equals("ClusterAdmin", StringComparison.OrdinalIgnoreCase));

        // Effective SID: If subjectSid is omitted, default to callerContext.UserSid unless caller is a privacy officer
        var effectiveSid = sid ?? (canAccessForeignReports ? (Sid?)null : callerContext.UserSid);

        if (effectiveSid.HasValue && !effectiveSid.Value.Equals(callerContext.UserSid) && !canAccessForeignReports)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("GDPR access reports for other identities require PrivacyAdmin or GovernanceAdmin rights.")
                .Build());
        }

        return await lineageService.GetGdprDataDisclosureReportAsync(tableId, effectiveSid, timeWindowDays, callerContext, ct);
    }

    public async Task<string> ExportGdprDataDisclosureReportPdfBase64Async(
        string? domain = null,
        string? schema = null,
        string? tableName = null,
        string? subjectSid = null,
        int timeWindowDays = 365,
        [Service] ILineageImpactAnalyzerService lineageService = null!,
        [Service] IGdprAuditReportExporter pdfExporter = null!,
        [Service] IHttpContextAccessor httpContextAccessor = null!,
        CancellationToken ct = default)
    {
        var report = await GetGdprDataDisclosureReportAsync(domain, schema, tableName, subjectSid, timeWindowDays, lineageService, httpContextAccessor, ct);
        var exportResult = pdfExporter.ExportReportToPdf(report);
        return Convert.ToBase64String(exportResult.DocumentBytes);
    }


    private static CallerSecurityContext GetCallerSecurityContext(IHttpContextAccessor httpContextAccessor)
    {
        var httpContext = httpContextAccessor?.HttpContext;
        var principal = httpContext?.User ?? new ClaimsPrincipal();
        var userSid = principal.GetUserSid() ?? new Sid("S-1-5-21-ANONYMOUS");
        var groupSids = principal.GetGroupSids().ToList();
        var roles = principal.GetUserRoles().ToList();

        var tenantId = TenantId.LegacySingleTenant;
        if (httpContext?.Items.TryGetValue("TenantId", out var tidObj) == true && tidObj is TenantId tid)
        {
            tenantId = tid;
        }
        else
        {
            tenantId = principal.GetTenantId();
        }

        bool isGovAdmin = roles.Contains("GovernanceAdmin", StringComparer.OrdinalIgnoreCase);
        bool isClusterAdmin = roles.Contains("ClusterAdmin", StringComparer.OrdinalIgnoreCase);

        return new CallerSecurityContext(
            userSid,
            groupSids,
            roles,
            tenantId,
            isGovAdmin,
            isClusterAdmin);
    }

    [GraphQLName("queryMetrics")]
    public Task<MetricQueryResult> QueryMetricsAsync(
        IReadOnlyList<string> metrics,
        IReadOnlyList<string>? groupBy = null,
        string? where = null,
        string? timeGranularity = null,
        IReadOnlyList<string>? orderBy = null,
        int? limit = null,
        [Service] ISemanticMetricQueryService metricQueryService = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default) =>
        AnalyticsQueryExtensions.ExecuteQueryMetricsAsync(
            metrics, groupBy, where, timeGranularity, orderBy, limit, metricQueryService, httpContextAccessor, ct);
}

[ExtendObjectType(typeof(Query))]
public sealed class AnalyticsQueryExtensions
{
    [GraphQLName("queryMetrics")]
    public Task<MetricQueryResult> QueryMetricsAsync(
        IReadOnlyList<string> metrics,
        IReadOnlyList<string>? groupBy = null,
        string? where = null,
        string? timeGranularity = null,
        IReadOnlyList<string>? orderBy = null,
        int? limit = null,
        [Service] ISemanticMetricQueryService metricQueryService = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default) =>
        ExecuteQueryMetricsAsync(metrics, groupBy, where, timeGranularity, orderBy, limit, metricQueryService, httpContextAccessor, ct);

    internal static async Task<MetricQueryResult> ExecuteQueryMetricsAsync(
        IReadOnlyList<string> metrics,
        IReadOnlyList<string>? groupBy,
        string? where,
        string? timeGranularity,
        IReadOnlyList<string>? orderBy,
        int? limit,
        ISemanticMetricQueryService metricQueryService,
        IHttpContextAccessor httpContextAccessor,
        CancellationToken ct)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required for semantic metric queries.")
                .Build());
        }

        if (metrics is not { Count: > 0 })
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("INVALID_METRIC_QUERY")
                .SetMessage("At least one metric name must be specified.")
                .Build());
        }

        TableFilterClause? whereClause = !string.IsNullOrWhiteSpace(where)
            ? TableFilterClause.FromTrinoText(where, Array.Empty<string>())
            : null;

        List<TableOrderBy>? parsedOrder = null;
        if (orderBy is { Count: > 0 })
        {
            parsedOrder = new List<TableOrderBy>(orderBy.Count);
            foreach (var entry in orderBy)
            {
                if (string.IsNullOrWhiteSpace(entry)) continue;
                var parts = entry.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
                bool desc = parts.Length > 1 && string.Equals(parts[1], "DESC", StringComparison.OrdinalIgnoreCase);
                parsedOrder.Add(new TableOrderBy(parts[0], desc));
            }
        }

        try
        {
            var req = new MetricQueryRequest(metrics, groupBy, whereClause, timeGranularity, parsedOrder, limit);
            return await metricQueryService.QueryMetricsAsync(principal, req, ct).ConfigureAwait(false);
        }
        catch (PrivacyBudgetExhaustedException)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("PRIVACY_BUDGET_EXHAUSTED")
                .SetMessage("The daily differential privacy epsilon budget has been exhausted.")
                .Build());
        }
        catch (MaskedAggregationException ex)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode(MaskedAggregationException.ErrorCodeConst)
                .SetMessage(ex.Message)
                .Build());
        }
        catch (AggregationColumnUnavailableException)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode(AggregationColumnUnavailableException.ErrorCodeConst)
                .SetMessage(AggregationColumnUnavailableException.FixedMessage)
                .Build());
        }
        catch (AggregationRowCapExceededException ex)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode(AggregationRowCapExceededException.ErrorCodeConst)
                .SetMessage(ex.Message)
                .Build());
        }
        catch (GatewayInvalidQueryException ex)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode(ex.ErrorCode)
                .SetMessage(ex.Message)
                .Build());
        }
        catch (GatewaySecurityException ex)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode(ex.ErrorCode)
                .SetMessage("Access to the requested semantic metric was denied by governance policy.")
                .Build());
        }
    }
}

/// <summary>Demo root fields (<c>finance</c>, <c>hr</c>); registered only when demo data is enabled (<see cref="IDemoDataSwitch"/>).</summary>
[ExtendObjectType(typeof(Query))]
public sealed class DemoQueryExtensions
{
    public FinanceQuery GetFinance() => new();
    public HrQuery GetHr() => new();
}

public sealed class FinanceQuery
{
    public async Task<TableRecordPayload> GetInvoicesAsync(
        int first = 50,
        int after = 0,
        [Service] IGatewayExecutionService executionService = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required for financial queries.")
                .Build());
        }

        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var (rows, decision) = await executionService.ExecuteTableQueryAsync(principal, tableId, first, after, ct);
        Query.PropagateCdnCacheFlags(httpContextAccessor?.HttpContext, decision);

        var jsonList = rows.Select(r => JsonSerializer.Serialize(r)).ToList();
        return new TableRecordPayload
        {
            TableName = tableId.ToString(),
            TotalCount = jsonList.Count,
            JsonRows = jsonList
        };
    }

    public async Task<IReadOnlyList<InvoiceRecord>> GetInvoicesWithItemsAsync(
        int first = 10,
        [Service] IGatewayExecutionService executionService = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required for financial queries.")
                .Build());
        }

        var parentTableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var (rows, decision) = await executionService.ExecuteTableQueryAsync(principal, parentTableId, first, 0, ct);
        Query.PropagateCdnCacheFlags(httpContextAccessor?.HttpContext, decision);

        List<InvoiceRecord> invoices = [];
        foreach (var r in rows)
        {
            invoices.Add(new InvoiceRecord
            {
                Id = r.TryGetValue("id", out var id) && id != null ? id.ToString()! : Guid.NewGuid().ToString(),
                Amount = r.TryGetValue("amount", out var amt) && amt is decimal d ? d : 1500.00m,
                Vendor = r.TryGetValue("name", out var n) && n != null ? n.ToString()! : "Vendor Alpha",
                Email = r.TryGetValue("email", out var em) ? em?.ToString() : null
            });
        }
        return invoices;
    }
}

public sealed class HrQuery
{
    public async Task<TableRecordPayload> GetEmployeesAsync(
        int first = 50,
        int after = 0,
        [Service] IGatewayExecutionService executionService = default!,
        [Service] IHttpContextAccessor httpContextAccessor = default!,
        CancellationToken ct = default)
    {
        var principal = httpContextAccessor?.HttpContext?.User;
        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentication is required for HR queries.")
                .Build());
        }

        var tableId = new TableIdentifier("hr", "dbo", "hr_table_1");
        var (rows, decision) = await executionService.ExecuteTableQueryAsync(principal, tableId, first, after, ct);
        Query.PropagateCdnCacheFlags(httpContextAccessor?.HttpContext, decision);

        var jsonList = rows.Select(r => JsonSerializer.Serialize(r)).ToList();
        return new TableRecordPayload
        {
            TableName = tableId.ToString(),
            TotalCount = jsonList.Count,
            JsonRows = jsonList
        };
    }
}

public sealed class TableMetadataDto
{
    public string Domain { get; init; } = string.Empty;
    public string Schema { get; init; } = string.Empty;
    public string TableName { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Sensitivity { get; init; } = string.Empty;
    public string? Description { get; init; }
    public IReadOnlyList<string> Columns { get; init; } = [];
    public IReadOnlyList<string> Tags { get; init; } = [];
    public string? AccessStatus { get; init; }
    public string? DataOwner { get; init; }
    public IReadOnlyList<string>? Delegates { get; init; }
}
