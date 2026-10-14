using System.Globalization;
using System.Security;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;

namespace Autheris.Application.Services;

/// <summary>
/// WP-ARC-B-02: the governed table-read executor of SQL data sources. It replaces the former <c>SqlDataSourceExecutor</c>, which
/// built its own SQL text: the read intent is planned by <see cref="IGovernedTableReadPlanner"/> (Trino text plus client
/// parameters) and executed through <see cref="IGovernedSqlExecutionService"/>, so tenant, row policy, masks, limits and the audit
/// digest all come from the single compiler path. No SQL text is built here and no <c>DbCommand</c> is touched.
/// <para>
/// Two attestations are reported through <see cref="DataSourceExecutionContext.Items"/>: <c>RlsPushdownExecuted</c> (the compiler
/// applied the row policy) and <c>InDbColumnMaskingExecuted</c> (it applied the masks), so the caller neither filters nor masks
/// the rows a second time.
/// </para>
/// </summary>
public sealed class SqlTableReadExecutor : IDataSourceExecutor
{
    /// <summary>16 MB limit per binary column value (SEC-SPEC-05).</summary>
    public const int MaxAllowedBinaryBytes = 16 * 1024 * 1024;

    internal const string RlsPushdownExecutedKey = "RlsPushdownExecuted";
    internal const string InDbColumnMaskingExecutedKey = "InDbColumnMaskingExecuted";

