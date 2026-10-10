namespace TrinoSqlEngine.Ast.Emit;

using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Globalization;
using System.Security;

/// <summary>Binds <see cref="CompiledSql"/> to a provider command. One implementation per ADO.NET provider family.</summary>
public interface ICompiledSqlBinder
{
    bool CanBind(TargetSqlDialect dialect);

    /// <summary>
    /// Sets <see cref="DbCommand.CommandText"/> from <see cref="CompiledSql.Sql"/> (the only text that may reach the
    /// command) and adds one typed parameter per <see cref="BoundParameter"/>, by name (INV-13).
    /// </summary>
    void Bind(DbCommand command, CompiledSql compiled, IReadOnlyDictionary<string, object?> clientParameterValues);
}

/// <summary>A client named parameter referenced by the SQL has no value in the request.</summary>
public sealed class MissingClientParameterException : SecurityException
{
    public MissingClientParameterException(string name)
        : base($"No value was supplied for client parameter '{name}'.")
    {
    }
}

/// <summary>
/// Shared binder logic for ADO.NET providers that bind by parameter name. Written against <see cref="DbCommand"/> so the
/// engine needs no provider reference. Only client named parameters read client values, and only by their own source name
/// (SEC-ADG-19); every other parameter carries the gateway-bound value of the compiled SQL.
/// </summary>
public abstract class DbCommandCompiledSqlBinder : ICompiledSqlBinder
{
    private const int DefaultStringSize = 4000;

    protected abstract TargetSqlDialect Dialect { get; }

    /// <summary>Fixed string and binary sizes keep one cached plan per statement (SQL Server).</summary>
    protected virtual bool SetsSizesAndPrecision => false;

    protected virtual DbType TimestampDbType => DbType.DateTime;

    /// <summary>The provider passes strings as C strings and would truncate at an embedded NUL (fail closed instead).</summary>
    protected virtual bool RejectsEmbeddedNul => false;

    /// <summary>The provider-side parameter name. Positional providers (Npgsql with <c>$n</c>) use an empty name.</summary>
    protected virtual string ParameterNameFor(BoundParameter parameter) => parameter.Name;

    /// <summary>Provider-specific command setup before any parameter is added (for example Oracle <c>BindByName</c>).</summary>
    protected virtual void PrepareCommand(DbCommand command)
    {
    }

    /// <summary>Provider-specific rejection of a value that would be bound (fail closed).</summary>
    protected virtual void ValidateValue(BoundParameter parameter, object? value)
    {
    }

    /// <summary>The provider has no boolean parameter type (Oracle before 23ai): booleans are bound as 0/1.</summary>
    protected virtual bool BooleanAsInteger => false;

    /// <summary>Provider enum name (for example Oracle <c>OracleDbType.TimeStamp</c>) that keeps sub-second precision of timestamps.</summary>
    protected virtual string? ProviderTimestampTypeName => null;

    public bool CanBind(TargetSqlDialect dialect) => dialect == Dialect;

    public void Bind(DbCommand command, CompiledSql compiled, IReadOnlyDictionary<string, object?> clientParameterValues)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(compiled);
        ArgumentNullException.ThrowIfNull(clientParameterValues);

        if (compiled.Dialect != Dialect)
        {
            throw new InvalidOperationException("The compiled SQL was generated for another dialect.");
        }

        if (command.Parameters.Count > 0)
        {
            throw new InvalidOperationException("The command already has parameters; the binder owns all parameters of a governed command.");
        }

        PrepareCommand(command);
        command.CommandText = compiled.Sql;
        command.CommandType = CommandType.Text;

