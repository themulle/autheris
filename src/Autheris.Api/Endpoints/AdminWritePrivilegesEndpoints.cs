namespace Autheris.Api.Endpoints;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Errors;
using Autheris.Api.Extensions;
using Autheris.Api.Security;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.DataSources;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;

/// <summary>
/// Admin report computing the least-privilege database <c>GRANT</c> statements required by the configured write connection
/// for active write grants on a data source. Never exposes connection strings or credentials.
/// </summary>
public static class AdminWritePrivilegesEndpoints
{
    private const string DefaultPrincipalPlaceholder = "<autheris_write_role>";

    public static IEndpointRouteBuilder MapAdminWritePrivilegesEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet(
                "/api/v1/admin/datasources/{name}/required-write-privileges",
                (
                    string name,
                    HttpContext httpContext,
                    IOptions<GatewayOptions> options,
                    ITableMetadataRepository tableRepository,
                    IConsentRepository consentRepository,
                    TimeProvider? timeProvider,
                    CancellationToken ct) =>
                    GetAsync(name, httpContext, options, tableRepository, consentRepository, timeProvider, ct))
            .RequireAuthorization(GatewayPolicies.GovernanceAdmin)
            .WithIngressTransport(DataSourceTransportFlags.Rest)
            .WithAudit(AuditLevel.Summarized, AuditEventTypes.CatalogRead);

