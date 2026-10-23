namespace Autheris.Tests.SqlCompilerGate.Parity;

using System.Data.Common;
using System.Globalization;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Connectors;
using Autheris.Application.Interfaces;
using Autheris.Application.Procedures.Interfaces;
using Autheris.Application.Procedures.Services;
using Autheris.Application.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Application.Streaming.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.SqlCompilerGate.Databases;
using Autheris.Tests.SqlCompilerGate.Harness;
using Autheris.Tests.SqlCompilerGate.Model;
using Microsoft.Extensions.Options;

/// <summary>The database and dialect one consumer runs on.</summary>
public sealed record ParityEnv(GateDatabase Database, ParityContext Context)
{
    public string Table => Context.Dialect == GateDialect.Oracle ? "ORDERS" : "orders";
}

/// <summary>
/// One §7.3 consumer. It receives the text and the values of a producer exactly as the production consumer reads them from a
/// <see cref="TableAccessDecision"/> and returns the ids of the tenant-A orders it admits. A refusal is an <see cref="EngineOutcome.Rejected"/>.
/// </summary>
public interface IParityConsumer
{
    /// <summary>The consumer name as listed in plan 7.3 (with the production class it stands for).</summary>
    string Name { get; }

    /// <summary>The name of the tenant parameter the consumer binds itself (no marker), or null when it binds none.</summary>
    string? OwnTenantParameter { get; }

    Task<EngineOutcome> RunAsync(ParityEnv env, FilterText filter);
}

internal static class ParityData
{
    public const string Source = "gate";

    private static string DataType(ColumnDef c) => c.Kind switch
    {
        ColumnKind.Whole => "bigint",
        ColumnKind.Fixed => "decimal",
        ColumnKind.Timestamp => "datetime",
        _ => "text"
    };

    public static TableMetadata OrdersMetadata(ParityContext ctx) => new()
    {
        Identifier = new TableIdentifier(Source, ctx.Schema, "orders"),
        Table = new Table
        {
            SourceName = Source, SchemaName = ctx.Schema, TableName = "orders", SourceType = ctx.Database.ToString(),
            DataSourceType = DataSourceType.Sql, IsActive = true
        },
        PrimaryKeyColumns = ["id"],
        Columns = [.. GateModel.Table("orders").Columns.Select(c => new TableColumn { ColumnName = c.Name, DataType = DataType(c) })]
    };

    public static TableAccessDecision Decision(ParityContext ctx, FilterText filter)
    {
        var access = GateModel.Table("orders").Columns.ToDictionary(c => c.Name, _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase);
        return TableAccessDecision.Allowed(
            new TableIdentifier(Source, ctx.Schema, "orders"), access,
            string.IsNullOrEmpty(filter.Sql) ? null : filter.Sql, hasUnconstrainedColumnAllow: true,
            filter.Parameters.Count == 0 ? null : filter.Parameters);
    }

    /// <summary>The decision of the tree consumer: a typed row policy when the filter has one (SQLA-06), else the text-only decision the tree compiler denies.</summary>
    public static TableAccessDecision TreeDecision(ParityContext ctx, FilterText filter)
    {
        if (filter.Typed is not { } typed) return Decision(ctx, filter);
        var access = GateModel.Table("orders").Columns.ToDictionary(c => c.Name, _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase);
        var policy = new Autheris.Application.Policy.PolicyPredicateRowPolicy(
            typed, Autheris.Application.Sql.Policy.PolicyParameterNamer.ToTargetDialect(ctx.Database), new Autheris.Application.Sql.Policy.PolicyPredicateRenderer());
        return TableAccessDecision.Allowed(new TableIdentifier(Source, ctx.Schema, "orders"), access, hasUnconstrainedColumnAllow: true)
            .WithAdditionalRowPolicy(policy);
    }