        foreach (var bound in compiled.Parameters)
        {
            object? value = bound.Value;
            var type = bound.Type;

            // Only client named parameters read client values, and only by their own source name (SEC-ADG-19).
            if (bound.Origin == ParameterOrigin.ClientNamed)
            {
                if (bound.SourceName is null || !clientParameterValues.TryGetValue(bound.SourceName, out value))
                {
                    throw new MissingClientParameterException(bound.SourceName ?? "?");
                }

                type = InferType(value);
            }
            else if (bound.Origin == ParameterOrigin.ClientPositional)
            {
                throw new SecurityException("Positional client parameters are not supported.");
            }

            ValidateValue(bound, value);
            if (RejectsEmbeddedNul && value is string text && text.Contains('\0', StringComparison.Ordinal))
            {
                throw new SecurityException("A string parameter contains an embedded NUL character, which the provider would truncate.");
            }

            var parameter = command.CreateParameter();
            parameter.ParameterName = ParameterNameFor(bound);
            Configure(parameter, type, value);
            command.Parameters.Add(parameter);
        }
    }

    private static SqlParameterType InferType(object? value) => value switch
    {
        null or DBNull => SqlParameterType.Null,
        string => SqlParameterType.String,
        int or short or byte or sbyte or ushort => SqlParameterType.Int32,
        long or uint => SqlParameterType.Int64,
        decimal => SqlParameterType.Decimal,
        double or float => SqlParameterType.Double,
        bool => SqlParameterType.Boolean,
        DateOnly => SqlParameterType.Date,
        TimeOnly or TimeSpan => SqlParameterType.Time,
        DateTime => SqlParameterType.Timestamp,
        DateTimeOffset => SqlParameterType.TimestampTz,
        byte[] => SqlParameterType.Binary,
        _ => throw new SecurityException($"Unsupported client parameter value type '{value.GetType().Name}'.")
    };

    private static bool TrySetProviderType(DbParameter parameter, string? enumName)
    {
        if (enumName is null) return false;
        var property = parameter.GetType().GetProperty("OracleDbType");
        if (property is null || !property.PropertyType.IsEnum || !Enum.TryParse(property.PropertyType, enumName, out var value)) return false;
        property.SetValue(parameter, value);
        return true;
    }

    private void Configure(DbParameter parameter, SqlParameterType type, object? value)
    {
        if (value is null or DBNull || type == SqlParameterType.Null)
        {
            parameter.DbType = DbType.String;
            parameter.Value = DBNull.Value;
            return;
        }

        switch (type)
        {
            case SqlParameterType.String:
                string text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
                parameter.DbType = DbType.String; // nvarchar
                // A fixed size keeps one cached plan per statement instead of one per value length.
                if (SetsSizesAndPrecision) parameter.Size = text.Length <= DefaultStringSize ? DefaultStringSize : -1;
                parameter.Value = text;
                break;
            case SqlParameterType.Int32:
                parameter.DbType = DbType.Int32;
                parameter.Value = Convert.ToInt32(value, CultureInfo.InvariantCulture);
                break;
            case SqlParameterType.Int64:
                parameter.DbType = DbType.Int64;
                parameter.Value = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                break;
            case SqlParameterType.Decimal:
                decimal d = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
                parameter.DbType = DbType.Decimal;
                if (SetsSizesAndPrecision && parameter is IDbDataParameter dbData)
                {
                    dbData.Precision = 38;
                    dbData.Scale = (byte)((decimal.GetBits(d)[3] >> 16) & 0xFF);
                }

                parameter.Value = d;
                break;
            case SqlParameterType.Double:
                parameter.DbType = DbType.Double;
                parameter.Value = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                break;
            case SqlParameterType.Boolean:
                if (BooleanAsInteger)
                {
                    parameter.DbType = DbType.Int32;
                    parameter.Value = Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? 1 : 0;
                    break;
                }

                parameter.DbType = DbType.Boolean;
                parameter.Value = Convert.ToBoolean(value, CultureInfo.InvariantCulture);
                break;
            case SqlParameterType.Date:
                parameter.DbType = DbType.Date;
                parameter.Value = value is DateOnly dateOnly ? dateOnly.ToDateTime(TimeOnly.MinValue) : Convert.ToDateTime(value, CultureInfo.InvariantCulture);
                break;
            case SqlParameterType.Timestamp:
                if (!TrySetProviderType(parameter, ProviderTimestampTypeName))
                {
                    parameter.DbType = TimestampDbType;
                }

                parameter.Value = Convert.ToDateTime(value, CultureInfo.InvariantCulture);
                break;
            case SqlParameterType.TimestampTz:
                parameter.DbType = DbType.DateTimeOffset;
                parameter.Value = value is DateTimeOffset dto ? dto : new DateTimeOffset(Convert.ToDateTime(value, CultureInfo.InvariantCulture));
                break;
            case SqlParameterType.Time:
                parameter.DbType = DbType.Time;
                parameter.Value = value is TimeOnly timeOnly ? timeOnly.ToTimeSpan() : (TimeSpan)value;
                break;
            case SqlParameterType.Binary:
                var bytes = (byte[])value;
                parameter.DbType = DbType.Binary;
                if (SetsSizesAndPrecision) parameter.Size = bytes.Length <= 8000 ? 8000 : -1;
                parameter.Value = bytes;
                break;
            default:
                throw new SecurityException($"Unsupported parameter type '{type}'.");
        }
    }
}

