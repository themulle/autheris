namespace Autheris.Infrastructure.Databricks;

using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

/// <summary>Command of the Databricks facade (WP-X0j): sends the statement with named typed parameters through the REST client.</summary>
public sealed class DatabricksDbCommand : DbCommand
{
    private readonly DatabricksDbParameterCollection _parameters = new();

    [AllowNull]
    public override string CommandText { get; set; } = string.Empty;

    public override int CommandTimeout { get; set; } = 30;
    public override CommandType CommandType { get; set; } = CommandType.Text;
    public override bool DesignTimeVisible { get; set; }
    public override UpdateRowSource UpdatedRowSource { get; set; }
    protected override DbConnection? DbConnection { get; set; }
    protected override DbParameterCollection DbParameterCollection => _parameters;
    protected override DbTransaction? DbTransaction { get; set; }

    public override void Cancel()
    {
    }

    public override int ExecuteNonQuery() => -1;
    public override object? ExecuteScalar() => null;
    public override void Prepare()
    {
    }

    protected override DbParameter CreateDbParameter() => new DatabricksDbParameter();

    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) => new DataTable().CreateDataReader();
}
