using System.Data.Common;

namespace Autheris.Infrastructure.Persistence.Catalog;

/// <summary>Helpers shared by the metadata readers. Every name reaches the database as a bound parameter (SEC-X1-09).</summary>
internal static class CatalogQuery
{
    public static void Bind(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }
}