    public static ClaimsPrincipal Principal() => new(new ClaimsIdentity(
        [new Claim("tenant_id", GateModel.TenantA), new Claim(ClaimTypes.PrimarySid, "S-1-5-21-GATE")], "Gate"));

    /// <summary>The fixture rows of tenant A in <c>orders</c> as the in-memory consumers receive them.</summary>
    public static List<IReadOnlyDictionary<string, object?>> TenantARows()
    {
        var table = GateModel.Table("orders");
        return [.. GateModel.Rows.Where(r => r.Table == "orders" && r.Tenant == GateModel.TenantA).Select(r =>
            (IReadOnlyDictionary<string, object?>)table.Columns.Select((c, i) => (c.Name, r.Values[i]))
                .ToDictionary(p => p.Name, p => p.Item2, StringComparer.OrdinalIgnoreCase))];
    }

    public static long IdOf(object? value) => Convert.ToInt64(value, CultureInfo.InvariantCulture);

    public static EngineOutcome Ids(IEnumerable<long> ids) => new EngineOutcome.Rows([.. ids.Select(i => new object?[] { i })]);

    public static EngineOutcome Refused(Exception ex) => new EngineOutcome.Rejected(RejectionStage.Compile, ex.GetType().Name, GateRunner.IsGoverned(ex) || ex is InvalidOperationException);

    public static EngineOutcome DatabaseError(Exception ex) => new EngineOutcome.Rejected(RejectionStage.Execute, ex.GetType().Name, false);
}

/// <summary>A connection factory over the gate database (the production factory needs a configured connection string).</summary>
internal sealed class GateConnectionFactory(GateDatabase database) : ISqlConnectionFactory
{
    public async Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default) =>
        await database.OpenAsync();
}

/// <summary><c>TreeSqlCompiler</c>: the GraphQL tree compiler (ADR-004).</summary>
public sealed class TreeSqlCompilerConsumer : IParityConsumer
{
    public string Name => "TreeSqlCompiler";

    public string? OwnTenantParameter => "gq0";

    public async Task<EngineOutcome> RunAsync(ParityEnv env, FilterText filter)
    {
        var metadata = ParityData.OrdersMetadata(env.Context);
        var access = new Dictionary<TableIdentifier, TreeTableAccess>
        {
            [metadata.Identifier] = new(metadata, ParityData.TreeDecision(env.Context, filter), GateModel.TenantColumn, GateModel.TenantA)
        };
        var root = new TreeQueryNode(metadata.Identifier, ["id"]) { OrderBy = [new TreeOrder("id")], Limit = 5000 };
        CompiledTreeQuery compiled;
        try
        {
            compiled = TreeSqlCompiler.Compile(root, access, env.Context.Database);
        }
        catch (Exception ex) when (ex is SecurityException or InvalidOperationException or ArgumentException or Autheris.Domain.Exceptions.GatewayInvalidQueryException
            or Autheris.Domain.Exceptions.GatewaySecurityException)
        {
            return ParityData.Refused(ex);
        }

        try
        {
            await using var conn = await env.Database.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = compiled.Sql;
            foreach (var (name, value) in compiled.Parameters)
            {
                var p = cmd.CreateParameter();
                p.ParameterName = name;
                p.Value = value ?? DBNull.Value;
                cmd.Parameters.Add(p);
            }

            // SQL Server splits FOR JSON output over several rows: the finished document is the concatenation.
            var json = new System.Text.StringBuilder();
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync()) json.Append(reader.GetString(0));
            if (json.Length == 0) return ParityData.Ids([]);
            using var doc = JsonDocument.Parse(json.ToString());
            return ParityData.Ids(doc.RootElement.EnumerateArray().Select(e => e.GetProperty("id").GetInt64()));
        }
        catch (Exception ex)
        {
            return ParityData.DatabaseError(ex);
        }
    }
}

/// <summary><c>SqlProcedureRowScopeResolver</c> (governed procedures, <c>GovernedProcedureExecutionService</c>).</summary>
public sealed class ProcedureRowScopeConsumer : IParityConsumer
{
    public string Name => "SqlProcedureRowScopeResolver";