        return app;
    }

    internal static async Task<IResult> GetAsync(
        string name,
        HttpContext httpContext,
        IOptions<GatewayOptions> options,
        ITableMetadataRepository tableRepository,
        IConsentRepository consentRepository,
        TimeProvider? timeProvider = null,
        CancellationToken ct = default)
    {
        if (!GatewayPolicies.HasRole(httpContext.User, GatewayRole.GovernanceAdmin))
        {
            return ApiProblems.Problem(
                StatusCodes.Status403Forbidden,
                ApiErrorCodes.AccessDenied,
                "GovernanceAdmin role is required.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return ApiProblems.Problem(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.BadRequest,
                "A data source name is required.");
        }

        var gatewayOptions = options.Value;
        var connections = gatewayOptions.DataSources?.Connections;
        if (connections is null ||
            !TryFindConnection(connections, name, out string matchedName, out DataSourceConnectionOptions? connOptions) ||
            connOptions is null)
        {
            return ApiProblems.Problem(
                StatusCodes.Status404NotFound,
                ApiErrorCodes.ResourceNotFound,
                "The requested data source was not found.");
        }

        if (!DataSourceProvider.TryResolveDialect(connOptions.Provider, out DatabaseDialect dialect))
        {
            return ApiProblems.Problem(
                StatusCodes.Status400BadRequest,
                ApiErrorCodes.BadRequest,
                "The data source dialect is not recognized.");
        }

        TenantId tenantId = EndpointSecurity.GetSecurityContext(httpContext)?.TenantId
                            ?? EndpointSecurity.GetRequestTenant(httpContext);
        DateTimeOffset now = (timeProvider ?? TimeProvider.System).GetUtcNow();

        IReadOnlyList<TableMetadata> allTables = await tableRepository.GetAllTablesAsync(ct).ConfigureAwait(false);
        var metadataByTable = allTables.ToDictionary(t => t.Identifier);

        var activeWriteConsents = await LoadActiveWriteConsentsForDataSourceAsync(
            consentRepository,
            tenantId,
            matchedName,
            gatewayOptions.WebSql,
            connections,
            now,
            ct).ConfigureAwait(false);

        var report = BuildPrivilegesReport(matchedName, dialect, tenantId, activeWriteConsents, metadataByTable);
        return Results.Ok(report);
    }

    private static async Task<List<Consent>> LoadActiveWriteConsentsForDataSourceAsync(
        IConsentRepository consentRepository,
        TenantId tenantId,
        string dataSourceName,
        WebSqlOptions webSqlOptions,
        IReadOnlyDictionary<string, DataSourceConnectionOptions> connections,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var result = new List<Consent>();
        Guid? after = null;

        while (true)
        {
            ConsentPage page = await consentRepository.GetConsentPageAsync(
                new ConsentPageQuery(tenantId, IncludeInactive: false, AfterId: after, PageSize: ConsentPageQuery.MaxPageSize),
                ct).ConfigureAwait(false);

            foreach (Consent consent in page.Items)
            {
                if (!consent.IsActive(now) ||
                    consent.Effect != ConsentEffect.Allow ||
                    consent.WriteRules is not { Count: > 0 })
                {
                    continue;
                }

                if (MatchesDataSource(consent.TableIdentifier, dataSourceName, webSqlOptions, connections))
                {
                    result.Add(consent);
                }
            }

            if (page.NextAfterId is null)
            {
                break;
            }

            after = page.NextAfterId;
        }

        return result;
    }

    private static bool TryFindConnection(
        IReadOnlyDictionary<string, DataSourceConnectionOptions> connections,
        string requestedName,
        out string matchedName,
        out DataSourceConnectionOptions? connOptions)
    {
        foreach (var kvp in connections)
        {
            if (string.Equals(kvp.Key, requestedName, StringComparison.OrdinalIgnoreCase))
            {
                matchedName = kvp.Key;
                connOptions = kvp.Value;
                return true;
            }
        }

        matchedName = string.Empty;
        connOptions = null;
        return false;
    }

    private static bool MatchesDataSource(
        TableIdentifier table,
        string targetDataSourceName,
        WebSqlOptions webSql,
        IReadOnlyDictionary<string, DataSourceConnectionOptions> connections)
    {
        if (webSql.DataSourceMappings is { Count: > 0 } &&
            webSql.DataSourceMappings.TryGetValue(table.Domain, out string? mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
        {
            return string.Equals(mapped, targetDataSourceName, StringComparison.OrdinalIgnoreCase);
        }

        if (string.Equals(table.Domain, targetDataSourceName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        bool domainHasDedicatedConnection = connections.Keys.Any(k => string.Equals(k, table.Domain, StringComparison.OrdinalIgnoreCase));
        if (!domainHasDedicatedConnection &&
            string.Equals(targetDataSourceName, webSql.DefaultDataSourceName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private sealed class TableWriteAccumulator
    {
        public TableWriteAccumulator(TableIdentifier table) => Table = table;

        public TableIdentifier Table { get; }
        public SortedSet<WriteOperation> Operations { get; } = [];
        public SortedSet<string> InsertColumns { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedSet<string> UpdateColumns { get; } = new(StringComparer.OrdinalIgnoreCase);
        public SortedSet<string> SelectColumns { get; } = new(StringComparer.OrdinalIgnoreCase);
        public bool RequiresDelete { get; set; }
    }

    private static object BuildPrivilegesReport(
        string dataSourceName,
        DatabaseDialect dialect,
        TenantId tenantId,
        List<Consent> consents,
        IReadOnlyDictionary<TableIdentifier, TableMetadata> metadataByTable)
    {
        var byTable = new Dictionary<TableIdentifier, TableWriteAccumulator>();

        foreach (Consent consent in consents)
        {
            if (!byTable.TryGetValue(consent.TableIdentifier, out var acc))
            {
                acc = new TableWriteAccumulator(consent.TableIdentifier);
                byTable[consent.TableIdentifier] = acc;
            }

            metadataByTable.TryGetValue(consent.TableIdentifier, out var metadata);
            string? tenantColumn = metadata?.TenantColumnName;

            foreach (WriteRule rule in consent.WriteRules)
            {
                acc.Operations.Add(rule.Operation);
                switch (rule.Operation)
                {
                    case WriteOperation.Insert:
                        foreach (string col in rule.Columns)
                        {
                            acc.InsertColumns.Add(col);
                        }
                        if (!string.IsNullOrWhiteSpace(tenantColumn))
                        {
                            acc.InsertColumns.Add(tenantColumn);
                        }
                        break;

                    case WriteOperation.Update:
                        foreach (string col in rule.Columns)
                        {
                            acc.UpdateColumns.Add(col);
                        }
                        AddWhereColumns(acc.SelectColumns, tenantColumn, metadata, rule, consent);
                        break;

                    case WriteOperation.Delete:
                        acc.RequiresDelete = true;
                        AddWhereColumns(acc.SelectColumns, tenantColumn, metadata, rule, consent);
                        break;
                }
            }
        }

        var orderedTables = byTable.Values
            .OrderBy(t => t.Table.Schema, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.Table.TableName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var tableEntries = new List<object>(orderedTables.Count);
        var statements = new List<string>();
        var notes = new List<string>();

        if (dialect == DatabaseDialect.Sqlite)
        {
            notes.Add("SQLite enforces access via OS file permissions rather than per-principal SQL GRANT statements.");
        }

        foreach (TableWriteAccumulator acc in orderedTables)
        {
            var tableGrants = GenerateGrantStatements(acc, dialect);
            statements.AddRange(tableGrants);

            tableEntries.Add(new
            {
                schema = acc.Table.Schema,
                table = acc.Table.TableName,
                operations = acc.Operations.Select(o => o.ToString()).ToArray(),
                insertColumns = acc.InsertColumns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToArray(),
                updateColumns = acc.UpdateColumns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToArray(),
                selectColumns = acc.SelectColumns.OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToArray(),
                deleteAllowed = acc.RequiresDelete,
                grantStatements = tableGrants
            });
        }

        return new
        {
            dataSource = dataSourceName,
            dialect = dialect.ToString(),
            tenantId = tenantId.Value,
            tables = tableEntries,
            grantStatements = statements,
            notes
        };
    }

    private static void AddWhereColumns(
        SortedSet<string> selectColumns,
        string? tenantColumn,
        TableMetadata? metadata,
        WriteRule rule,
        Consent consent)
    {
        if (!string.IsNullOrWhiteSpace(tenantColumn))
        {
            selectColumns.Add(tenantColumn);
        }

        if (metadata?.PrimaryKeyColumns is { Count: > 0 })
        {
            foreach (string pk in metadata.PrimaryKeyColumns)
            {
                if (!string.IsNullOrWhiteSpace(pk))
                {
                    selectColumns.Add(pk);
                }
            }
        }

        if (rule.RowFilter is { Count: > 0 })
        {
            foreach (GrantRowScopeEntry filter in rule.RowFilter)
            {
                if (filter.ColumnMap is not null)
                {
                    foreach (string mappedColumn in filter.ColumnMap.Values)
                    {
                        if (!string.IsNullOrWhiteSpace(mappedColumn))
                        {
                            selectColumns.Add(mappedColumn);
                        }
                    }
                }
            }
        }

        foreach (ConsentRowFilter rowFilter in consent.RowFilters)
        {
            if (!string.IsNullOrWhiteSpace(rowFilter.ColumnName))
            {
                selectColumns.Add(rowFilter.ColumnName);
            }
        }
    }

    private static List<string> GenerateGrantStatements(TableWriteAccumulator acc, DatabaseDialect dialect)
    {
        var grants = new List<string>();
        string qualifiedTable = QuoteQualifiedTable(acc.Table.Schema, acc.Table.TableName, dialect);

        if (dialect == DatabaseDialect.Sqlite)
        {
            string ops = string.Join(", ", acc.Operations.Select(o => o.ToString().ToUpperInvariant()));
            grants.Add($"-- SQLite requires write file-system access on {qualifiedTable} for {ops}.");
            return grants;
        }

        string principal = DefaultPrincipalPlaceholder;

        if (acc.InsertColumns.Count > 0)
        {
            if (dialect == DatabaseDialect.SqlServer)
            {
                grants.Add($"GRANT INSERT ON {qualifiedTable} TO {principal};");
            }
            else
            {
                string cols = JoinQuotedColumns(acc.InsertColumns, dialect);
                grants.Add($"GRANT INSERT ({cols}) ON {qualifiedTable} TO {principal};");
            }
        }

        if (acc.UpdateColumns.Count > 0)
        {
            string cols = JoinQuotedColumns(acc.UpdateColumns, dialect);
            grants.Add($"GRANT UPDATE ({cols}) ON {qualifiedTable} TO {principal};");
        }

        if (acc.RequiresDelete)
        {
            grants.Add($"GRANT DELETE ON {qualifiedTable} TO {principal};");
        }

        if (acc.SelectColumns.Count > 0)
        {
            string cols = JoinQuotedColumns(acc.SelectColumns, dialect);
            grants.Add($"GRANT SELECT ({cols}) ON {qualifiedTable} TO {principal};");
        }

        return grants;
    }

    private static string QuoteQualifiedTable(string schema, string table, DatabaseDialect dialect) =>
        $"{QuoteIdentifier(schema, dialect)}.{QuoteIdentifier(table, dialect)}";

    private static string JoinQuotedColumns(IEnumerable<string> columns, DatabaseDialect dialect) =>
        string.Join(", ", columns.Select(c => QuoteIdentifier(c, dialect)));

    private static string QuoteIdentifier(string identifier, DatabaseDialect dialect)
    {
        if (dialect == DatabaseDialect.SqlServer)
        {
            return $"[{identifier.Replace("]", "]]", StringComparison.Ordinal)}]";
        }

        return $"\"{identifier.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }
}
