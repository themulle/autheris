namespace Autheris.Extensions.OData;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common;
using Autheris.Application.Interfaces;
using System.Text.RegularExpressions;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

public sealed partial class ODataHandler(
    ITableMetadataRepository metadataRepo,
    IGatewayExecutionService executionService,
    ILogger<ODataHandler> logger,
    Microsoft.Extensions.Hosting.IHostEnvironment? environment = null,
    Microsoft.Extensions.Caching.Memory.IMemoryCache? memoryCache = null,
    Autheris.Application.Catalog.ICatalogVersionProvider? versionProvider = null,
    Autheris.Application.Tenancy.ITenantResolver? tenantResolver = null) : IODataHandler
{
    private const string GenericDenied = "Access denied.";

    /// <summary>
    /// O6: Upper bound for $skip. Larger offsets make the database read and discard that many filtered rows per page;
    /// clients page further with smaller result sets or (later) keyset-based next links.
    /// </summary>
    public const int MaxSkip = 100_000;

    /// <summary>O7: Retry-After for 503/504 responses (deadlock, failover, exhausted pool, statement timeout).</summary>
    private const int UnavailableRetryAfterSeconds = 5;
    private const int TimeoutRetryAfterSeconds = 5;

    // G3 / RR-L3: detailed denial and not-found messages are only returned in Development (fail-closed when unknown).
    private readonly bool _verboseErrors = string.Equals(environment?.EnvironmentName, "Development", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex SafeIdentifierRegex();

    private readonly ITableMetadataRepository _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
    private readonly IGatewayExecutionService _executionService = executionService ?? throw new ArgumentNullException(nameof(executionService));
    private readonly ILogger<ODataHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache? _memoryCache = memoryCache;
    private readonly Autheris.Application.Catalog.ICatalogVersionProvider? _versionProvider = versionProvider;
    private readonly Autheris.Application.Tenancy.ITenantResolver? _tenantResolver = tenantResolver;

    /// <summary>
    /// DT-A-7: the default tenant is the catalog owner and may see every active table as a candidate (each still passes the table access
    /// check); every other tenant only its domain and the shared <c>default</c> domain. An unresolved tenant (no claim, no default,
    /// invalid claim, no resolver) has no candidates (fail closed).
    /// </summary>
    private List<Autheris.Domain.Model.TableMetadata> CandidateTablesFor(
        ClaimsPrincipal principal, IEnumerable<Autheris.Domain.Model.TableMetadata> allTables)
    {
        var resolution = _tenantResolver?.Resolve(principal)
            ?? Autheris.Application.Catalog.Services.CallerScopeResolver.ResolutionOf(principal, null);
        if (resolution.Tenant is not { } tenant || tenant.Value is null ||
            resolution.Outcome is Autheris.Domain.Tenancy.TenantResolutionOutcome.Invalid or Autheris.Domain.Tenancy.TenantResolutionOutcome.Unresolved)
        {
            return [];
        }

        var isOwner = _tenantResolver?.IsDefaultTenant(tenant) == true;
        return allTables
            .Where(t => t.Table.IsActive &&
                        (isOwner ||
                         string.Equals(t.Identifier.Domain, tenant.Value, StringComparison.OrdinalIgnoreCase) ||
                         string.Equals(t.Identifier.Domain, "default", StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    public async Task<string> GetMetadataCsdlAsync(ClaimsPrincipal? principal = null, CancellationToken ct = default)
    {
        if (principal?.Identity?.IsAuthenticated == true && _memoryCache != null)
        {
            var (tables, complete) = await GetAuthorizedTablesCoreAsync(principal, ct).ConfigureAwait(false);
            var tenantId = principal.GetTenantId().Value;
            var structureVersion = _versionProvider != null ? (await _versionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false)).StructureVersion : 1;
            // EXT-01: Key by column visibility and keys shape hash rather than table identifiers alone
            var shapeHash = ComputeShapeHash(tables);
            var cacheKey = $"odata:csdl:{tenantId}:s{structureVersion}:{shapeHash}";

            if (_memoryCache.TryGetValue(cacheKey, out string? cachedXml) && cachedXml != null)
            {
                return cachedXml;
            }

            var xml = ODataCsdlGenerator.GenerateMetadataXml(tables);
            if (complete)
            {
                SetCache(cacheKey, xml, xml.Length * 2L);
            }

            return xml;
        }

        var directTables = await GetAuthorizedTablesAsync(principal, ct).ConfigureAwait(false);
        return ODataCsdlGenerator.GenerateMetadataXml(directTables);
    }

    public async Task<object> GetServiceDocumentAsync(string serviceRootUrl, ClaimsPrincipal? principal = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceRootUrl);
        if (principal?.Identity?.IsAuthenticated == true && _memoryCache != null)
        {
            var (tables, complete) = await GetAuthorizedTablesCoreAsync(principal, ct).ConfigureAwait(false);
            var tenantId = principal.GetTenantId().Value;
            var structureVersion = _versionProvider != null ? (await _versionProvider.GetCatalogVersionAsync(ct).ConfigureAwait(false)).StructureVersion : 1;
            var visibilityHash = Autheris.Application.Catalog.Search.CatalogVisibleBitSet.ComputeSha256(tables.Select(t => t.Identifier));
            var cacheKey = $"odata:servicedoc:{tenantId}:s{structureVersion}:{visibilityHash}:{serviceRootUrl}";

            if (_memoryCache.TryGetValue(cacheKey, out object? cachedDoc) && cachedDoc != null)
            {
                return cachedDoc;
            }

            var doc = ODataResponseFormatter.FormatServiceDocument(serviceRootUrl, tables);
            if (complete)
            {
                SetCache(cacheKey, doc, 1024L + tables.Count * 256L + serviceRootUrl.Length * 2L);
            }

            return doc;
        }

        var directTables = await GetAuthorizedTablesAsync(principal, ct).ConfigureAwait(false);
        return ODataResponseFormatter.FormatServiceDocument(serviceRootUrl, directTables);
    }

    private async Task<IReadOnlyList<Autheris.Domain.Model.TableMetadata>> GetAuthorizedTablesAsync(ClaimsPrincipal? principal, CancellationToken ct)
        => (await GetAuthorizedTablesCoreAsync(principal, ct).ConfigureAwait(false)).Tables;

    /// <summary>
    /// The shared L1 cache has a SizeLimit, so every entry must declare a Size. Sizes are rough byte estimates.
    /// </summary>
    private void SetCache(string key, object value, long size) =>
        _memoryCache?.Set(key, value, new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60),
            Size = Math.Max(1, size)
        });

    private static long EstimateTablesSize(IReadOnlyList<Autheris.Domain.Model.TableMetadata> tables)
    {
        long size = 256;
        foreach (var t in tables)
        {
            size += 512 + t.Columns.Count * 128L;
        }

        return size;
    }

    private static string ComputeShapeHash(IReadOnlyList<Autheris.Domain.Model.TableMetadata> tables)
    {
        using var sha = System.Security.Cryptography.SHA256.Create();
        using var ms = new MemoryStream();
        using var writer = new StreamWriter(ms, new System.Text.UTF8Encoding(false));

        var sortedTables = tables.OrderBy(t => t.Identifier.ToString(), StringComparer.Ordinal).ToList();
        foreach (var t in sortedTables)
        {
            writer.Write(t.Identifier.ToString());
            writer.Write('\0');

            foreach (var col in t.Columns)
            {
                writer.Write(col.ColumnName);
                writer.Write('\0');
            }

            foreach (var pk in t.PrimaryKeyColumns)
            {
                writer.Write(pk);
                writer.Write('\0');
            }

            foreach (var idx in t.Indexes)
            {
                foreach (var idxCol in idx.Columns)
                {
                    writer.Write(idxCol);
                    writer.Write('\0');
                }
            }

            writer.Write('\n');
        }

        writer.Flush();
        ms.Position = 0;
        var hashBytes = sha.ComputeHash(ms);
        return Convert.ToHexString(hashBytes).ToLowerInvariant();
    }

    /// <summary>
    /// EXT-02: the access decision depends on the whole principal, so the key is a fingerprint of tenant, SID, group SIDs, roles and
    /// token scopes (not the SID alone). A principal without a SID is never cached (no shared "empty SID" key).
    /// </summary>
    internal static string? BuildAuthorizedTablesCacheKey(ClaimsPrincipal principal)
    {
        var sid = principal.GetUserSid()?.Value;
        if (string.IsNullOrWhiteSpace(sid))
        {
            return null;
        }

        var groups = string.Join(',', principal.GetGroupSids().Select(g => g.Value).OrderBy(x => x, StringComparer.Ordinal));
        var roles = string.Join(',', principal.GetUserRoles().OrderBy(x => x, StringComparer.Ordinal));
        var scopes = string.Join(',', Autheris.Domain.Security.TokenAccessScope.GetScopeSet(principal.Claims).OrderBy(x => x, StringComparer.Ordinal));
        var fingerprint = $"{principal.GetTenantId().Value}\n{sid}\ng:{groups}\nr:{roles}\ns:{scopes}";
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(fingerprint))).ToLowerInvariant();
        return $"odata:tables:{principal.GetTenantId().Value}:{hash}";
    }

    private async Task<(IReadOnlyList<Autheris.Domain.Model.TableMetadata> Tables, bool Complete)> GetAuthorizedTablesCoreAsync(ClaimsPrincipal? principal, CancellationToken ct)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return (Array.Empty<Autheris.Domain.Model.TableMetadata>(), true);
        }

        if (_memoryCache != null)
        {
            var cacheKey = BuildAuthorizedTablesCacheKey(principal);
            if (cacheKey != null && _memoryCache.TryGetValue(cacheKey, out IReadOnlyList<Autheris.Domain.Model.TableMetadata>? cachedTables) && cachedTables != null)
            {
                return (cachedTables, true);
            }
        }

        var complete = true;
        var allTables = await _metadataRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
        var candidateTables = CandidateTablesFor(principal, allTables);

        var authorizedTables = new List<Autheris.Domain.Model.TableMetadata>();
        foreach (var tableMeta in candidateTables)
        {
            try
            {
                TableAccessDecision? decision = await _executionService
                    .CheckTableAccessAsync(principal, tableMeta, recordAudit: false, ct).ConfigureAwait(false);

                // SR-M-16: fail closed. A null decision is treated as deny.
                if (decision == null)
                {
                    complete = false;
                    _logger.LogWarning("Access check for table {Table} returned no decision during OData metadata generation; table omitted.", tableMeta.Identifier);
                    continue;
                }

                if (!decision.IsAllowed)
                {
                    continue;
                }

                // SEC EX-05: filter columns based on caller's effective permissions (omit Deny columns).
                var allowedColumns = tableMeta.Columns
                    .Where(c => decision.GetEffectiveColumnAccess(c.ColumnName, tableMeta) != ColumnAccessLevel.Deny).ToList();

                if (allowedColumns.Count == 0 && tableMeta.Columns.Count > 0)
                {
                    continue;
                }

                // SD-4 / SEC-94-03: keys, indexes and Meta marker keys that touch a hidden column are dropped as a whole.
                var filteredMeta = Autheris.Application.Data.Services.TableKeyVisibility.Restrict(tableMeta, allowedColumns);

                authorizedTables.Add(filteredMeta);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // SR-M-16: fail closed - table is omitted and the (partial) result is not cached.
                complete = false;
                _logger.LogWarning(ex, "Failed to check access for table {Table} during OData metadata generation; table omitted.", tableMeta.Identifier);
            }
        }

        if (_memoryCache != null && complete)
        {
            var cacheKey = BuildAuthorizedTablesCacheKey(principal);
            if (cacheKey != null)
            {
                SetCache(cacheKey, (IReadOnlyList<Autheris.Domain.Model.TableMetadata>)authorizedTables, EstimateTablesSize(authorizedTables));
            }
        }

        return (authorizedTables, complete);
    }

    public Task<ODataQueryResult> ExecuteEntitySetQueryAsync(
        ClaimsPrincipal? principal,
        string serviceRootUrl,
        TableIdentifier table,
        int? top,
        int? skip,
        string? select,
        bool includeCount,
        IReadOnlyDictionary<string, string[]>? headers,
        string? orderBy = null,
        CancellationToken ct = default) =>
        ExecuteEntitySetQueryAsync(principal, serviceRootUrl, table, top, skip, select, includeCount, headers, orderBy, filter: null, ct);

    public async Task<ODataQueryResult> ExecuteEntitySetQueryAsync(
        ClaimsPrincipal? principal,
        string serviceRootUrl,
        TableIdentifier table,
        int? top,
        int? skip,
        string? select,
        bool includeCount,
        IReadOnlyDictionary<string, string[]>? headers,
        string? orderBy,
        string? filter,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceRootUrl);

        if (top.HasValue && top.Value < 0)
        {
            return new ODataQueryResult(
                Success: false,
                StatusCode: 400,
                Payload: ODataResponseFormatter.FormatErrorResponse("InvalidQueryOption", "The query parameter '$top' must be a non-negative integer."),
                ErrorCode: "InvalidQueryOption",
                ErrorMessage: "The query parameter '$top' must be a non-negative integer."
            );
        }

        if (skip.HasValue && skip.Value < 0)
        {
            return new ODataQueryResult(
                Success: false,
                StatusCode: 400,
                Payload: ODataResponseFormatter.FormatErrorResponse("InvalidQueryOption", "The query parameter '$skip' must be a non-negative integer."),
                ErrorCode: "InvalidQueryOption",
                ErrorMessage: "The query parameter '$skip' must be a non-negative integer."
            );
        }

        if (skip.HasValue && skip.Value > MaxSkip)
        {
            return Error(400, "InvalidQueryOption", $"The query parameter '$skip' must not exceed {MaxSkip}.");
        }

        // 4a.3: $orderby is a comma separated list of "property [asc|desc]".
        IReadOnlyList<TableOrderBy>? orderByColumns = null;
        if (orderBy != null)
        {
            if (!TryParseOrderBy(orderBy, out var parsedOrder, out var orderError))
            {
                return Error(400, "InvalidQueryOption", orderError);
            }

            orderByColumns = parsedOrder;
        }

        // Befund 2.1: $filter pushdown parser with Zero-Trust tracking
        TableFilterClause? filterClause = null;
        if (!string.IsNullOrWhiteSpace(filter))
        {
            try
            {
                filterClause = ODataFilterParser.Parse(filter);
            }
            catch (Autheris.Domain.Exceptions.GatewayInvalidQueryException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return Error(400, "InvalidQueryOption", $"Invalid $filter expression: {ex.Message}");
            }
        }

        // Safe limit handling: default top 100, max 1000
        // EXT-19: $top=0 is the standard count probe and returns no rows.
        var effectiveTop = top.HasValue ? Math.Clamp(top.Value, 0, 1000) : 100;
        var effectiveSkip = skip.HasValue ? Math.Max(0, skip.Value) : 0;

        IReadOnlyList<string>? requestedFields = null;
        if (select != null)
        {
            if (string.IsNullOrWhiteSpace(select))
            {
                return Error(400, "InvalidQueryOption", "The query parameter '$select' must specify at least one property.");
            }
            var fields = select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (fields.Length == 0)
            {
                return Error(400, "InvalidQueryOption", "The query parameter '$select' must specify at least one property.");
            }
            foreach (var field in fields)
            {
                if (!SafeIdentifierRegex().IsMatch(field))
                {
                    return new ODataQueryResult(
                        Success: false,
                        StatusCode: 400,
                        Payload: ODataResponseFormatter.FormatErrorResponse("InvalidQueryOption", $"The column '{field}' in '$select' contains invalid characters."),
                        ErrorCode: "InvalidQueryOption",
                        ErrorMessage: $"The column '{field}' in '$select' contains invalid characters."
                    );
                }
            }
            requestedFields = fields;
        }

        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows;
        TableAccessDecision decision;
        long? totalCount;
        var unordered = false;

        try
        {
            // O5 / 4a.3 / 2.1: $count is the total under the same row filter and $filter, counted by the data source in the same statement
            // context; a source that cannot count answers 501, never the number of rows on the page.
            var page = await _executionService.ExecuteTablePageAsync(
                principal,
                table,
                new TablePageRequest(
                    First: effectiveTop + 1,
                    After: effectiveSkip,
                    RequestedFields: requestedFields,
                    OrderBy: orderByColumns,
                    Filter: filterClause,
                    IncludeTotalCount: includeCount,
                    RequestHeaders: headers),
                ct).ConfigureAwait(false);
            (rows, decision, totalCount) = (page.Rows, page.Decision, page.TotalCount);
            unordered = page.Unordered;
            if (includeCount && totalCount == null && decision.IsAllowed)
            {
                return Error(501, "NotImplemented", "The total row count cannot be determined for this data source.");
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            // O8: the client went away; SqlClient reports the cancelled command as SqlException, not as cancellation.
            _logger.LogInformation("OData query for {Table} cancelled by the client ({ExceptionType}).", table, ex.GetType().Name);
            return Error(499, "ClientClosedRequest", "The client closed the request.");
        }
        catch (Autheris.Domain.Exceptions.GatewayInvalidQueryException iqEx)
        {
            _logger.LogWarning("OData query for {Table} rejected: {Message}", table, iqEx.Message);
            return Error(400, "InvalidQueryOption", iqEx.Message);
        }
        catch (Autheris.Domain.Exceptions.GatewayThrottledException thEx)
        {
            _logger.LogWarning("OData query for {Table} throttled (too many concurrent reads).", table);
            return Error(429, "TooManyRequests", thEx.Message, thEx.RetryAfterSeconds);
        }
        catch (Autheris.Domain.Exceptions.GatewayNotImplementedException niEx)
        {
            _logger.LogWarning("OData query for {Table} uses an unsupported option: {Message}", table, niEx.Message);
            return Error(501, "NotImplemented", niEx.Message);
        }
        catch (Autheris.Domain.Exceptions.GatewayUnsupportedColumnTypeException utEx)
        {
            _logger.LogWarning(utEx, "OData query for {Table} hit an unsupported column type.", table);
            return Error(501, "UnsupportedColumnType", utEx.Message);
        }
        catch (Autheris.Domain.Exceptions.GatewaySecurityException sizeEx) when (sizeEx.ErrorCode == "RESPONSE_TOO_LARGE")
        {
            // O11: a size limit is not an access decision; tell the client how to narrow the request.
            _logger.LogWarning("OData query for {Table} exceeded the response size limit.", table);
            return Error(400, "ResponseTooLarge", "The response exceeds the size limit. Narrow it with $select or a smaller $top.");
        }
        catch (Autheris.Domain.Exceptions.TableNotFoundException nfEx)
        {
            _logger.LogWarning("OData query for {Table} not found: {Message}", table, nfEx.Message);
            if (!_verboseErrors)
            {
                // Same answer as for a denied table: no existence oracle.
                return new ODataQueryResult(
                    Success: false,
                    StatusCode: 403,
                    Payload: ODataResponseFormatter.FormatErrorResponse("ACCESS_DENIED", GenericDenied),
                    ErrorCode: "ACCESS_DENIED",
                    ErrorMessage: GenericDenied
                );
            }

            return new ODataQueryResult(
                Success: false,
                StatusCode: 404,
                Payload: ODataResponseFormatter.FormatErrorResponse("NOT_FOUND", nfEx.Message),
                ErrorCode: "NOT_FOUND",
                ErrorMessage: nfEx.Message
            );
        }
        catch (Autheris.Domain.Exceptions.GatewayUnauthorizedException unEx)
        {
            _logger.LogWarning("OData query for {Table} unauthorized: {Message}", table, unEx.Message);
            return new ODataQueryResult(
                Success: false,
                StatusCode: 401,
                Payload: ODataResponseFormatter.FormatErrorResponse("UNAUTHORIZED", unEx.Message),
                ErrorCode: "UNAUTHORIZED",
                ErrorMessage: unEx.Message
            );
        }
        catch (Autheris.Domain.Exceptions.GatewaySecurityException secEx)
        {
            _logger.LogWarning("OData query for {Table} forbidden: {Message}", table, secEx.Message);
            var secMessage = _verboseErrors ? secEx.Message : GenericDenied;
            return new ODataQueryResult(
                Success: false,
                StatusCode: 403,
                Payload: ODataResponseFormatter.FormatErrorResponse("ACCESS_DENIED", secMessage),
                ErrorCode: "ACCESS_DENIED",
                ErrorMessage: secMessage
            );
        }
        catch (Exception ex) when (DataAccessErrorClassifier.Classify(ex) == DataAccessErrorKind.Timeout)
        {
            _logger.LogWarning(ex, "OData query for {Table} timed out.", table);
            return Error(504, "ExecutionTimeout", "The query exceeded the execution time limit. Narrow the query or check database locks.", TimeoutRetryAfterSeconds);
        }
        catch (Exception ex) when (DataAccessErrorClassifier.Classify(ex) == DataAccessErrorKind.Unavailable)
        {
            // O7: deadlock, failover, exhausted pool: retryable, and the database error text stays in the log.
            _logger.LogWarning(ex, "OData query for {Table} failed with a transient data source error.", table);
            return Error(503, "ServiceUnavailable", "The data source is temporarily unavailable. Retry later.", UnavailableRetryAfterSeconds);
        }
        catch (Exception ex) when (Autheris.Application.Sql.Errors.SqlCompilerErrorMapper.TryMap(ex, out var mapped))
        {
            _logger.LogWarning("OData query for {Table} rejected by SQL compiler or policy: {Code}", table, mapped.Code);
            Autheris.Application.Sql.Errors.SqlCompilerErrorMapper.RecordOutcome(mapped);
            return new ODataQueryResult(
                Success: false,
                StatusCode: mapped.Status,
                Payload: ODataResponseFormatter.FormatErrorResponse(mapped.Code, mapped.Title),
                ErrorCode: mapped.Code,
                ErrorMessage: mapped.Title
            );
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "OData query for {Table} failed unexpectedly.", table);
            var msg = _verboseErrors ? ex.Message : "The data access request could not be processed. Contact support.";
            return new ODataQueryResult(
                Success: false,
                StatusCode: 500,
                Payload: ODataResponseFormatter.FormatErrorResponse("INTERNAL_ERROR", msg),
                ErrorCode: "INTERNAL_ERROR",
                ErrorMessage: msg
            );
        }

        if (!decision.IsAllowed)
        {
            var reasons = decision.DeniedReasons.Count > 0 ? string.Join("; ", decision.DeniedReasons) : "Access denied by gateway governance policy.";
            _logger.LogWarning("OData query for {Table} denied: {Reasons}", table, reasons);

            var clientReasons = _verboseErrors ? reasons : GenericDenied;
            return new ODataQueryResult(
                Success: false,
                StatusCode: 403,
                Payload: ODataResponseFormatter.FormatErrorResponse("ACCESS_DENIED", clientReasons),
                ErrorCode: "ACCESS_DENIED",
                ErrorMessage: clientReasons
            );
        }

        string? nextLink = null;
        if (unordered && effectiveTop > 0 && rows.Count > effectiveTop)
        {
            // Issue #94 (D-10): without an orderable clear column the pages cannot be positioned deterministically. Refuse
            // instead of returning a page that the next one may duplicate or skip. The text names no column.
            return Error(400, "InvalidQueryOption", Autheris.Application.Data.Services.GovernedTableReadPlanner.NoOrderableColumnMessage);
        }

        if (effectiveTop == 0)
        {
            rows = [];
        }
        else if (rows.Count > effectiveTop)
        {
            nextLink = BuildNextLink(serviceRootUrl, table, effectiveSkip + effectiveTop, top, select, orderBy, filter, includeCount);
            rows = rows.Take(effectiveTop).ToList();
        }

        var payload = ODataResponseFormatter.FormatEntitySetResponse(serviceRootUrl, table, rows, totalCount, nextLink);

        return new ODataQueryResult(
            Success: true,
            StatusCode: 200,
            Payload: payload
        );
    }

    private static string BuildNextLink(string serviceRootUrl, TableIdentifier table, int nextSkip, int? top, string? select, string? orderBy, string? filter, bool includeCount)
    {
        var cleanRoot = serviceRootUrl.TrimEnd('/');
        var sb = new System.Text.StringBuilder();
        // SEC-94-14: every path segment is escaped, so a name with '/', '?' or '#' cannot alter the link structure.
        sb.Append(cleanRoot).Append('/')
          .Append(Uri.EscapeDataString(table.Domain)).Append('/')
          .Append(Uri.EscapeDataString(table.Schema)).Append('/')
          .Append(Uri.EscapeDataString(table.TableName));
        sb.Append("?$skip=").Append(nextSkip);
        if (top.HasValue)
        {
            sb.Append("&$top=").Append(top.Value);
        }
        if (!string.IsNullOrWhiteSpace(filter))
        {
            sb.Append("&$filter=").Append(Uri.EscapeDataString(filter));
        }
        if (!string.IsNullOrWhiteSpace(select))
        {
            sb.Append("&$select=").Append(Uri.EscapeDataString(select));
        }
        if (!string.IsNullOrWhiteSpace(orderBy))
        {
            sb.Append("&$orderby=").Append(Uri.EscapeDataString(orderBy));
        }
        if (includeCount)
        {
            sb.Append("&$count=true");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 4a.3: parses "$orderby=a desc, b" into columns with direction. Only simple property names (no paths,
    /// functions or expressions), each property at most once.
    /// </summary>
    internal static bool TryParseOrderBy(string orderBy, out IReadOnlyList<TableOrderBy> columns, out string error)
    {
        columns = Array.Empty<TableOrderBy>();
        error = string.Empty;
        var items = orderBy.Split(',', StringSplitOptions.TrimEntries);
        if (items.Length == 0 || items.Any(string.IsNullOrEmpty))
        {
            error = "The query parameter '$orderby' must list at least one property.";
            return false;
        }

        var parsed = new List<TableOrderBy>(items.Length);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in items)
        {
            var parts = item.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length is < 1 or > 2 || !SafeIdentifierRegex().IsMatch(parts[0]))
            {
                error = $"The expression '{Truncate(item)}' in '$orderby' is not supported; use 'property [asc|desc]'.";
                return false;
            }

            bool descending = false;
            if (parts.Length == 2)
            {
                if (string.Equals(parts[1], "desc", StringComparison.OrdinalIgnoreCase))
                {
                    descending = true;
                }
                else if (!string.Equals(parts[1], "asc", StringComparison.OrdinalIgnoreCase))
                {
                    error = $"The direction '{Truncate(parts[1])}' in '$orderby' must be 'asc' or 'desc'.";
                    return false;
                }
            }

            if (!seen.Add(parts[0]))
            {
                error = $"The property '{parts[0]}' appears more than once in '$orderby'.";
                return false;
            }

            parsed.Add(new TableOrderBy(parts[0], descending));
        }

        columns = parsed;
        return true;
    }

    private static string Truncate(string value) => value.Length > 64 ? value[..64] : value;

    private static ODataQueryResult Error(int statusCode, string errorCode, string message, int? retryAfterSeconds = null) =>
        new(
            Success: false,
            StatusCode: statusCode,
            Payload: ODataResponseFormatter.FormatErrorResponse(errorCode, message),
            ErrorCode: errorCode,
            ErrorMessage: message,
            RetryAfterSeconds: retryAfterSeconds);
}
