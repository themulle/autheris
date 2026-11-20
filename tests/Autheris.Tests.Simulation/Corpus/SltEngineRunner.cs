using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Data.Common;
using System.Security;
using Autheris.Tests.SqlCompilerGate.Harness;
using Microsoft.Data.Sqlite;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

namespace Autheris.Tests.Simulation.Corpus;

/// <summary>A: permissive (no row filter, no mask). B: row filter <c>a &gt; 120</c> on <c>t1</c>.</summary>
public enum SltScenario
{
    Permissive,
    RowFilter,
}

public abstract record SltEngineOutcome;

public sealed record SltEngineRows(IReadOnlyList<object?[]> Rows) : SltEngineOutcome;

/// <summary>A typed, documented governance refusal (compile or bind stage) or an emitted statement SQLite refuses.</summary>
public sealed record SltEngineRejected(string Stage, string ExceptionType, string Message) : SltEngineOutcome;

/// <summary>An exception type that is not a documented refusal.</summary>
public sealed record SltEngineCrashed(string Stage, string ExceptionType, string Message) : SltEngineOutcome;

public sealed record SltEngineHung(string Stage) : SltEngineOutcome;

/// <summary>
/// Compiles a corpus query with the AST compiler (the same <c>FastSqlEngine.Compile</c> and <c>SqliteCompiledSqlBinder.Bind</c> calls as
/// <c>GateRunner.RunAstAsync</c>, decision 10: the gate fixture is not loaded) and executes the emitted SQL on the corpus SQLite file.
/// The catalog is built from <c>PRAGMA table_info</c> of that file.
/// </summary>
/// <summary>The engine side of one scenario (a seam so the circuit breaker can be tested with a fake).</summary>
public interface ISltEngine
{
    SltEngineOutcome Run(string sql);

    void InvalidateCatalog();
}

public sealed class SltEngineRunner(SltScenario scenario, DbConnection connection, int executeTimeoutSeconds = SltEngineRunner.ExecuteTimeoutSeconds) : ISltEngine
{
    public const string Schema = "main";
    public const string FilteredTable = "t1";
    public const string FilterColumn = "a";
    public const long FilterMinExclusive = 120;
    public const int ExecuteTimeoutSeconds = 5;
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(10);

    private readonly FastSqlEngine _engine = new();
    private readonly PostgreSqlCompiledSqlBinder _binder = new();
    private readonly Policies _policies = new(scenario);
    private readonly FrozenSet<string> _consentTables = (scenario == SltScenario.RowFilter ? new HashSet<string>([FilteredTable], StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase)).ToFrozenSet(StringComparer.OrdinalIgnoreCase);
    private InMemoryTableCatalog? _catalog;

    public SltScenario Scenario => scenario;

    /// <summary>Call after a statement changed the schema.</summary>
    public void InvalidateCatalog() => _catalog = null;

    public SltEngineOutcome Run(string sql)
    {
        CompiledSql compiled;
        using (var cts = new CancellationTokenSource(CompileTimeout))
        {
            try
            {
                compiled = _engine.Compile(sql.AsMemory(), Request(), cts.Token);
            }
            catch (Exception ex)
            {
                if (cts.IsCancellationRequested)
                {
                    return new SltEngineHung("Compile");
                }

                return GateRunner.IsGoverned(ex)
                    ? new SltEngineRejected("Compile", ex.GetType().Name, ex.Message)
                    : new SltEngineCrashed("Compile", ex.GetType().Name, ex.Message);
            }
        }

        try
        {
            using var cmd = connection.CreateCommand();
            cmd.CommandTimeout = executeTimeoutSeconds;
            _binder.Bind(cmd, compiled, new Dictionary<string, object?>());
            AdaptPostgreSqlForSqlite(cmd);
            return new SltEngineRows(SltReference.Read(cmd, executeTimeoutSeconds));
        }
        catch (Exception ex) when (ex is SecurityException or GovernedSqlException)
        {
            return new SltEngineRejected("Bind", ex.GetType().Name, ex.Message);
        }
        catch (TimeoutException)
        {
            return new SltEngineHung("Execute");
        }
        catch (SqliteException ex)
        {
            return new SltEngineRejected("Execute", ex.GetType().Name, ex.Message);
        }
        catch (Exception ex)
        {
            return new SltEngineCrashed("Execute", ex.GetType().Name, ex.Message);
        }
    }

