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
public sealed class SltEngineRunner(SltScenario scenario, DbConnection connection)
{
    public const string Schema = "main";
    public const string FilteredTable = "t1";
    public const string FilterColumn = "a";
    public const long FilterMinExclusive = 120;
    public const int ExecuteTimeoutSeconds = 5;
    private static readonly TimeSpan CompileTimeout = TimeSpan.FromSeconds(10);

    private readonly FastSqlEngine _engine = new();
    private readonly SqliteCompiledSqlBinder _binder = new();
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
            cmd.CommandTimeout = ExecuteTimeoutSeconds;
            _binder.Bind(cmd, compiled, new Dictionary<string, object?>());
            return new SltEngineRows(SltReference.Read(cmd, ExecuteTimeoutSeconds));
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

    private CompileRequest Request()
    {
        var policies = new Policies(scenario);
        return new CompileRequest
        {
            TargetDialect = TargetSqlDialect.Sqlite,
            TokenGuards = SqlTokenSecurityOptions.ForDialect(TargetSqlDialect.Sqlite),
            Statements = StatementPermissions.ReadOnly,
            AllowedFunctions = SqlFunctionAllowlists.Build(TargetSqlDialect.Sqlite, null),
            Policy = new GovernancePolicy
            {
                RowFilters = policies,
                Masks = policies,
                Catalog = _catalog ??= BuildCatalog(),
                Tenant = new TenantBinding("__autheris_tenant", "sim", SqlParameterType.String),
                TablesWithConsentRowFilter = (scenario == SltScenario.RowFilter ? new HashSet<string>([FilteredTable], StringComparer.OrdinalIgnoreCase) : new HashSet<string>(StringComparer.OrdinalIgnoreCase)).ToFrozenSet(StringComparer.OrdinalIgnoreCase),
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