    public string? OwnTenantParameter => "rs_tenant";

    public async Task<EngineOutcome> RunAsync(ParityEnv env, FilterText filter)
    {
        var options = Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    [ParityData.Source] = new() { ConnectionString = "gate", Provider = env.Context.Database.ToString() }
                }
            }
        });
        var resolver = new SqlProcedureRowScopeResolver(new GateConnectionFactory(env.Database), options);
        var definition = ProcedureDefinitionParser.Parse(
            $"-- @name p\n-- @procedure dbo.usp_P\n-- @result-table {env.Context.Schema}.orders\n-- @row-scope-key id", "x", false, 60) with { DataSource = ParityData.Source };
        var ids = GateModel.Rows.Where(r => r.Table == "orders").Select(r => r.Id).ToList();
        var byKey = ids.ToDictionary(i => RowScopeKeys.Normalize([i])!, i => i, StringComparer.Ordinal);
        try
        {
            var allowed = await resolver.GetAllowedKeysAsync(
                definition, ParityData.OrdersMetadata(env.Context), ParityData.Decision(env.Context, filter), ["id"],
                [.. ids.Select(i => new object?[] { i })], new ProcedureSecurityContext(GateModel.TenantA, "S-1-5-21-GATE", null), CancellationToken.None);
            return ParityData.Ids(allowed.Select(k => byKey[k]));
        }
        catch (Exception ex) when (ex is SecurityException or InvalidOperationException or ArgumentException)
        {
            return ParityData.Refused(ex);
        }
        catch (Exception ex)
        {
            return ParityData.DatabaseError(ex);
        }
    }
}

/// <summary>
/// <c>GatewayExecutionService.FilterRows</c>: the in-memory row filter of <c>GovernedConnectorReader</c>, <c>DeltaLakeDataSourceExecutor</c>,
/// <c>LakehouseDataSourceExecutor</c>, the HTTP and file sources and <c>IcebergRestCatalogFederationService</c> (plan 7.3).
/// </summary>
public sealed class InMemoryFilterRowsConsumer : IParityConsumer
{
    public string Name => "GatewayExecutionService.FilterRows";

    public string? OwnTenantParameter => null;

    public Task<EngineOutcome> RunAsync(ParityEnv env, FilterText filter)
    {
        var rows = ParityData.TenantARows();
        try
        {
            if (string.IsNullOrEmpty(filter.Sql)) return Task.FromResult(ParityData.Ids(rows.Select(r => ParityData.IdOf(r["id"]))));
            var kept = GatewayExecutionService.FilterRows(rows, filter.Sql, ParityData.OrdersMetadata(env.Context), filter.Parameters);

            // An empty result for a non-empty input is a refusal that looks like "no rows" (the evaluator fails closed this way).
            return Task.FromResult(ParityData.Ids(kept.Select(r => ParityData.IdOf(r["id"]))));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ParityData.Refused(ex));
        }
    }
}

/// <summary>
/// <c>StreamRlsPolicyEnforcer</c> through <c>StreamingRowFilterAstEvaluator</c>. It reads the text only and binds no values, so the typed text
/// is evaluated as produced (<see cref="Inlined"/> false) and after the marker inlining of the other in-memory consumers (<see cref="Inlined"/> true).
/// </summary>
public sealed class StreamingEvaluatorConsumer(bool inlined) : IParityConsumer
{
    public bool Inlined => inlined;

    public string Name => inlined ? "StreamingRowFilterAstEvaluator (markers inlined)" : "StreamingRowFilterAstEvaluator";

    public string? OwnTenantParameter => null;

