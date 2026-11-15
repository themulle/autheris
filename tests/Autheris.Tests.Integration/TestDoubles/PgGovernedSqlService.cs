namespace Autheris.Tests.Integration.TestDoubles;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataSources.Interfaces;
using Autheris.Application.DataSources.Services;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Npgsql;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// PostgreSQL compile environment for integration tests using the real <see cref="FastSqlEngine"/> pipeline.
/// </summary>
internal sealed class PgCompileEnv(string defaultSchema = "public")
{
    private readonly List<TableCatalogEntry> _tables = [];
    private readonly Policies _policies = new();

    public FastSqlEngine Engine { get; } = new();

    public PgCompileEnv Table(string name, string? tenantColumn, params string[] columns)
    {
        var cols = ImmutableArray.CreateBuilder<CatalogColumn>();
        foreach (var c in columns)
        {
            var parts = c.Split(':');
            cols.Add(new CatalogColumn(parts[0], parts.Length > 1 ? parts[1] : "text"));
        }

        _tables.Add(new TableCatalogEntry(new TableIdentity(defaultSchema, name), cols.ToImmutable(), tenantColumn, 1));
        return this;
    }

    public PgCompileEnv Policy(string table, PolicyPredicate predicate)
    {
        _policies.Predicates[new TableIdentity(defaultSchema, table)] = predicate;
        return this;
    }

    public PgCompileEnv Mask(string table, string column, MaskSpec spec)
    {
        _policies.Masks[(new TableIdentity(defaultSchema, table), column.ToLowerInvariant())] = spec;
        return this;
    }

    public CompiledSql Compile(string sql, string tenant = "t1") =>
        Engine.Compile(
            sql.AsMemory(),
            new CompileRequest
            {
                TargetDialect = TargetSqlDialect.PostgreSql,
                TokenGuards = SqlTokenSecurityOptions.Strict,
                Statements = StatementPermissions.ReadOnly,
                AllowExperimentalDialect = true,
                Policy = new GovernancePolicy
                {
                    RowFilters = _policies,
                    Masks = _policies,
                    Catalog = new InMemoryTableCatalog(_tables, defaultSchema),
                    Tenant = new TenantBinding("__autheris_tenant", tenant, SqlParameterType.String),
                    TablesWithConsentRowFilter = new HashSet<string>()
                }
            },
            CancellationToken.None);

    private sealed class Policies : IPolicyPredicateProvider, IColumnMaskProvider
    {
        public Dictionary<TableIdentity, PolicyPredicate> Predicates { get; } = new();
        public Dictionary<(TableIdentity, string), MaskSpec> Masks { get; } = new();
        public bool ShouldApplyPolicy(TableIdentity table) => Predicates.ContainsKey(table);
        public PolicyPredicate GetPredicate(TableIdentity table) => Predicates.TryGetValue(table, out var p) ? p : PolicyPredicate.DenyAll;
        public bool HasMask(TableIdentity table, string column) => Masks.ContainsKey((table, column.ToLowerInvariant()));
        public MaskSpec GetMask(TableIdentity table, string column) => Masks[(table, column.ToLowerInvariant())];
    }
}

/// <summary>
/// Test double of <see cref="IGovernedSqlExecutionService"/> that runs planned Trino SQL through
/// <see cref="FastSqlEngine.Compile"/> and executes the bound PostgreSQL SQL against <see cref="PostgreSqlTestDatabase"/>.
/// </summary>
internal sealed class PgGovernedSqlService(string connectionString, PgCompileEnv env) : IGovernedSqlExecutionService
{
    private readonly PostgreSqlCompiledSqlBinder _binder = new();

    public Task ExecuteGovernedQueryAsync(
        GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId,
        Func<System.Data.Common.DbDataReader, CancellationToken, Task> rowWriter, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public async Task<GovernedSqlResult> ExecuteQueryBufferedAsync(
        GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
    {
        var sql = GovernedSqlExecutionService.NormalizeClientParameters(request.Sql, request.Parameters, out _);
        var compiled = env.Compile(sql, tenantId.Value);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct);
        await using var command = connection.CreateCommand();
        var clientValues = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in request.Parameters ?? new Dictionary<string, object?>())
        {
            clientValues[name.TrimStart('@')] = value;
        }

        _binder.Bind(command, compiled, clientValues);

        var columns = new List<string>();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(reader.GetName(i));
            }

            while (await reader.ReadAsync(ct))
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[columns[i]] = await reader.IsDBNullAsync(i, ct) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }
        }

        return new GovernedSqlResult(request.Sql, columns, rows, rows.Count, 0);
    }

    public SqlTableReadExecutor Executor(IGovernedTableReadPlanner? planner = null) =>
        new(planner ?? new GovernedTableReadPlanner(), () => this);

    public static PolicyPredicate RowPolicy(string column, string value) => PolicyPredicate.Create(
        new BinaryExpression(
            new ColumnReference(new SqlQualifiedName([new SqlIdentifier(column, false)])),
            BinaryOperator.Equal,
            new PolicyParameterExpression("__pol_" + column, SqlParameterType.String)),
        new Dictionary<string, PolicyValue> { ["__pol_" + column] = new(value, SqlParameterType.String) });

    public static MaskSpec Redact(string column, string replacement) => new(
        MaskKind.Redact,
        new MaskArguments(Constant: new PolicyParameterExpression("__mask_" + column, SqlParameterType.String, ParameterOrigin.Mask)),
        new Dictionary<string, PolicyValue> { ["__mask_" + column] = new(replacement, SqlParameterType.String) }.ToFrozenDictionary());
}
