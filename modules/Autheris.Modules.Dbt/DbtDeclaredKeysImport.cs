namespace Autheris.Modules.Dbt;

using Autheris.Application.Catalog.Services;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging;

/// <summary>Turns the keys a dbt model declares (including per-column ASC/DESC directions, issue #390) into a table definition through the shared import rules.</summary>
internal static class DbtDeclaredKeysImport
{
    public static TableMetadata Apply(TableMetadata metadata, DbtDeclaredKeys declared, ILogger logger)
    {
        var collector = new CatalogKeyDefinitionImport.Collector();
        if (declared.Invalid)
        {
            collector.MarkInvalid(declared.InvalidRule ?? "INVALID_KEY_DEFINITION");
        }
        else
        {
            if (declared.PrimaryKey is not null)
            {
                collector.AddPrimaryKey(declared.PrimaryKey, declared.PrimaryKeyDirections);
            }

            if (declared.DeclaredIndexes is { Count: > 0 } declaredIndexes)
            {
                foreach (var index in declaredIndexes)
                {
                    collector.AddIndex(index.Columns, index.IsUnique, index.Name, index.Directions);
                }
            }
            else
            {
                for (var i = 0; i < declared.UniqueConstraints.Count; i++)
                {
                    collector.AddUnique(declared.UniqueConstraints[i], declared.UniqueDirections is { } unique && i < unique.Count ? unique[i] : null);
                }
            }
        }

        return CatalogKeyDefinitionImport.Apply(metadata, collector, logger, "dbt");
    }
}
