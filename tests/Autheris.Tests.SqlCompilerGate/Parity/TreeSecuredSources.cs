namespace Autheris.Tests.SqlCompilerGate.Parity;

using System.Collections.Immutable;
using System.Security.Claims;
using Autheris.Application.Interfaces;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.SqlCompilerGate.Databases;
using Autheris.Tests.SqlCompilerGate.Harness;
using Autheris.Tests.SqlCompilerGate.Model;
using Microsoft.Extensions.Options;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;

/// <summary>
/// SQLA-06 follow-up: the secured source of the tree consumer, built through the real <see cref="TreeSecuredSourceBuilder"/>, the real
/// <see cref="GovernancePolicyFactory"/> and the real engine over the gate catalog (every gate table, tenant column <c>tenant_id</c>), and the
/// WebSQL reference through the same factory and engine.
/// </summary>
internal sealed class GateTreeSources
{
    private readonly FastSqlEngine _engine = new();
    private readonly GovernancePolicyFactory _factory;

    public GateTreeSources(ParityContext ctx)
    {
        Context = ctx;
        var entries = GateModel.Tables.Select(t => new TableCatalogEntry(
            new TableIdentity(ctx.Schema, t.Name),
            [.. t.Columns.Select(c => new CatalogColumn(c.Name, ParityData.DataTypeOf(c)))],
            GateModel.TenantColumn,
            1)).ToList();
        _factory = new GovernancePolicyFactory(
            new GateCatalogProvider(new InMemoryTableCatalog(entries, ctx.Schema)),
            new GateMetadataRepository(ctx),
            Options.Create(new GatewayOptions()));
        Builder = new TreeSecuredSourceBuilder(_factory, _engine);
    }

    public ParityContext Context { get; }

    public TreeSecuredSourceBuilder Builder { get; }

    public Task<TreeSecuredSource> BuildAsync(TableMetadata metadata, TableAccessDecision decision, string tenant) =>
        Builder.BuildAsync(
            new ResolvedTableAccess(metadata, decision, new TenantId(tenant), new Sid("S-1-5-21-GATE"), new ClaimsPrincipal()),
            ParityData.Source,
            Context.Database,
            CancellationToken.None);

    /// <summary>The WebSQL path: <c>SELECT id FROM orders</c> compiled with the same factory, engine and decision, bound through the binder.</summary>
    public async Task<IReadOnlyList<long>> WebSqlOrderIdsAsync(GateDatabase database, TableMetadata metadata, TableAccessDecision decision, string tenant)
    {
        var identity = new TableIdentity(Context.Schema, "orders");
        var governed = await _factory.CreateAsync(
            new GovernedPolicyInput(
                ParityData.Source,
                new TenantId(tenant),
                new Dictionary<TableIdentity, TableAccessDecision> { [identity] = decision },
                new HashSet<TableIdentity>(),
                GovernedSubqueryStrategy.Correlated)
            {
                Metadata = new Dictionary<TableIdentity, TableMetadata> { [identity] = metadata }
            },
            CancellationToken.None);
        var target = SqlDialectMapper.ToTargetDialect(Context.Database);
        var request = CompileRequestFactory.Create(governed, target, new WebSqlOptions(), false, 0, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var compiled = _engine.Compile($"SELECT id FROM {Context.Schema}.orders ORDER BY id".AsMemory(), request, CancellationToken.None);

        await using var conn = await database.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = compiled.Sql;
        CompiledSqlBinders.For(target).Bind(cmd, compiled, new Dictionary<string, object?>());
        var ids = new List<long>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(Convert.ToInt64(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture));
        }

        return ids;
    }

    private sealed class GateCatalogProvider(ITableCatalog catalog) : IGovernedCatalogProvider
    {
        public ValueTask<ITableCatalog> GetCatalogAsync(string dataSourceName, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct) =>
            ValueTask.FromResult(catalog);
    }

    private sealed class GateMetadataRepository(ParityContext ctx) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) =>
            Task.FromResult<TableMetadata?>(ParityData.MetadataOf(ctx, table.TableName));

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<TableMetadata>>([.. GateModel.Tables.Select(t => ParityData.MetadataOf(ctx, t.Name))]);

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }
}
