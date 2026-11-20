namespace Autheris.Tests.Unit.Sql;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Catalog.Discovery;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Semantic;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Security;

/// <summary>
/// Allow-all discovery and PDP fakes for unit tests that exercise <see cref="TrinoMetadataQueryHandler"/>
/// outside the discovery/PDP layer itself.
/// </summary>
internal static class TrinoMetadataVisibilityFakes
{
    public static TrinoMetadataVisibility AllowAll(params TableMetadata[] tables) =>
        AllowAll((IEnumerable<TableMetadata>)tables);

    public static TrinoMetadataVisibility AllowAll(IEnumerable<TableMetadata> tables)
    {
        var list = tables.ToList();
        return new TrinoMetadataVisibility(
            new FakeDiscoveryService(() => list),
            new FakeAccessDecisionPoint());
    }

    public static TrinoMetadataVisibility AllowAll(ITableMetadataRepository repository)
    {
        ArgumentNullException.ThrowIfNull(repository);
        return new TrinoMetadataVisibility(
            new RepositoryDiscoveryService(repository),
            new FakeAccessDecisionPoint());
    }

    private static DiscoveryObject ToDiscoveryObject(TableMetadata table) => new(
        SemanticVirtualTables.IsVirtualTable(table) ? DiscoveryObjectKind.View : DiscoveryObjectKind.Table,
        table.Identifier.Domain,
        table.Identifier.Schema,
        table.Identifier.TableName,
        table.Table.DisplayName,
        table.Table.Description,
        "Public",
        [],
        [],
        DiscoveryAccessState.Granted,
        null,
        table.Columns
            .Select(static c => new DiscoveryColumn(
                c.ColumnName,
                c.DisplayName,
                string.IsNullOrWhiteSpace(c.DataType) ? "varchar" : c.DataType,
                c.Description,
                false,
                false,
                DiscoveryColumnState.Clear,
                null))
            .ToList(),
        null,
        false,
        false);

    private static DiscoveryPage Slice(IReadOnlyList<TableMetadata> tables, DiscoveryQuery query)
    {
        var active = tables.Where(static t => t.Table.IsActive).Select(ToDiscoveryObject).ToList();
        var start = Math.Clamp(query.After, 0, active.Count);
        var take = Math.Clamp(query.First, 1, DiscoveryService.MaxPageSize);
        var items = active.Skip(start).Take(take).ToList();
        var hasNext = start + items.Count < active.Count;
        return new DiscoveryPage(items, active.Count, hasNext);
    }

    private sealed class FakeDiscoveryService(Func<IReadOnlyList<TableMetadata>> load) : IDiscoveryService
    {
        public ValueTask<DiscoveryPage> ListAsync(DiscoveryQuery query, CatalogCaller caller, CancellationToken ct = default) =>
            new(Slice(load(), query));

        public ValueTask<DiscoveryKeysetPage> ListKeysetAsync(DiscoveryKeysetQuery query, CatalogCaller caller, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<DiscoveryFacets> FacetsAsync(DiscoveryAccessState? access, CatalogCaller caller, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<DiscoveryObject?> GetAsync(TableIdentifier id, CatalogCaller caller, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<DiscoveryObject>> SearchAsync(string text, DiscoverySearchOptions options, CatalogCaller caller, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class RepositoryDiscoveryService(ITableMetadataRepository repository) : IDiscoveryService
    {
        public async ValueTask<DiscoveryPage> ListAsync(DiscoveryQuery query, CatalogCaller caller, CancellationToken ct = default)
        {
            var active = await CatalogTablePageReader.ReadAllAsync(
                repository,
                new CatalogTableQuery(IncludeInactive: false),
                static t => t.Table.IsActive,
                ct: ct).ConfigureAwait(false);
            return Slice(active, query);
        }

        public ValueTask<DiscoveryObject?> GetAsync(TableIdentifier id, CatalogCaller caller, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<DiscoveryKeysetPage> ListKeysetAsync(DiscoveryKeysetQuery query, CatalogCaller caller, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<DiscoveryFacets> FacetsAsync(DiscoveryAccessState? access, CatalogCaller caller, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<IReadOnlyList<DiscoveryObject>> SearchAsync(string text, DiscoverySearchOptions options, CatalogCaller caller, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeAccessDecisionPoint : IAccessDecisionPoint
    {
        public ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(
            SecurityPrincipalContext subject,
            IReadOnlyList<TableMetadata> objects,
            AccessOperation operation,
            AccessTransport transport,
            CancellationToken ct = default)
        {
            IReadOnlyList<TableAccessDecision> decisions = objects
                .Select(static t => new TableAccessDecision(
                    t.Identifier,
                    true,
                    t.Columns.ToDictionary(static c => c.ColumnName, static _ => ColumnAccessLevel.Clear, StringComparer.OrdinalIgnoreCase),
                    null,
                    []))
                .ToList();
            return new ValueTask<IReadOnlyList<TableAccessDecision>>(decisions);
        }

        public ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
