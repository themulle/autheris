namespace Autheris.Tests.SqlCompilerGate.Harness;

using System.Data.Common;
using System.Security;
using Autheris.Tests.SqlCompilerGate.Databases;
using Autheris.Tests.SqlCompilerGate.Model;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Builder;
using TrinoSqlEngine.Ast.Capabilities;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Generators;

/// <summary>
/// Runs one corpus statement through one engine against the fixture and returns the outcome. Queries return their typed rows;
/// DML returns the row images it changed over the whole fixture (a diff of two snapshots), and the fixture is restored before
/// every DML statement.
/// </summary>
public sealed class GateRunner(GateDatabase database, PolicyScenario scenario)
{
    private const int MaxRows = 20_000;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(10);

    private readonly FastSqlEngine _engine = new();
    private readonly GatePolicies _policies = new(database.Dialect, database.DefaultSchema, scenario);

    public PolicyScenario Scenario => scenario;

    /// <summary>The binder of the dialect, or null for a dialect the gate does not know.</summary>
    private DbCommandCompiledSqlBinder? Binder() => database.Dialect switch
    {
        GateDialect.PostgreSql => new PostgreSqlCompiledSqlBinder(),
        GateDialect.SqlServer => new SqlServerCompiledSqlBinder(),
        GateDialect.Oracle => new OracleCompiledSqlBinder(),
        GateDialect.Sqlite => new SqliteCompiledSqlBinder(),
        _ => null
    };

    /// <summary>
    /// Null when the AST compiler supports the database's dialect in this scenario, otherwise why not. An unsupported dialect is a
    /// failure of the gate (see <see cref="DifferentialRules.RequireAstLeg"/>), never a skip. Any other exception propagates.
    /// </summary>
    public string? AstUnavailableReason()
    {
        if (Binder() is null) return $"no compiled-SQL binder for {database.Dialect}";
        try
        {
            _engine.Compile("SELECT id FROM orders".AsMemory(), _policies.AstRequest(StatementPermissions.ReadOnly), CancellationToken.None);
            return null;
        }
        catch (SqlCompileNotSupportedException ex)
        {
            return ex.Message;
        }
    }

    public async Task<EngineOutcome> RunAsync(EngineKind engine, CorpusStatement statement)
    {
        if (!statement.IsDml) return await RunEngineAsync(engine, statement);

        // The fixture is pristine before every write. A write that changed rows restores the tables it changed (and everything
        // after an unexpected failure), so no statement sees another one's changes.
        EngineOutcome outcome;
        try
        {
            outcome = await RunEngineAsync(engine, statement);
        }
        catch
        {
            await database.ResetAsync();
            throw;
        }

        if (outcome is EngineOutcome.Written { Changes.Count: > 0 } written)
        {
            await database.ResetAsync([.. written.Changes.Select(c => c.Table).Distinct(StringComparer.Ordinal)]);
        }

        return outcome;
    }

    private async Task<EngineOutcome> RunEngineAsync(EngineKind engine, CorpusStatement statement)
    {
        return engine switch
        {
            EngineKind.Ast => await RunAstAsync(statement),
            EngineKind.Unfiltered => await RunUnfilteredAsync(statement),
            _ => throw new ArgumentOutOfRangeException(nameof(engine))
        };
    }

