namespace TrinoSqlEngine.Ast.Emit;

/// <summary>Logical type of a bound parameter. The binder maps it to the provider type.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1720:Identifiers should not contain type names")]
public enum SqlParameterType
{
    String,
    Int32,
    Int64,
    Decimal,
    Double,
    Boolean,
    Date,
    Timestamp,
    TimestampTz,
    Time,
    Binary,
    Null
}
