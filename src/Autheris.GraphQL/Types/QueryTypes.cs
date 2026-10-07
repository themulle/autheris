using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using HotChocolate;
using HotChocolate.Types;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

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
        CancellationToken ct = default)
    {
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
        => GetCatalogAsync(null, null, null, null, repository, repository, httpContextAccessor, options, ct);

    public async Task<IReadOnlyList<TableMetadataDto>> GetCatalogAsync(
        string? domain = null,
        int? first = null,
        int? after = null,
        string? search = null,
        [Service] ITableMetadataRepository metadataRepository = default!,
        [Service] IConsentRepository consentRepository = default!,
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

        if (isOpenSchema)
        {
            var tables = await metadataRepository.GetAllTablesAsync(ct);
            var dtos = tables.Select(t => new TableMetadataDto
            {
                Domain = t.Identifier.Domain,
                Schema = t.Table.SchemaName,
                TableName = t.Table.TableName,
                DisplayName = t.Table.DisplayName,
                Sensitivity = t.Table.Sensitivity,
                Description = t.Table.Description,
                Columns = t.Columns.Select(c => c.ColumnName).ToList()
            });
            return FilterAndPaginateCatalog(dtos, domain, search, first, after);
        }

        if (principal?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentifizierung erforderlich für Katalogabfragen.")
                .Build());
        }

        var userSidNullable = principal.GetUserSid();
        if (userSidNullable == null)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Keine gültige Benutzer-SID im Authentifizierungstoken vorhanden.")
                .Build());
        }

        var userSid = userSidNullable.Value;
        var groupSids = principal.GetGroupSids();
        var roles = principal.GetUserRoles();

        var isGlobalAdmin = roles.Contains("GovernanceAdmin") || roles.Contains("ClusterAdmin");
        var allowDiscovery = options?.Value?.Catalog?.AllowAuthenticatedCatalogDiscovery == true;

        var allTables = await metadataRepository.GetAllTablesAsync(ct);
        if (isGlobalAdmin || allowDiscovery)
        {
            var dtos = allTables.Select(t => new TableMetadataDto
            {
                Domain = t.Identifier.Domain,
                Schema = t.Table.SchemaName,
                TableName = t.Table.TableName,
                DisplayName = t.Table.DisplayName,
                Sensitivity = t.Table.Sensitivity,
                Description = t.Table.Description,
                Columns = t.Columns.Select(c => c.ColumnName).ToList()
            });
            return FilterAndPaginateCatalog(dtos, domain, search, first, after);
        }

        // F-CONS-04: Non-admins only see tables for which they have at least one active ALLOW consent
        var tenantId = principal.GetTenantId();
        var allSubjects = groupSids.Append(userSid).ToList();
        var activeConsents = await consentRepository.GetAllActiveConsentsForSubjectsAsync(allSubjects, roles, DateTimeOffset.UtcNow, tenantId, ct);
        var allowedTableIds = activeConsents
            .Where(c => c.Effect == ConsentEffect.Allow)
            .Select(c => c.TableIdentifier)
            .ToHashSet();

        var unconditionallyDeniedTableIds = activeConsents
            .Where(c => c.Effect == ConsentEffect.Deny && c.RowFilters.Count == 0 && c.ColumnRules.Count == 0)
            .Select(c => c.TableIdentifier)
            .ToHashSet();

        var consentsByTable = activeConsents
            .GroupBy(c => c.TableIdentifier)
            .ToDictionary(g => g.Key, g => g.ToList());

        var visibleTables = allTables
            .Where(t => allowedTableIds.Contains(t.Identifier) && !unconditionallyDeniedTableIds.Contains(t.Identifier))
            .Select(t =>
            {
                var tableConsents = consentsByTable.TryGetValue(t.Identifier, out var tc) ? tc : (List<Consent>)[];
                var tableAllows = tableConsents.Where(c => c.Effect == ConsentEffect.Allow).ToList();
                var tableDenies = tableConsents.Where(c => c.Effect == ConsentEffect.Deny).ToList();

                var hasUnconstrainedAllow = tableAllows.Any(c => c.ColumnRules.Count == 0);
                var explicitlyGrantedColumns = tableAllows
                    .SelectMany(c => c.ColumnRules)
                    .Where(cr => cr.AccessLevel != ColumnAccessLevel.Deny)
                    .Select(cr => cr.ColumnName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var deniedColumns = tableDenies
                    .SelectMany(c => c.ColumnRules)
                    .Where(cr => cr.AccessLevel == ColumnAccessLevel.Deny)
                    .Select(cr => cr.ColumnName)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                var visibleColumns = t.Columns
                    .Where(c =>
                    {
                        if (deniedColumns.Contains(c.ColumnName)) return false;
                        if (hasUnconstrainedAllow) return true;
                        return explicitlyGrantedColumns.Contains(c.ColumnName);
                    })
                    .Select(c => c.ColumnName)
                    .ToList();

                return new TableMetadataDto
                {
                    Domain = t.Identifier.Domain,
                    Schema = t.Table.SchemaName,
                    TableName = t.Table.TableName,
                    DisplayName = t.Table.DisplayName,
                    Sensitivity = t.Table.Sensitivity,
                    Description = t.Table.Description,
                    Columns = visibleColumns
                };
            });

        return FilterAndPaginateCatalog(visibleTables, domain, search, first, after);
    }

    private static IReadOnlyList<TableMetadataDto> FilterAndPaginateCatalog(
        IEnumerable<TableMetadataDto> tables,
        string? domain,
        string? search,
        int? first,
        int? after)
    {
        var query = tables;

        if (!string.IsNullOrWhiteSpace(domain))
        {
            query = query.Where(t => string.Equals(t.Domain, domain, StringComparison.OrdinalIgnoreCase));
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
                .SetMessage("Authentifizierung erforderlich für Lineage- und Auswirkungsanalysen.")
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
        CancellationToken ct = default)
    {
        var callerContext = GetCallerSecurityContext(httpContextAccessor);
        if (callerContext.UserSid.Value == "S-1-5-21-ANONYMOUS" || httpContextAccessor?.HttpContext?.User?.Identity?.IsAuthenticated != true)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("Authentifizierung erforderlich für Konsumentenanalysen.")
                .Build());
        }

        var tableId = new TableIdentifier(domain, schema, tableName);
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
                .SetMessage("Authentifizierung erforderlich für DSGVO-Auskunftsberichte.")
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
                .SetMessage("DSGVO-Auskunftsberichte für fremde Identitäten erfordern PrivacyAdmin- oder GovernanceAdmin-Rechte.")
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
                .SetMessage("Authentifizierung erforderlich für Finanzabfragen.")
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
                .SetMessage("Authentifizierung erforderlich für Finanzabfragen.")
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
                .SetMessage("Authentifizierung erforderlich für Personalabfragen.")
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
}
