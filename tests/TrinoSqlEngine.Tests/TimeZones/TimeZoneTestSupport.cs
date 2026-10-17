namespace TrinoSqlEngine.Tests.TimeZones;

using System.Collections.Immutable;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using TrinoSqlEngine.Tests.Compiler;
using Xunit;

/// <summary>Compile helpers for the time zone tests (REQ-SQL-130): a tenant-scoped <c>dbo.Events</c> table with typed timestamp columns per dialect.</summary>
internal static class TimeZoneTestSupport
{
    public static readonly TableIdentity Events = new("dbo", "Events");
    public static readonly TableIdentity Other = new("dbo", "Other");

    public static readonly TargetSqlDialect[] Dialects = [TargetSqlDialect.SqlServer, TargetSqlDialect.PostgreSql, TargetSqlDialect.DuckDb];

    public static IEnumerable<object[]> DialectData() => Dialects.Select(d => new object[] { d });

    private static readonly FastSqlEngine Engine = new();

    /// <summary>Id, TenantId, OccurredAt (plain), OccurredAtTz (offset), OccurredOn (date), Label (text), Version (SQL Server rowversion spelling).</summary>
    public static InMemoryTableCatalog Catalog(TargetSqlDialect dialect)
    {
        var (i, t, plain, offset, date, text) = dialect == TargetSqlDialect.SqlServer
            ? ("int", "nvarchar(64)", "datetime2(3)", "datetimeoffset(7)", "date", "nvarchar(100)")
            : ("integer", "varchar", "timestamp", "timestamptz", "date", "varchar");
        var columns = ImmutableArray.Create(
            new CatalogColumn("Id", i), new CatalogColumn("TenantId", t), new CatalogColumn("OccurredAt", plain), new CatalogColumn("OccurredAtTz", offset),
            new CatalogColumn("OccurredOn", date), new CatalogColumn("Label", text), new CatalogColumn("Version", dialect == TargetSqlDialect.SqlServer ? "timestamp" : plain));
        return new InMemoryTableCatalog(
        [
            new TableCatalogEntry(Events, columns, "TenantId", 1),
            new TableCatalogEntry(Other, columns, "TenantId", 1)
        ], "dbo");
    }

    public static CompileRequest Request(
        TargetSqlDialect dialect,
        bool available = false,
        DictMaskProvider? masks = null,
        ClientListShapes? lists = null,
        StatementPermissions statements = StatementPermissions.ReadOnly)
    {
        var filters = new DictPolicyProvider();
        filters.NoPolicy.Add(Events);
        filters.NoPolicy.Add(Other);
        return new CompileRequest
        {
            TargetDialect = dialect,
            AllowExperimentalDialect = true,
            TokenGuards = SqlTokenSecurityOptions.Strict,
            Statements = statements,
            ClientLists = lists ?? ClientListShapes.Empty,
            TimeZoneConversionAvailable = available,
            Policy = new GovernancePolicy
            {
                RowFilters = filters,
                Masks = masks ?? new DictMaskProvider(),
                Catalog = Catalog(dialect),
                Tenant = new TenantBinding("__autheris_tenant", "acme", SqlParameterType.String)
            }
        };
    }

    public static CompiledSql Compile(TargetSqlDialect dialect, string sql, bool available = true, DictMaskProvider? masks = null, ClientListShapes? lists = null,
        StatementPermissions statements = StatementPermissions.ReadOnly) =>
        Engine.Compile(sql.AsMemory(), Request(dialect, available, masks, lists, statements), CancellationToken.None);

    /// <summary>The error code of the refusal, or fails the test when the statement compiles or throws something else.</summary>
    public static string Rejected(TargetSqlDialect dialect, string sql, bool available = true, DictMaskProvider? masks = null)
    {
        var ex = Assert.Throws<SqlTimeZoneException>(() => Compile(dialect, sql, available, masks));
        Assert.Equal(GovernedSqlErrorCodes.FixedMessage(ex.Code), ex.Message);
        return ex.Code;
    }
}