    private static readonly HashSet<string> SpatialTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "geometry", "geography", "spatial", "point", "polygon", "linestring", "multipolygon", "multipoint", "sdo_geometry", "hierarchyid"
    };

    private readonly IGovernedTableReadPlanner _planner;
    private readonly Func<IGovernedSqlExecutionService> _executionService;
    private readonly IOptions<GatewayOptions>? _options;

    /// <param name="planner">The shared table-read planner.</param>
    /// <param name="executionService">
    /// Resolved on first use. The governed execution service reaches the federated staging service, which enumerates the
    /// registered <see cref="IDataSourceExecutor"/>s (this one included); resolving it lazily keeps that cycle out of the
    /// constructor graph.
    /// </param>
    /// <param name="options">Gateway options (the masking kill switch).</param>
    public SqlTableReadExecutor(
        IGovernedTableReadPlanner planner,
        Func<IGovernedSqlExecutionService> executionService,
        IOptions<GatewayOptions>? options = null)
    {
        _planner = planner ?? throw new ArgumentNullException(nameof(planner));
        _executionService = executionService ?? throw new ArgumentNullException(nameof(executionService));
        _options = options;
    }

    public DataSourceType SupportedType => DataSourceType.Sql;

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(
        DataSourceExecutionContext context,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var metadata = context.Metadata;
        var decision = context.AccessDecision;

        // SEC-AC-01: fail closed before any plan or connection.
        if (!decision.IsAllowed)
        {
            var reasons = decision.DeniedReasons.Count > 0 ? string.Join("; ", decision.DeniedReasons) : "Table is blocked by access policy.";
            throw new SecurityException($"Zero-Trust violation: Access to table '{metadata.Identifier}' denied: {reasons}");
        }

        // The same stage the other governed adapters run: the compiler keeps every role claim, so a non-canonical
        // ClusterAdmin claim must be refused here.
        GovernedQueryStages.EnsureCanonicalClusterAdminClaim(context.Principal, metadata.Identifier);

        var tenant = ResolveTenant(context);
        var selected = ResolveSelectedColumns(context);
        RejectSpatialColumns(metadata, decision, selected);

        var limit = Math.Max(1, context.Limit);
        var intent = new TableReadIntent(
            metadata.Identifier,
            selected,
            BuildFilter(context),
            context.Items.TryGetValue(TableQueryItems.OrderBy, out var order) && order is IReadOnlyList<TableOrderBy> orderBy ? orderBy : null,
            limit,
            Math.Max(0, context.Offset),
            SqlTransports.Rest);

        var planned = _planner.Plan(intent, metadata, decision);
        var service = _executionService();

        var result = await service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest(planned.Sql, planned.Parameters.Count > 0 ? planned.Parameters : null, Transport: intent.Transport, PlannedTableRead: true),
            context.Principal,
            tenant,
            ct).ConfigureAwait(false);

        // Fail closed: a row cap below the requested page must never surface as a short page.
        if (result.Truncated && result.Rows.Count < limit)
        {
            throw new GatewaySecurityException(
                "The read exceeds the row limit of this data source; reduce the page size.", "ROW_LIMIT_EXCEEDED");
        }

        var rows = new List<IReadOnlyDictionary<string, object?>>(result.Rows.Count);
        foreach (var source in result.Rows)
        {
            var row = new Dictionary<string, object?>(planned.ColumnsToSelect.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var column in planned.ColumnsToSelect)
            {
                source.TryGetValue(column, out var raw);
                row[column] = NormalizeReadValue(raw, column);
            }

            rows.Add(row);
        }

        if (context.Items.TryGetValue(TableQueryItems.CountTotal, out var countRequested) && countRequested is true)
        {
            context.Items[TableQueryItems.TotalCount] = await CountAsync(service, intent, metadata, decision, context, tenant, ct).ConfigureAwait(false);
        }

        context.Items[RlsPushdownExecutedKey] = true;
        if (_options?.Value?.IsColumnMaskingDisabled != true &&
            planned.ColumnsToSelect.Any(c => decision.GetEffectiveColumnAccess(c, metadata) == ColumnAccessLevel.Mask))
        {
            context.Items[InDbColumnMaskingExecutedKey] = true;
        }

        return rows;
    }

    private async Task<long> CountAsync(
        IGovernedSqlExecutionService service,
        TableReadIntent intent,
        TableMetadata metadata,
        TableAccessDecision decision,
        DataSourceExecutionContext context,
        TenantId tenant,
        CancellationToken ct)
    {
        var count = _planner.PlanCount(intent, metadata, decision);
        var result = await service.ExecuteQueryBufferedAsync(
            new GovernedSqlQueryRequest(count.Sql, count.Parameters.Count > 0 ? count.Parameters : null, Transport: intent.Transport, PlannedTableRead: true),
            context.Principal,
            tenant,
            ct).ConfigureAwait(false);

        if (result.Rows.Count == 0)
        {
            return 0;
        }

        var first = result.Rows[0];
        var value = first.TryGetValue(GovernedTableReadPlanner.CountColumnAlias, out var named) ? named : first.Values.FirstOrDefault();
        return Convert.ToInt64(value, CultureInfo.InvariantCulture);
    }

    private static TenantId ResolveTenant(DataSourceExecutionContext context)
    {
        if (context.Tenant is { } tenant)
        {
            return tenant;
        }

        var claim = context.Principal?.FindFirst("tenant_id")?.Value ?? context.Principal?.FindFirst("tenant")?.Value;
        if (claim == null)
        {
            return TenantId.LegacySingleTenant;
        }

        return TenantId.TryParse(claim, out var parsed) ? parsed : throw new GatewayForbiddenException("Invalid tenant identity.");
    }

    /// <summary>The requested columns without the denied ones (an empty request selects every accessible column in the planner).</summary>
    private static IReadOnlyList<string>? ResolveSelectedColumns(DataSourceExecutionContext context)
    {
        if (context.RequestedFields == null || context.RequestedFields.Count == 0)
        {
            return null;
        }

        var allowed = context.RequestedFields
            .Where(c => context.AccessDecision.GetEffectiveColumnAccess(c, context.Metadata) != ColumnAccessLevel.Deny)
            .ToList();
        if (allowed.Count == 0)
        {
            throw new UnauthorizedAccessException($"No accessible columns requested on table '{context.Metadata.Identifier.ToQualifiedName()}'.");
        }

        return allowed;
    }

    /// <summary>
    /// Spatial and hierarchy columns are not projected through the governed path (the driver value cannot be returned as a JSON
    /// scalar): fail closed with the stable unsupported-column error instead of returning a provider object.
    /// </summary>
    private static void RejectSpatialColumns(TableMetadata metadata, TableAccessDecision decision, IReadOnlyList<string>? selected)
    {
        var columns = selected == null
            ? metadata.Columns.Where(c => decision.GetEffectiveColumnAccess(c.ColumnName, metadata) != ColumnAccessLevel.Deny).ToList()
            : selected.Select(metadata.GetColumn).Where(c => c != null).Select(c => c!).ToList();
        foreach (var column in columns)
        {
            var type = column.DataType?.Trim();
            if (!string.IsNullOrEmpty(type) && SpatialTypes.Contains(type))
            {
                throw new GatewayUnsupportedColumnTypeException(column.ColumnName, type);
            }
        }
    }

    /// <summary>
    /// Equality arguments (GraphQL, MCP) and the pushed-down filter of <see cref="TableQueryItems.Filter"/> as one neutral
    /// clause. Values are client parameters; the planner authorizes every referenced column.
    /// </summary>
    private static TableFilterClause? BuildFilter(DataSourceExecutionContext context)
    {
        var clauses = new List<TableFilterClause>();
        var index = 0;
        foreach (var (key, value) in context.Arguments)
        {
            if (string.Equals(key, "limit", StringComparison.OrdinalIgnoreCase) || string.Equals(key, "offset", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var column = context.Metadata.GetColumn(key);
            if (column == null || value == null)
            {
                continue;
            }

            var marker = '@' + "p_arg_" + (index++).ToString(CultureInfo.InvariantCulture);
            var name = column.ColumnName;
            clauses.Add(new TableFilterClause(
                $"{name} = {marker}",
                new Dictionary<string, object?> { [marker] = value },
                new[] { name })
            {
                TrinoSqlFactory = resolve => $"{DataApiFilterTranslator.QuoteIdentifier(resolve(name))} = {marker}"
            });
        }

        if (context.Items.TryGetValue(TableQueryItems.Filter, out var filterObj) && filterObj is TableFilterClause filter)
        {
            clauses.Add(filter);
        }

        if (clauses.Count == 0)
        {
            return null;
        }

        if (clauses.Count == 1)
        {
            return clauses[0];
        }

        var parameters = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var clause in clauses)
        {
            foreach (var (name, value) in clause.Parameters)
            {
                // SEC-ADG-02: a duplicate parameter name fails closed instead of letting one of two values win.
                if (!parameters.TryAdd(name, value))
                {
                    throw new SecurityException("A statement parameter name is bound twice (fail-closed).");
                }
            }
        }

        return new TableFilterClause(
            string.Join(" AND ", clauses.Select(c => "(" + c.SqlPredicate + ")")),
            parameters,
            clauses.SelectMany(c => c.ReferencedColumns).Distinct(StringComparer.OrdinalIgnoreCase).ToList())
        {
            TrinoSqlFactory = resolve => string.Join(" AND ", clauses.Select(c => "(" + c.GetTrinoPredicate(resolve) + ")"))
        };
    }

    /// <summary>Culture-invariant normalization of a driver value (ISO dates in UTC, base64 binary).</summary>
    public static object? NormalizeReadValue(object? rawValue, string? columnName = null)
    {
        if (rawValue == null || rawValue is DBNull)
        {
            return null;
        }

        // SEC-SPEC-05: LOH allocation defense & safe base64 representation for binary blobs
        if (rawValue is byte[] bytes)
        {
            if (bytes.Length > MaxAllowedBinaryBytes)
            {
                throw new SecurityException($"Binary column '{columnName ?? "unknown"}' exceeds the maximum allowed size of {MaxAllowedBinaryBytes / (1024 * 1024)} MB.");
            }

            return Convert.ToBase64String(bytes);
        }

        // SEC-SPEC-04: Deterministic culture-invariant UTC normalization
        if (rawValue is DateTime dt)
        {
            if (dt.Kind == DateTimeKind.Unspecified)
            {
                dt = DateTime.SpecifyKind(dt, DateTimeKind.Utc);
            }

            return dt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
        }

        if (rawValue is DateTimeOffset dto)
        {
            return dto.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
        }

        if (rawValue is DateOnly d)
        {
            return d.ToString("O", CultureInfo.InvariantCulture);
        }

        if (rawValue is TimeOnly t)
        {
            return t.ToString("O", CultureInfo.InvariantCulture);
        }

        if (rawValue is TimeSpan ts)
        {
            return ts.ToString("c", CultureInfo.InvariantCulture);
        }

        // Defense in depth: normalize ISO strings from DB without explicit UTC indicator to canonical UTC 'Z'
        if (rawValue is string s && s.Length >= 19 && s[10] == 'T' && !s.EndsWith('Z') && !s.Contains('+') && s.IndexOf('-', 11) == -1)
        {
            return s + "Z";
        }

        // Defense in depth: spatial CLR objects the driver may hand back
        var typeName = rawValue.GetType().FullName;
        if (typeName != null && (typeName.Contains("Spatial", StringComparison.OrdinalIgnoreCase) ||
                                 typeName.Contains("Geometry", StringComparison.OrdinalIgnoreCase) ||
                                 typeName.Contains("Geography", StringComparison.OrdinalIgnoreCase)))
        {
            return rawValue.ToString();
        }

        return rawValue;
    }
}
