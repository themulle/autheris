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
    ValueTask<ITableCatalog> GetCatalogAsync(GovernedDataSource dataSource, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct);
}

/// <summary>
/// The two names of one WebSQL data source (#668): the logical name the caller requested (catalog domain, allowlists) and the key of the
/// <c>DataSources:Connections</c> entry it resolves to (connection, dialect, fingerprint). A catalog entry does not record its connection:
/// its <c>SourceName</c> is the logical name and the connection follows from <c>WebSql:DataSourceMappings</c>. An unmapped name is its own
/// connection key (the implicit conversion).
/// </summary>
public sealed record GovernedDataSource(string LogicalName, string ConnectionKey)
{
    public static implicit operator GovernedDataSource(string name) => new(name, name);
}
