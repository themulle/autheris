namespace Autheris.Tests.SqlCompilerGate.Parity;

using Autheris.Domain.Common;
using Autheris.Tests.SqlCompilerGate.Model;

/// <summary>The text of a producer plus the values it binds (marker keyed, for example <c>@__gql_rls_0</c>). Legacy text binds nothing.</summary>
public sealed record FilterText(string Sql, IReadOnlyDictionary<string, object?> Parameters)
{
    public static readonly IReadOnlyDictionary<string, object?> NoParameters = new Dictionary<string, object?>();

    public static FilterText Plain(string sql) => new(sql, NoParameters);
}

/// <summary>The database a parity case runs on: its dialect and the schema that the catalog identifiers carry.</summary>
public sealed record ParityContext(GateDialect Dialect, string Schema)
{
    public DatabaseDialect Database => Dialect switch
    {
        GateDialect.Sqlite => DatabaseDialect.Sqlite,
        GateDialect.PostgreSql => DatabaseDialect.PostgreSql,
        GateDialect.SqlServer => DatabaseDialect.SqlServer,
        GateDialect.Oracle => DatabaseDialect.Oracle,
        _ => throw new ArgumentOutOfRangeException(nameof(Dialect))
    };
}

public enum ProducerKind
{
    ConsentFilter,
    CorrelatedFilter,
    CrossSourceSetFilter,
    VirtualFilterSql,
    VirtualFilterStructured,
    ColumnMask
}

/// <summary>
/// One §7.3 producer case: the legacy string producer and the IR renderer for the same decision on the same dialect. <see cref="Reference"/>
/// is the independent expectation (which fixture rows of tenant A the decision admits), written from the plan semantics and not derived
/// from either producer. It is null where the case has no row semantics (masks).
/// </summary>
public sealed record ParityCase(
    string Id,
    ProducerKind Kind,
    string Description,
    Func<ParityContext, ProducedPair> Produce,
    Func<RowDef, bool>? Reference);

/// <summary>The two producers' output for one case and dialect.</summary>
public sealed record ProducedPair(FilterText Legacy, FilterText Ir);
