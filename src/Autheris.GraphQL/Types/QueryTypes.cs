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

        if (!TableIdentifier.TryCreate(domain, schema, name, out var tableId))
        {
            throw InvalidTableArgument();
        }

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
            ItsmTicketId = r.ItsmTicketId,
            ContextJson = r.ContextJson
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
            ItsmTicketId = r.ItsmTicketId,
            ContextJson = r.ContextJson
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
        [Service] IOptions<GovernanceTextOptions>? textOptions = null,
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

        var descriptionMax = (textOptions?.Value ?? new GovernanceTextOptions()).ConsentDescriptionMaxLength;
        var activeConsents = consents.Where(c => c.IsActive(now) && c.TenantId == tenantId).ToList();
        await Autheris.Application.Governance.ConsentDescriptionLoader.LoadAsync(consentRepository, activeConsents, ct).ConfigureAwait(false);
        var dtos = activeConsents
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
                Columns = c.ColumnRules.Select(cr => cr.ColumnName).ToList(),
                Description = ConsentDescriptionText.ForOutput(c.Description, descriptionMax)
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
        IHttpContextAccessor httpContextAccessor,
        IOptions<GatewayOptions>? options = null,
        Autheris.Application.Catalog.Interfaces.ICatalogVisibilityService? visibilityService = null,
        CancellationToken ct = default)
        => GetCatalogAsync(domain: null, first: null, after: null, search: null, tag: null, metadataRepository: repository, consentRepository: repository, approvalRepository: repository, dataOwnershipRepository: repository, httpContextAccessor: httpContextAccessor, options: options, visibilityService: visibilityService, language: null, languageNegotiator: null, localizationOptions: null, ct: ct);

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
        Autheris.Application.Catalog.Interfaces.ICatalogVisibilityService? visibilityService = null,
        CancellationToken ct = default)
        => GetCatalogAsync(domain, first, after, search, tag: null, metadataRepository, consentRepository, approvalRepository: null, dataOwnershipRepository: null, httpContextAccessor, options, visibilityService, language: null, languageNegotiator: null, localizationOptions: null, ct: ct);

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
        [Service] Autheris.Application.Catalog.Interfaces.ICatalogVisibilityService? visibilityService = null,
        string? language = null,
        [Service] Autheris.Application.Catalog.Localization.ILanguageNegotiator? languageNegotiator = null,
        [Service] IOptions<Autheris.Domain.Options.CatalogLocalizationOptions>? localizationOptions = null,
        CancellationToken ct = default)
    {
        var httpContext = httpContextAccessor?.HttpContext;
        var principal = httpContext?.User;
        var isOpenSchema = options?.Value?.IsOpenSchemaAllowed == true;

        var acceptLanguage = httpContext?.Request?.Headers.AcceptLanguage.ToString();
        var requestedLang = !string.IsNullOrWhiteSpace(language) ? language : (!string.IsNullOrWhiteSpace(acceptLanguage) ? acceptLanguage : null);
        var targetLang = languageNegotiator?.NegotiateLanguage(requestedLang, localizationOptions?.Value.SupportedLanguages, localizationOptions?.Value.DefaultLanguage);

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
            var dtos = tables.Select(t =>
            {
                var resolved = targetLang != null ? t.Table.Texts.Resolve(targetLang) : null;
                return new TableMetadataDto
                {
                    Domain = t.Identifier.Domain,
                    Schema = t.Table.SchemaName,
                    TableName = t.Table.TableName,
                    DisplayName = resolved?.Title ?? t.Table.DisplayName,
                    Sensitivity = t.Table.Sensitivity,
                    Description = resolved?.Description ?? t.Table.Description,
                    Columns = t.Columns.Select(c => c.ColumnName).ToList(),
                    Tags = t.CatalogTags,
                    AccessStatus = Autheris.Application.Services.CatalogVisibility.ComputeAccessStatus(t.Identifier, Array.Empty<Consent>(), null, isAdmin: true),
                    Language = targetLang
                };
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

        // SR-L-21: fail-closed on an invalid tenant claim (never echoes the claim value).
        TenantId tenantId;
        try
        {
            tenantId = principal.GetTenantId();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or Autheris.Domain.Exceptions.GatewaySecurityException)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("UNAUTHORIZED")
                .SetMessage("No valid tenant in the authentication token.")
                .Build());
        }

        // #138 INC-1: the one visibility service decides the row set (caller scope, consent, tier, shape, ReBAC); a missing service is empty.
        // Scope, admin view and shape come from the returned set; this channel never recomputes them (SEC-CAT138-13).
        if (visibilityService == null)
        {
            return [];
        }

        var visible = await visibilityService
            .GetVisibleAsync(new Autheris.Application.Catalog.Interfaces.CatalogCaller(principal, tenantId, Autheris.Application.Catalog.Interfaces.CatalogChannel.GraphQl), contractDef, ct)
            .ConfigureAwait(false);
        var isAdmin = visible.Scope is CallerScope.Global or CallerScope.CatalogOwner or CallerScope.Tenant;

        // SEC-CAT138-19: consents and pending requests are loaded only to compute AccessStatus; they never add or remove a row.
        IReadOnlyList<Consent> activeConsents = Array.Empty<Consent>();
        IReadOnlyList<ConsentRequest> pendingRequests = Array.Empty<ConsentRequest>();
        if (!isAdmin && visible.Tables.Count > 0)
        {
            var allSubjects = principal.GetGroupSids().Append(userSid).ToList();
            activeConsents = await consentRepository.GetAllActiveConsentsForSubjectsAsync(
                allSubjects, principal.GetUserRoles(), DateTimeOffset.UtcNow, tenantId, ct);
            if (approvalRepository != null)
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
        }

        // Second line of defence (SEC-CAT138-16): ToCatalogDtos re-applies the shaping rule with the set's admin view.
        var catalogList = ToCatalogDtos(visible.Tables, visible.IsAdminView, activeConsents, pendingRequests, isAdmin, targetLang);

        return FilterAndPaginateCatalog(catalogList, domain, search, tag, first, after);
    }

    /// <summary>The shaping loop of the catalog query: rows that do not exist for the caller are dropped before filtering and paging (F-C11).</summary>
    internal static List<TableMetadataDto> ToCatalogDtos(
        IEnumerable<TableMetadata> visibleTables, bool isAdminView, IReadOnlyList<Consent> activeConsents, IReadOnlyList<ConsentRequest> pendingRequests, bool isAdmin, string? language = null)
    {
        var catalogList = new List<TableMetadataDto>();
        foreach (var t in visibleTables)
        {
            var status = Autheris.Application.Services.CatalogVisibility.ComputeAccessStatus(t.Identifier, activeConsents, pendingRequests, isAdmin);
            var dto = ToCatalogDto(t, isAdminView, status, language);
            if (dto != null)
            {
                catalogList.Add(dto);
            }
        }

        return catalogList;
    }

    /// <summary>
    /// Issue #111 shaping of one consent/tier-filtered row (<see cref="Autheris.Application.Services.CatalogViewShaping"/>): null when the row
    /// does not exist for the caller (a non-administrator row without a recognised view), no columns for <c>Summary</c>. Not used by the
    /// explicit OpenSchema branch.
    /// </summary>
    internal static TableMetadataDto? ToCatalogDto(TableMetadata t, bool isAdminView, string status, string? language = null)
    {
        var shape = Autheris.Application.Services.CatalogViewShaping.Resolve(isAdminView, t.CatalogView);
        if (shape == Autheris.Application.Services.CatalogOutputShape.NotFound)
        {
            return null;
        }

        var resolved = !string.IsNullOrWhiteSpace(language) ? t.Table.Texts.Resolve(language) : null;

        return new TableMetadataDto
        {
            Domain = t.Identifier.Domain,
            Schema = t.Table.SchemaName,
            TableName = t.Table.TableName,
            DisplayName = resolved?.Title ?? t.Table.DisplayName,
            Sensitivity = t.Table.Sensitivity,
            Description = resolved?.Description ?? t.Table.Description,
            Columns = shape == Autheris.Application.Services.CatalogOutputShape.Summary ? [] : t.Columns.Select(c => c.ColumnName).ToList(),
            Tags = t.CatalogTags,
            AccessStatus = status,
            Language = language
        };
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

    /// <summary>#341: table arguments are caller text; a blank or malformed component is a client error (never an existence signal).</summary>
    private static GraphQLException InvalidTableArgument() => new(ErrorBuilder.New()
        .SetCode("BAD_REQUEST")
        .SetMessage("The table identifier is invalid.")
        .Build());

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

        try
        {
            return await lineageService.CalculateConsentRevocationImpactAsync(
                callerContext.Tenant,
                consentId,
                callerContext,
                ct);
        }
        catch (Exception ex) when (ex is System.Collections.Generic.KeyNotFoundException or Autheris.Domain.Exceptions.GatewayForbiddenException)
        {
            // #341: an unknown consent, a foreign tenant's consent and a missing owner/admin role answer the same fixed NOT_FOUND
            // (the contract of the consent mutations); the service message names the consent id and stays in the server log.
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("NOT_FOUND")
                .SetMessage("The consent was not found.")
                .Build());
        }
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

        if (!TableIdentifier.TryCreate(domain, schema, tableName, out var tableId))
        {
            throw InvalidTableArgument();
        }

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

        TableIdentifier? tableId = null;
        if (!string.IsNullOrWhiteSpace(domain) && !string.IsNullOrWhiteSpace(schema) && !string.IsNullOrWhiteSpace(tableName))
        {
            if (!TableIdentifier.TryCreate(domain, schema, tableName, out var parsedTableId))
            {
                throw InvalidTableArgument();
            }

            tableId = parsedTableId;
        }

        Sid? sid = !string.IsNullOrWhiteSpace(subjectSid) ? new Sid(subjectSid) : (Sid?)null;

        bool isClusterAdmin = callerContext.IsClusterAdmin;
        bool canAccessForeignReports = isClusterAdmin || callerContext.Roles.Any(r =>
            r.Equals("PrivacyAdmin", StringComparison.OrdinalIgnoreCase) ||
            r.Equals("DataProtectionOfficer", StringComparison.OrdinalIgnoreCase) ||
            r.Equals("GovernanceAdmin", StringComparison.OrdinalIgnoreCase));

        // Effective SID: If subjectSid is omitted, default to callerContext.UserSid unless caller is a privacy officer
        var effectiveSid = sid ?? (canAccessForeignReports ? (Sid?)null : callerContext.UserSid);

        if (effectiveSid.HasValue && !effectiveSid.Value.Equals(callerContext.UserSid) && !canAccessForeignReports)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode("FORBIDDEN")
                .SetMessage("GDPR access reports for other identities require PrivacyAdmin or GovernanceAdmin rights.")
                .Build());
        }

        try
        {
            return await lineageService.GetGdprDataDisclosureReportAsync(tableId, effectiveSid, timeWindowDays, callerContext, ct);
        }
        catch (Autheris.Application.Lineage.GdprSubjectScopeUnresolvedException ex)
        {
            throw new GraphQLException(ErrorBuilder.New()
                .SetCode(Autheris.Application.Lineage.GdprSubjectScopeUnresolvedException.ErrorCode)
                .SetMessage(ex.Message)
                .Build());
        }
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

        TenantId tenantId;
        if (httpContext?.Items.TryGetValue("TenantId", out var tidObj) == true && tidObj is TenantId tid)
        {
            tenantId = tid;
        }
        else
        {
            tenantId = principal.GetTenantId();
        }

        bool isGovAdmin = roles.Contains("GovernanceAdmin", StringComparer.OrdinalIgnoreCase);
        bool isClusterAdmin = ClusterAdminPolicy.IsCanonicalClusterAdmin(principal);

        return new CallerSecurityContext(
            userSid,
            groupSids,
            roles,
            tenantId,
            isGovAdmin,
            isClusterAdmin);
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
    public string? Language { get; init; }
}
