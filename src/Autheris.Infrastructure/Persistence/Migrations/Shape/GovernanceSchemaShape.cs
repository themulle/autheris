namespace Autheris.Infrastructure.Persistence.Migrations.Shape;

/// <summary>A column of the expected or the actual governance schema. <see cref="Family"/> is the engine-neutral type family.</summary>
internal sealed record SchemaColumn(string Name, string Type, string Family);

/// <summary>An explicitly named index (auto-named constraint indexes are not part of the shape).</summary>
internal sealed record SchemaIndex(string Name, bool Unique, IReadOnlyList<string> Columns);

/// <summary>A table with its columns (catalog order), ordered primary key columns and named indexes.</summary>
internal sealed record SchemaTable(
    string Name,
    IReadOnlyList<SchemaColumn> Columns,
    IReadOnlyList<string> PrimaryKey,
    IReadOnlyList<SchemaIndex> Indexes);

/// <summary>The shape of a governance schema, either the embedded snapshot of the baseline or the one read from a database.</summary>
internal sealed record GovernanceSchemaShape(string Engine, IReadOnlyList<SchemaTable> Tables);

/// <summary>Type families that make column types comparable without depending on length, nullability or defaults (plan D-3).</summary>
internal static class SchemaTypeFamily
{
    public const string String = "string";
    public const string Integer = "integer";
    public const string Decimal = "decimal";
    public const string Float = "float";
    public const string Boolean = "boolean";
    public const string DateTime = "datetime";
    public const string Binary = "binary";
    public const string Json = "json";
    public const string Other = "other";
}

public enum SchemaDeviationKind
{
    MissingTable,
    MissingColumn,
    WrongType,
    WrongPrimaryKey,
    MissingIndex,
    WrongIndex
}

/// <summary>One difference between the expected and the actual governance schema. Carries identifiers only, never data.</summary>
public sealed record SchemaDeviation(SchemaDeviationKind Kind, string Table, string? Name, string Expected, string? Found);
