namespace Autheris.Tests.Unit.TestDoubles;

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Services;
using Autheris.Application.Sql.Interfaces;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Tests.Unit.Sql;
using Microsoft.Data.Sqlite;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

/// <summary>
/// Test double of <see cref="IGovernedSqlExecutionService"/> that runs the planned Trino text through the real compiler
/// (<see cref="TestCompileEnv"/>: catalog, tenant, row policy, masks) and the SQLite binder against an open SQLite connection. It
/// stands in for the production pipeline in tests of <see cref="SqlTableReadExecutor"/> and its consumers (test-only).
/// </summary>
internal sealed class SqliteGovernedSqlService(SqliteConnection connection, TestCompileEnv env) : IGovernedSqlExecutionService
{
    public List<GovernedSqlQueryRequest> Requests { get; } = [];

    public Task ExecuteGovernedQueryAsync(
        GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId,
        Func<System.Data.Common.DbDataReader, CancellationToken, Task> rowWriter, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task ValidateAsync(string rawSql, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default) =>
        throw new NotSupportedException();

    public Task<GovernedSqlResult> ExecuteQueryBufferedAsync(
        GovernedSqlQueryRequest request, ClaimsPrincipal user, TenantId tenantId, CancellationToken ct = default)
    {
        Requests.Add(request);
        var sql = GovernedSqlExecutionService.NormalizeClientParameters(request.Sql, request.Parameters, out _);
        var compiled = env.Compile(sql, tenantId.Value);

        using var command = connection.CreateCommand();
        command.CommandText = compiled.Sql;
        var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in request.Parameters ?? new Dictionary<string, object?>())
        {
            values[name.TrimStart('@')] = value;
        }

        CompiledSqlBinders.For(TargetSqlDialect.Sqlite).Bind(command, compiled, values);

        var columns = new List<string>();
        var rows = new List<IReadOnlyDictionary<string, object?>>();
        using (var reader = command.ExecuteReader())
        {
            for (var i = 0; i < reader.FieldCount; i++)
            {
                columns.Add(reader.GetName(i));
            }

            while (reader.Read())
            {
                var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[columns[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                }

                rows.Add(row);
            }
        }

        return Task.FromResult(new GovernedSqlResult(request.Sql, columns, rows, rows.Count, 0));
    }

    /// <summary>A <see cref="SqlTableReadExecutor"/> over this double.</summary>
    public SqlTableReadExecutor Executor(IGovernedTableReadPlanner? planner = null) =>
        new(planner ?? new GovernedTableReadPlanner(), () => this);

    /// <summary>The row policy <c>column = value</c> as a typed predicate (the value is a bound policy parameter).</summary>
    public static PolicyPredicate RowPolicy(string column, string value) => PolicyPredicate.Create(
        new BinaryExpression(
            new ColumnReference(new SqlQualifiedName(new List<SqlIdentifier> { new(column, false) })),
            BinaryOperator.Equal,
            new PolicyParameterExpression("__pol_" + column, SqlParameterType.String)),
        new Dictionary<string, PolicyValue> { ["__pol_" + column] = new(value, SqlParameterType.String) });

    /// <summary>The mask <c>Redact</c> with the given replacement as a typed mask spec.</summary>
    public static MaskSpec Redact(string column, string replacement) => new(
        MaskKind.Redact,
        new MaskArguments(Constant: new PolicyParameterExpression("__mask_" + column, SqlParameterType.String, ParameterOrigin.Mask)),
        new Dictionary<string, PolicyValue> { ["__mask_" + column] = new(replacement, SqlParameterType.String) }.ToFrozenDictionary());
}