    public Task<EngineOutcome> RunAsync(ParityEnv env, FilterText filter)
    {
        try
        {
            string sql = inlined ? PolicyMarkerInliner.Inline(filter.Sql, filter.Parameters) : filter.Sql;
            var kept = ParityData.TenantARows().Where(r => StreamingRowFilterAstEvaluator.Matches(r, sql));
            return Task.FromResult(ParityData.Ids(kept.Select(r => ParityData.IdOf(r["id"]))));
        }
        catch (Exception ex)
        {
            return Task.FromResult(ParityData.Refused(ex));
        }
    }
}

/// <summary>
/// The dataset splice of <c>GovernedDataQueryService</c>: the marker text is inlined by <c>PolicyMarkerInliner</c> and appended to the dataset
/// query as <c>WHERE tenant AND (filter)</c> (the dataset text is user SQL, so values cannot be bound there).
/// </summary>
public sealed class DatasetSpliceConsumer : IParityConsumer
{
    public string Name => "GovernedDataQueryService (dataset splice)";

    public string? OwnTenantParameter => null;

    public async Task<EngineOutcome> RunAsync(ParityEnv env, FilterText filter)
    {
        string predicate;
        try
        {
            predicate = string.IsNullOrEmpty(filter.Sql) ? "1 = 1" : PolicyMarkerInliner.Inline(filter.Sql, filter.Parameters);
        }
        catch (Exception ex) when (ex is SecurityException or InvalidOperationException or ArgumentException)
        {
            return ParityData.Refused(ex);
        }

        try
        {
            await using var conn = await env.Database.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT id FROM {env.Context.Schema}.orders AS autheris_target WHERE tenant_id = '{GateModel.TenantA}' AND ({predicate})";
            await using var reader = await cmd.ExecuteReaderAsync();
            var ids = new List<long>();
            while (await reader.ReadAsync()) ids.Add(Convert.ToInt64(reader.GetValue(0), CultureInfo.InvariantCulture));
            return ParityData.Ids(ids);
        }
        catch (Exception ex)
        {
            return ParityData.DatabaseError(ex);
        }
    }
}

/// <summary>
/// <c>DuckDbOlapEndpoints</c> and <c>FederatedStagingService</c> hand <c>CombinedRowFilterSql</c> to the connector as <c>PushdownFilterSql</c>;
/// the connector security policy validates it before any query. The outcome is acceptance (an empty row set) or refusal.
/// </summary>
public sealed class ConnectorPushdownValidationConsumer : IParityConsumer
{
    public string Name => "ConnectorSecurityPolicyEvaluator (DuckDB OLAP, federated staging)";

    public string? OwnTenantParameter => null;

    public Task<EngineOutcome> RunAsync(ParityEnv env, FilterText filter)
    {
        var session = new ConnectorSessionContext(
            ParityData.Principal(), new TenantId(GateModel.TenantA), ParityData.Decision(env.Context, filter), ["id"],
            new Dictionary<string, object?>(), filter.Sql);
        try
        {
            ConnectorSecurityPolicyEvaluator.EnforceSecurityPolicy(session, ParityData.OrdersMetadata(env.Context));
            return Task.FromResult<EngineOutcome>(new EngineOutcome.Rows([]));
        }
        catch (Exception ex) when (ex is SecurityException or InvalidOperationException or ArgumentException)
        {
            return Task.FromResult(ParityData.Refused(ex));
        }
    }
}

public static class ParityConsumers
{
    /// <summary>The consumers that evaluate rows (their results are classified directionally).</summary>
    public static IReadOnlyList<IParityConsumer> RowConsumers(bool databaseBacked) =>
    [
        .. databaseBacked
            ? new IParityConsumer[] { new TreeSqlCompilerConsumer(), new ProcedureRowScopeConsumer(), new DatasetSpliceConsumer() }
            : [],
        new InMemoryFilterRowsConsumer(),
        new StreamingEvaluatorConsumer(inlined: false),
        new StreamingEvaluatorConsumer(inlined: true)
    ];

    public static IParityConsumer Pushdown { get; } = new ConnectorPushdownValidationConsumer();
}
