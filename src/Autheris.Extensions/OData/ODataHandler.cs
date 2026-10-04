namespace Autheris.Extensions.OData;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using System.Text.RegularExpressions;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Microsoft.Extensions.Logging;

public sealed partial class ODataHandler(
    ITableMetadataRepository metadataRepo,
    IGatewayExecutionService executionService,
    ILogger<ODataHandler> logger) : IODataHandler
{
    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex SafeIdentifierRegex();

    private readonly ITableMetadataRepository _metadataRepo = metadataRepo ?? throw new ArgumentNullException(nameof(metadataRepo));
    private readonly IGatewayExecutionService _executionService = executionService ?? throw new ArgumentNullException(nameof(executionService));
    private readonly ILogger<ODataHandler> _logger = logger ?? throw new ArgumentNullException(nameof(logger));

    public async Task<string> GetMetadataCsdlAsync(ClaimsPrincipal? principal = null, CancellationToken ct = default)
    {
        var tables = await GetAuthorizedTablesAsync(principal, ct).ConfigureAwait(false);
        return ODataCsdlGenerator.GenerateMetadataXml(tables);
    }

    public async Task<object> GetServiceDocumentAsync(string serviceRootUrl, ClaimsPrincipal? principal = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceRootUrl);
        var tables = await GetAuthorizedTablesAsync(principal, ct).ConfigureAwait(false);
        return ODataResponseFormatter.FormatServiceDocument(serviceRootUrl, tables);
    }

    private async Task<IReadOnlyList<Autheris.Domain.Model.TableMetadata>> GetAuthorizedTablesAsync(ClaimsPrincipal? principal, CancellationToken ct)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return Array.Empty<Autheris.Domain.Model.TableMetadata>();
        }

        var allTables = await _metadataRepo.GetAllTablesAsync(ct).ConfigureAwait(false);
        var tenant = principal.GetTenantId();
        var candidateTables = allTables
            .Where(t => tenant == TenantId.LegacySingleTenant ||
                        string.Equals(t.Identifier.Domain, tenant.Value, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(t.Identifier.Domain, "default", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var authorizedTables = new List<Autheris.Domain.Model.TableMetadata>();
        foreach (var tableMeta in candidateTables)
        {
            try
            {
                var decision = await _executionService.CheckTableAccessAsync(principal, tableMeta.Identifier, ct).ConfigureAwait(false);
                if (decision != null && !decision.IsAllowed)
                {
                    continue;
                }

                // SEC EX-05: filter columns based on caller's effective permissions (omit Deny columns).
                var allowedColumns = decision != null
                    ? tableMeta.Columns.Where(c => decision.GetEffectiveColumnAccess(c.ColumnName, tableMeta) != ColumnAccessLevel.Deny).ToList()
                    : tableMeta.Columns;

                if (allowedColumns.Count == 0 && tableMeta.Columns.Count > 0)
                {
                    continue;
                }

                var filteredMeta = new Autheris.Domain.Model.TableMetadata
                {
                    Identifier = tableMeta.Identifier,
                    Table = tableMeta.Table,
                    PrimaryKeyColumns = tableMeta.PrimaryKeyColumns,
                    Columns = allowedColumns,
                    ColumnMaskingRules = tableMeta.ColumnMaskingRules
                };

                authorizedTables.Add(filteredMeta);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to check access for table {Table} during OData metadata generation.", tableMeta.Identifier);
            }
        }

        return authorizedTables;
    }

    public async Task<ODataQueryResult> ExecuteEntitySetQueryAsync(
        ClaimsPrincipal? principal,
        string serviceRootUrl,
        TableIdentifier table,
        int? top,
        int? skip,
        string? select,
        bool includeCount,
        IReadOnlyDictionary<string, string[]>? headers,
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

        // Safe limit handling: default top 100, max 1000
        var effectiveTop = top.HasValue ? Math.Clamp(top.Value, 1, 1000) : 100;
        var effectiveSkip = skip.HasValue ? Math.Max(0, skip.Value) : 0;

        IReadOnlyList<string>? requestedFields = null;
        if (!string.IsNullOrWhiteSpace(select))
        {
            var fields = select.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
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

        try
        {
            (rows, decision) = await _executionService.ExecuteTableQueryAsync(
                principal: principal,
                table: table,
                first: effectiveTop,
                after: effectiveSkip,
                queryArguments: null,
                requestedFields: requestedFields,
                requestHeaders: headers,
                ct: ct
            ).ConfigureAwait(false);
        }
        catch (Autheris.Domain.Exceptions.TableNotFoundException nfEx)
        {
            _logger.LogWarning("OData query for {Table} not found: {Message}", table, nfEx.Message);
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
            return new ODataQueryResult(
                Success: false,
                StatusCode: 403,
                Payload: ODataResponseFormatter.FormatErrorResponse("ACCESS_DENIED", secEx.Message),
                ErrorCode: "ACCESS_DENIED",
                ErrorMessage: secEx.Message
            );
        }

        if (!decision.IsAllowed)
        {
            var reasons = decision.DeniedReasons.Count > 0 ? string.Join("; ", decision.DeniedReasons) : "Access denied by gateway governance policy.";
            _logger.LogWarning("OData query for {Table} denied: {Reasons}", table, reasons);

            return new ODataQueryResult(
                Success: false,
                StatusCode: 403,
                Payload: ODataResponseFormatter.FormatErrorResponse("ACCESS_DENIED", reasons),
                ErrorCode: "ACCESS_DENIED",
                ErrorMessage: reasons
            );
        }

        int? totalCount = includeCount ? rows.Count : null;
        var payload = ODataResponseFormatter.FormatEntitySetResponse(serviceRootUrl, table, rows, totalCount);

        return new ODataQueryResult(
            Success: true,
            StatusCode: 200,
            Payload: payload
        );
    }
}