    /// <summary>
    /// Adapts a PostgreSQL-bound command for the SQLite execution harness: names positional parameters (<c>1..n</c> for <c>$1..$n</c>)
    /// and rewrites unquoted <c>CROSS JOIN</c> to comma joins so SQLite's query planner can reorder multi-way joins instead of forcing
    /// left-to-right Cartesian order.
    /// </summary>
    internal static void AdaptPostgreSqlForSqlite(DbCommand cmd)
    {
        for (var i = 0; i < cmd.Parameters.Count; i++)
        {
            cmd.Parameters[i].ParameterName = (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        cmd.CommandText = ReplaceUnquotedCrossJoin(cmd.CommandText);
    }

    internal static string ReplaceUnquotedCrossJoin(string sql)
    {
        if (!sql.Contains("CROSS", StringComparison.OrdinalIgnoreCase))
        {
            return sql;
        }

        var sb = new System.Text.StringBuilder(sql.Length);
        var inSingleQuote = false;
        var inDoubleQuote = false;
        var i = 0;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (inSingleQuote)
            {
                sb.Append(c);
                if (c == '\'' && i + 1 < sql.Length && sql[i + 1] == '\'')
                {
                    sb.Append('\'');
                    i += 2;
                    continue;
                }

                if (c == '\'')
                {
                    inSingleQuote = false;
                }

                i++;
                continue;
            }

            if (inDoubleQuote)
            {
                sb.Append(c);
                if (c == '"' && i + 1 < sql.Length && sql[i + 1] == '"')
                {
                    sb.Append('"');
                    i += 2;
                    continue;
                }

                if (c == '"')
                {
                    inDoubleQuote = false;
                }

                i++;
                continue;
            }

            if (c == '\'')
            {
                inSingleQuote = true;
                sb.Append(c);
                i++;
                continue;
            }

            if (c == '"')
            {
                inDoubleQuote = true;
                sb.Append(c);
                i++;
                continue;
            }

            if (TryMatchCrossJoin(sql, i, out var nextIndex))
            {
                sb.Append(',');
                i = nextIndex;
                continue;
            }

            sb.Append(c);
            i++;
        }

        return sb.ToString();
    }

    private static bool TryMatchCrossJoin(string sql, int index, out int nextIndex)
    {
        nextIndex = index;
        if (index > 0 && IsIdentChar(sql[index - 1]))
        {
            return false;
        }

        if (index + 5 > sql.Length || !sql.AsSpan(index, 5).Equals("CROSS", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var pos = index + 5;
        if (pos >= sql.Length || !char.IsWhiteSpace(sql[pos]))
        {
            return false;
        }

        while (pos < sql.Length && char.IsWhiteSpace(sql[pos]))
        {
            pos++;
        }

        if (pos + 4 > sql.Length || !sql.AsSpan(pos, 4).Equals("JOIN", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var end = pos + 4;
        if (end < sql.Length && IsIdentChar(sql[end]))
        {
            return false;
        }

        nextIndex = end;
        return true;
    }

    private static bool IsIdentChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private CompileRequest Request()
    {
        return new CompileRequest
        {
            TargetDialect = TargetSqlDialect.PostgreSql,
            TokenGuards = SqlTokenSecurityOptions.ForDialect(TargetSqlDialect.PostgreSql),
            Statements = StatementPermissions.ReadOnly,
            AllowedFunctions = SqlFunctionAllowlists.Build(TargetSqlDialect.PostgreSql, null),
            Policy = new GovernancePolicy
            {
                RowFilters = _policies,
                Masks = _policies,
                Catalog = _catalog ??= BuildCatalog(),
                Tenant = new TenantBinding("__autheris_tenant", "sim", SqlParameterType.String),
                TablesWithConsentRowFilter = _consentTables,
                TablesWithMaskedColumns = FrozenSet<string>.Empty,
            },
        };
    }

    private InMemoryTableCatalog BuildCatalog()
    {
        var tables = new List<string>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                tables.Add(reader.GetString(0));
            }
        }

        var entries = new List<TableCatalogEntry>();
        foreach (var table in tables)
        {
            var columns = new List<CatalogColumn>();
            using var cmd = connection.CreateCommand();
            cmd.CommandText = $"PRAGMA table_info(\"{table.Replace("\"", "\"\"", StringComparison.Ordinal)}\")";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(new CatalogColumn(reader.GetString(1), reader.GetString(2)));
            }

            entries.Add(new TableCatalogEntry(new TableIdentity(Schema, table), [.. columns], null, 1));
        }

        return new InMemoryTableCatalog(entries, Schema);
    }

    private sealed class Policies(SltScenario scenario) : IPolicyPredicateProvider, IColumnMaskProvider
    {
        private static bool IsFiltered(TableIdentity table) =>
            string.Equals(table.Table, FilteredTable, StringComparison.OrdinalIgnoreCase);

        public bool ShouldApplyPolicy(TableIdentity table) => scenario == SltScenario.RowFilter && IsFiltered(table);

        public PolicyPredicate GetPredicate(TableIdentity table) => ShouldApplyPolicy(table)
            ? PolicyPredicate.Create(
                new BinaryExpression(
                    new ColumnReference(new SqlQualifiedName([new SqlIdentifier(FilterColumn, true)])),
                    BinaryOperator.GreaterThan,
                    new PolicyParameterExpression("__pol_min", SqlParameterType.Int64)),
                new Dictionary<string, PolicyValue> { ["__pol_min"] = new(FilterMinExclusive, SqlParameterType.Int64) })
            : PolicyPredicate.DenyAll;

        public bool HasMask(TableIdentity table, string column) => false;

        public MaskSpec GetMask(TableIdentity table, string column) =>
            throw new InvalidOperationException("The corpus scenarios define no masks.");
    }
}
