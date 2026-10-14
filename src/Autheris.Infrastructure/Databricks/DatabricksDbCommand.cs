namespace Autheris.Infrastructure.Databricks;

using System.Collections.Immutable;
using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;

/// <summary>
/// Command of the Databricks facade (WP-X0j): sends the statement with named, typed parameters through the REST client. Values are
/// never inlined into the statement text (INV-13). A parameter whose type has no safe textual mapping is refused before any request
/// is sent. The command timeout comes from the data source (<c>CommandTimeoutSeconds</c>); the REST client enforces it and cancels
/// the statement on the server.
/// </summary>
public sealed class DatabricksDbCommand : DbCommand
{
    private readonly DatabricksDbParameterCollection _parameters = new();
    private CancellationTokenSource? _running;

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    /// <summary>Informational only: the client applies the data source timeout.</summary>
    public override int CommandTimeout { get; set; } = 30;

    public override CommandType CommandType { get; set; } = CommandType.Text;
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; }
    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel() => Volatile.Read(ref _running)?.Cancel();

    public override int ExecuteNonQuery() => ExecuteNonQueryAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override object? ExecuteScalar() => ExecuteScalarAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override void Prepare()
    {
    }

    public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(cancellationToken).ConfigureAwait(false);
        if (result.Rows.Count == 1 && result.Columns.Length == 1 &&
            string.Equals(result.Columns[0].Name, "num_affected_rows", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(result.Rows[0][0], NumberStyles.None, CultureInfo.InvariantCulture, out var affected))
        {
            return affected;
        }

        return -1;   // not reported: a caller with a row limit fails closed on it
    }

    public override async Task<object?> ExecuteScalarAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(cancellationToken).ConfigureAwait(false);
        return result.Rows.Count > 0 && result.Rows[0].Length > 0 ? (object?)result.Rows[0][0] ?? DBNull.Value : null;
    }

    protected override DbParameter CreateDbParameter() => new DatabricksDbParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
        ExecuteDbDataReaderAsync(behavior, CancellationToken.None).GetAwaiter().GetResult();

    protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(CommandBehavior behavior, CancellationToken cancellationToken)
    {
        var result = await RunAsync(cancellationToken).ConfigureAwait(false);
        var table = new DataTable { Locale = CultureInfo.InvariantCulture };
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in result.Columns)
        {
            var name = column.Name;
            for (var i = 1; !used.Add(name); i++)
            {
                name = $"{column.Name}_{i.ToString(CultureInfo.InvariantCulture)}";
            }

            table.Columns.Add(name, typeof(string));
        }

        foreach (var row in result.Rows)
        {
            var values = new object[table.Columns.Count];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = i < row.Length && row[i] is { } cell ? cell : DBNull.Value;
            }

            table.Rows.Add(values);
        }

        return table.CreateDataReader();
    }

    private async Task<DatabricksStatementResult> RunAsync(CancellationToken cancellationToken)
    {
        var connection = DbConnection as DatabricksDbConnection
                         ?? throw new InvalidOperationException("The command is not attached to a Databricks connection.");
        if (connection.State != ConnectionState.Open)
        {
            throw new InvalidOperationException("The Databricks connection is not open.");
        }

        if (DbTransaction is not null)
        {
            throw new NotSupportedException("Databricks has no transaction.");
        }

        var request = new DatabricksStatementRequest(
            CommandText,
            BuildParameters(),
            connection.Options.Catalog,
            connection.Options.Schema,
            TimeSpan.FromSeconds(connection.Options.WaitTimeoutSeconds),
            connection.DataSourceName);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Volatile.Write(ref _running, cts);
        try
        {
            return await connection.Client.ExecuteAsync(request, cts.Token).ConfigureAwait(false);
        }
        finally
        {
            Volatile.Write(ref _running, null);
        }
    }

    private ImmutableArray<DatabricksParameter> BuildParameters()
    {
        var builder = ImmutableArray.CreateBuilder<DatabricksParameter>(_parameters.Count);
        foreach (var parameter in _parameters.Items)
        {
            builder.Add(Map(parameter));
        }

        return builder.MoveToImmutable();
    }

    private static DatabricksParameter Map(DbParameter parameter)
    {
        var name = parameter.ParameterName;
        var value = parameter.Value;
        if (value is null or DBNull)
        {
            return new DatabricksParameter(name, null, "STRING");
        }

        return parameter.DbType switch
        {
            DbType.String or DbType.AnsiString or DbType.StringFixedLength or DbType.AnsiStringFixedLength =>
                new DatabricksParameter(name, Convert.ToString(value, CultureInfo.InvariantCulture), "STRING"),
            DbType.Int32 => new DatabricksParameter(name, Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), "INT"),
            DbType.Int64 => new DatabricksParameter(name, Convert.ToInt64(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture), "BIGINT"),
            DbType.Double => new DatabricksParameter(name, Convert.ToDouble(value, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture), "DOUBLE"),
            DbType.Boolean => new DatabricksParameter(name, Convert.ToBoolean(value, CultureInfo.InvariantCulture) ? "true" : "false", "BOOLEAN"),
            DbType.Decimal => MapDecimal(name, Convert.ToDecimal(value, CultureInfo.InvariantCulture)),
            DbType.Date => new DatabricksParameter(name, ToDateTime(value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), "DATE"),
            DbType.DateTime or DbType.DateTime2 => new DatabricksParameter(name, FormatTimestamp(ToDateTime(value)), "TIMESTAMP"),
            DbType.DateTimeOffset => new DatabricksParameter(name, FormatTimestamp(((DateTimeOffset)value).UtcDateTime), "TIMESTAMP"),
            _ => throw new NotSupportedException($"The parameter type {parameter.DbType} is not supported by the Databricks facade.")
        };
    }

    private static DatabricksParameter MapDecimal(string name, decimal value)
    {
        var text = value.ToString(CultureInfo.InvariantCulture);
        var scale = (decimal.GetBits(value)[3] >> 16) & 0xFF;
        var digits = text.Replace("-", string.Empty, StringComparison.Ordinal).Replace(".", string.Empty, StringComparison.Ordinal).TrimStart('0').Length;
        var precision = Math.Max(Math.Max(digits, scale), 1);
        return new DatabricksParameter(name, text, $"DECIMAL({precision.ToString(CultureInfo.InvariantCulture)},{scale.ToString(CultureInfo.InvariantCulture)})");
    }

    private static DateTime ToDateTime(object value) =>
        value is DateOnly dateOnly ? dateOnly.ToDateTime(TimeOnly.MinValue) : Convert.ToDateTime(value, CultureInfo.InvariantCulture);

    private static string FormatTimestamp(DateTime value) =>
        (value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value).ToString("yyyy-MM-dd HH:mm:ss.ffffff", CultureInfo.InvariantCulture);
}
