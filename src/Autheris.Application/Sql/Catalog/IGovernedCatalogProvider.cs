using Autheris.Domain.Common;
using TrinoSqlEngine.Governance;

namespace Autheris.Application.Sql.Catalog;

/// <summary>
/// CR-ADG-47: builds the catalog the AST compiler trusts from database metadata instead of admin-entered governance text.
/// The exposed columns are the governance columns intersected with the database columns; native type and collation come
/// from the database. A table that cannot be verified is absent from the result, so the compiler denies it (fail closed).
/// </summary>
public interface IGovernedCatalogProvider
{
    ValueTask<ITableCatalog> GetCatalogAsync(string dataSourceName, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct);
}