/// <summary>SQL Server binder (<c>Microsoft.Data.SqlClient</c> binds by name: <c>@p0</c>, <c>@p1</c> ...).</summary>
public sealed class SqlServerCompiledSqlBinder : DbCommandCompiledSqlBinder
{
    protected override TargetSqlDialect Dialect => TargetSqlDialect.SqlServer;
    protected override bool SetsSizesAndPrecision => true;
    protected override DbType TimestampDbType => DbType.DateTime2;
}

/// <summary>
/// DuckDB binder (<c>DuckDB.NET</c>): markers are <c>$1</c>, <c>$2</c> ... and the parameters are named <c>1</c>, <c>2</c> ...
/// </summary>
public sealed class DuckDbCompiledSqlBinder : DbCommandCompiledSqlBinder
{
    protected override TargetSqlDialect Dialect => TargetSqlDialect.DuckDb;
    protected override bool RejectsEmbeddedNul => true;
}

/// <summary>
/// PostgreSQL binder (<c>Npgsql</c>): markers are <c>$1</c>, <c>$2</c> ... and the parameters are positional (unnamed, added in
/// ordinal order). PostgreSQL text cannot hold NUL, so a string with an embedded NUL is rejected instead of failing late.
/// </summary>
public sealed class PostgreSqlCompiledSqlBinder : DbCommandCompiledSqlBinder
{
    protected override TargetSqlDialect Dialect => TargetSqlDialect.PostgreSql;
    protected override bool RejectsEmbeddedNul => true;
    protected override DbType TimestampDbType => DbType.DateTime2;   // Npgsql: timestamp without time zone (DbType.DateTime is timestamptz)
    protected override string ParameterNameFor(BoundParameter parameter) => string.Empty;
}

/// <summary>
/// Oracle binder (<c>Oracle.ManagedDataAccess</c>). ODP.NET binds by POSITION unless <c>BindByName</c> is true; with reused
/// markers and out-of-order parameters positional binding would bind a tenant value to a user slot (SEC-ADG-03). The binder sets
/// <c>BindByName = true</c> and verifies it; a command without that property is rejected. An empty tenant or policy string is
/// rejected because Oracle treats it as NULL (SEC-ADG-17). Markers are <c>:p1..:pN</c>, parameter names <c>p1..pN</c>.
/// </summary>
public sealed class OracleCompiledSqlBinder : DbCommandCompiledSqlBinder
{
    protected override TargetSqlDialect Dialect => TargetSqlDialect.Oracle;
    protected override DbType TimestampDbType => DbType.DateTime;
    protected override string? ProviderTimestampTypeName => "TimeStamp";
    protected override bool BooleanAsInteger => true;

    protected override void PrepareCommand(DbCommand command)
    {
        var property = command.GetType().GetProperty("BindByName");
        if (property is null || property.PropertyType != typeof(bool) || !property.CanWrite || !property.CanRead)
        {
            throw new SecurityException("The Oracle command does not expose BindByName; positional binding is not permitted.");
        }

        property.SetValue(command, true);
        if (!(bool)property.GetValue(command)!)
        {
            throw new SecurityException("BindByName could not be enabled on the Oracle command.");
        }
    }

    protected override void ValidateValue(BoundParameter parameter, object? value)
    {
        if (parameter.Origin is ParameterOrigin.Tenant or ParameterOrigin.Policy && value is string { Length: 0 })
        {
            throw new SecurityException("An empty tenant or policy string is rejected on Oracle (it would be NULL).");
        }
    }
}