    private async Task<EngineOutcome> RunUnfilteredAsync(CorpusStatement statement) =>
        await ExecuteAsync(statement, async (conn, token) =>
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = statement.Sql;
            cmd.CommandTimeout = (int)CommandTimeout.TotalSeconds;
            return await RunCommandAsync(cmd, statement, token);
        });

    private async Task<EngineOutcome> RunAstAsync(CorpusStatement statement)
    {
        var binder = Binder() ?? throw new InvalidOperationException("No binder for this dialect.");
        CompiledSql compiled;
        try
        {
            compiled = _engine.Compile(statement.Sql.AsMemory(), _policies.AstRequest(GatePolicies.PermissionsOf(statement.Class)), CancellationToken.None);
        }
        catch (Exception ex)
        {
            return new EngineOutcome.Rejected(RejectionStage.Compile, ex.GetType().Name, IsGoverned(ex));
        }

        return await ExecuteAsync(statement, async (conn, token) =>
        {
            if (compiled.RequiresRowCountCheck)
            {
                try
                {
                    await CompiledSqlBinders.CheckedExecutorFor(compiled.Dialect).ExecuteAsync(conn, compiled, new Dictionary<string, object?>(), cancellationToken: token);
                }
                catch (GovernedSqlException ex)
                {
                    return new EngineOutcome.Rejected(RejectionStage.Execute, ex.GetType().Name, true);
                }

                return await SnapshotDiffAsync();
            }

            await using var cmd = conn.CreateCommand();
            cmd.CommandTimeout = (int)CommandTimeout.TotalSeconds;
            try
            {
                binder.Bind(cmd, compiled, new Dictionary<string, object?>());
            }
            catch (Exception ex) when (ex is SecurityException or GovernedSqlException)
            {
                return new EngineOutcome.Rejected(RejectionStage.Compile, ex.GetType().Name, true);
            }

            return await RunCommandAsync(cmd, statement, token);
        });
    }

    private async Task<EngineOutcome> ExecuteAsync(CorpusStatement statement, Func<DbConnection, CancellationToken, Task<EngineOutcome>> run)
    {
        Dictionary<string, List<object?[]>>? before = statement.IsDml ? await database.SnapshotAsync() : null;
        _before = before;
        using var cts = new CancellationTokenSource(CommandTimeout + TimeSpan.FromSeconds(5));
        try
        {
            await using var conn = await database.OpenAsync();
            return await run(conn, cts.Token);
        }
        catch (Exception ex)
        {
            return new EngineOutcome.Rejected(RejectionStage.Execute, ex.GetType().Name, false);
        }
    }

    private Dictionary<string, List<object?[]>>? _before;

    private async Task<EngineOutcome> RunCommandAsync(DbCommand cmd, CorpusStatement statement, CancellationToken token)
    {
        if (statement.IsDml)
        {
            await cmd.ExecuteNonQueryAsync(token);
            return await SnapshotDiffAsync();
        }

        var rows = new List<object?[]>();
        await using var reader = await cmd.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token) && rows.Count < MaxRows)
        {
            var row = new object?[reader.FieldCount];
            for (int i = 0; i < row.Length; i++) row[i] = await reader.IsDBNullAsync(i, token) ? null : reader.GetValue(i);
            rows.Add(row);
        }

        return new EngineOutcome.Rows(rows);
    }

    private async Task<EngineOutcome> SnapshotDiffAsync()
    {
        var before = _before ?? throw new InvalidOperationException("DML without a before snapshot.");
        var after = await database.SnapshotAsync();
        var changes = new List<RowChange>();
        foreach (var table in GateModel.Tables)
        {
            var b = CellNormalizer.Multiset(before[table.Name].Select(r => CellNormalizer.RowKey(r, CellNormalizer.Normalize)));
            var a = CellNormalizer.Multiset(after[table.Name].Select(r => CellNormalizer.RowKey(r, CellNormalizer.Normalize)));
            Add(changes, '-', table.Name, before[table.Name], b, a);
            Add(changes, '+', table.Name, after[table.Name], a, b);
        }

        return new EngineOutcome.Written(changes);
    }

    private static void Add(List<RowChange> changes, char sign, string table, List<object?[]> rows, Dictionary<string, int> own, Dictionary<string, int> other)
    {
        var budget = own.ToDictionary(p => p.Key, p => p.Value - other.GetValueOrDefault(p.Key));
        foreach (var row in rows)
        {
            string key = CellNormalizer.RowKey(row, CellNormalizer.Normalize);
            if (budget.TryGetValue(key, out int n) && n > 0)
            {
                changes.Add(new RowChange(sign, table, row));
                budget[key] = n - 1;
            }
        }
    }

    /// <summary>A typed, expected governance refusal (as opposed to an unexpected exception type, which is an AST defect).</summary>
    public static bool IsGoverned(Exception ex) =>
        ex is SecurityException or AstBuildException or GovernedSqlException or ArgumentException
            or OperationCanceledException or Antlr4.Runtime.Misc.ParseCanceledException;
}
