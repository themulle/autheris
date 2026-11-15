using System.Collections.Immutable;
using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Policy;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using NSubstitute;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;

namespace Autheris.Tests.Unit.GraphQL;

/// <summary>
/// SQLA-06 follow-up: the real engine, the real <see cref="GovernancePolicyFactory"/> and the real
/// <see cref="TreeSecuredSourceBuilder"/> for building and compiling secured tree sources in unit tests.
/// Real database row-parity execution runs in <c>Autheris.Tests.Integration.GraphQL</c>.
/// </summary>
internal sealed class SecuredTreeHarness
{
    public const string DataSource = "shop";

    public static readonly TableIdentifier OrdersId = new("shop", "main", "orders");
    public static readonly TableIdentifier ItemsId = new("shop", "main", "items");
    public static readonly TableIdentifier ConsentsId = new("shop", "main", "consents");
    public static readonly TableIdentifier ItemConsentsId = new("shop", "main", "item_consents");

    private static readonly PolicyPredicateRenderer Renderer = new();

    private readonly Func<TableIdentifier, TableMetadata?> _dependentMetadata;

    public SecuredTreeHarness(
        GatewayOptions? options = null,
        Func<TableIdentifier, TableMetadata?>? dependentMetadata = null,
        IEnumerable<string>? catalogTables = null)
    {
        _dependentMetadata = dependentMetadata ?? (id => DependentMetadata(id));
        var names = catalogTables?.ToHashSet(StringComparer.OrdinalIgnoreCase)
            ?? new HashSet<string>(["orders", "consents", "items", "item_consents"], StringComparer.OrdinalIgnoreCase);
        var entries = new List<TableCatalogEntry>();
        foreach (var name in names)
        {
            entries.Add(Entry(name));
        }

        var provider = CatalogProvider = Substitute.For<IGovernedCatalogProvider>();
        provider.GetCatalogAsync(Arg.Any<GovernedDataSource>(), Arg.Any<IReadOnlyCollection<TableIdentifier>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<ITableCatalog>(new InMemoryTableCatalog(entries, "main")));
        var tables = MetadataRepository = Substitute.For<ITableMetadataRepository>();
        tables.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(_dependentMetadata(call.Arg<TableIdentifier>())));
        Factory = new GovernancePolicyFactory(provider, tables, Microsoft.Extensions.Options.Options.Create(options ?? new GatewayOptions()));
        Builder = new TreeSecuredSourceBuilder(Factory, Engine);
    }

    public IGovernedCatalogProvider CatalogProvider { get; }

    public ITableMetadataRepository MetadataRepository { get; }

    public FastSqlEngine Engine { get; } = new();

    public GovernancePolicyFactory Factory { get; }

    public TreeSecuredSourceBuilder Builder { get; }

    public static TableCatalogEntry Entry(string table) => new(
        new TableIdentity("main", table),
        table switch
        {
            "orders" => ImmutableArray.Create(new CatalogColumn("id", "INTEGER"), new CatalogColumn("email", "TEXT"), new CatalogColumn("amount", "INTEGER"), new CatalogColumn("tenant_id", "TEXT")),
            "consents" => ImmutableArray.Create(new CatalogColumn("id", "INTEGER"), new CatalogColumn("order_id", "INTEGER"), new CatalogColumn("purpose", "TEXT"), new CatalogColumn("tenant_id", "TEXT")),
            "items" => ImmutableArray.Create(new CatalogColumn("id", "INTEGER"), new CatalogColumn("order_id", "INTEGER"), new CatalogColumn("label", "TEXT"), new CatalogColumn("tenant_id", "TEXT")),
            _ => ImmutableArray.Create(new CatalogColumn("id", "INTEGER"), new CatalogColumn("item_id", "INTEGER"), new CatalogColumn("tenant_id", "TEXT"))
        },
        "tenant_id",
        1);

    public static TableMetadata DependentMetadata(TableIdentifier id, bool withTenant = true, bool active = true) => new()
    {
        Table = new Table { SourceName = DataSource, SchemaName = id.Schema, TableName = id.TableName, IsActive = active, SourceType = "PostgreSql" },
        Identifier = id,
        Columns = withTenant
            ? [new TableColumn { ColumnName = "id" }, new TableColumn { ColumnName = "tenant_id" }]
            : [new TableColumn { ColumnName = "id" }]
    };

    public static TableMetadata Meta(TableIdentifier id, bool maskEmail = false)
    {
        string[] columns = id.TableName switch
        {
            "orders" => ["id", "email", "amount", "tenant_id"],
            "items" => ["id", "order_id", "label", "tenant_id"],
            _ => ["id", "tenant_id"]
        };
        return new TableMetadata
        {
            Identifier = id,
            Table = new Table { SourceName = DataSource, SchemaName = "main", TableName = id.TableName, SourceType = "PostgreSql" },
            PrimaryKeyColumns = ["id"],
            Columns = [.. columns.Select(c => new TableColumn { ColumnName = c, DataType = c is "id" or "amount" or "order_id" ? "integer" : "text" })],
            ColumnMaskingRules = maskEmail && id.TableName == "orders"
                ? new Dictionary<string, MaskingRule> { ["email"] = new MaskingRule { RuleType = "REDACT", Replacement = "***" } }
                : new Dictionary<string, MaskingRule>()
        };
    }

    // ---- policies -------------------------------------------------------------------------------------------------------

    private static SqlIdentifier Id(string name) => new(name, true);

    private static ColumnReference Col(string alias, string column) => new(new SqlQualifiedName([Id(alias), Id(column)]));

    private static NamedTableSource Source(string table, string alias, string schema = "main") => new(new SqlQualifiedName([Id(schema), Id(table)]), Id(alias));

    private static SelectStatement Select(string table, string alias, string column, Expression where, string schema = "main") => new(
        null,
        new QuerySpecification(false, [new ColumnSelectItem(Col(alias, column), null)], Source(table, alias, schema), where, null, null),
        null,
        null);

    /// <summary>EXISTS (SELECT c.order_id FROM main.consents c WHERE c.order_id = autheris_target.id AND c.purpose = :purpose).</summary>
    public static IRowPolicy ExistsPolicy(string purpose = "analytics", string schema = "main")
    {
        var namer = new PolicyParameterNamer("__rf_");
        var parameter = namer.Add(new PolicyValue(purpose, SqlParameterType.String));
        var where = new BinaryExpression(
            new BinaryExpression(Col("c", "order_id"), BinaryOperator.Equal, PolicyParameterNamer.Column($"{RowFilterAliases.Target}.id")),
            BinaryOperator.And,
            new BinaryExpression(Col("c", "purpose"), BinaryOperator.Equal, parameter));
        return new PolicyPredicateRowPolicy(
            PolicyPredicate.Create(new ExistsExpression(Select("consents", "c", "order_id", where, schema)), namer.Values), TargetSqlDialect.PostgreSql, Renderer);
    }

    /// <summary>autheris_target.id IN (SELECT c.order_id FROM main.consents c WHERE c.purpose = :purpose).</summary>
    public static IRowPolicy InPolicy(string purpose = "analytics")
    {
        var namer = new PolicyParameterNamer("__rf_");
        var parameter = namer.Add(new PolicyValue(purpose, SqlParameterType.String));
        var where = new BinaryExpression(Col("c", "purpose"), BinaryOperator.Equal, parameter);
        return new PolicyPredicateRowPolicy(
            PolicyPredicate.Create(
                new InSubqueryExpression(PolicyParameterNamer.Column($"{RowFilterAliases.Target}.id"), Select("consents", "c", "order_id", where), false),
                namer.Values),
            TargetSqlDialect.PostgreSql,
            Renderer);
    }

    /// <summary>EXISTS (SELECT ic.item_id FROM main.item_consents ic WHERE ic.item_id = autheris_target.id), on the child table.</summary>
    public static IRowPolicy ItemPolicy()
    {
        var where = new BinaryExpression(Col("ic", "item_id"), BinaryOperator.Equal, PolicyParameterNamer.Column($"{RowFilterAliases.Target}.id"));
        return new PolicyPredicateRowPolicy(
            PolicyPredicate.Create(new ExistsExpression(Select("item_consents", "ic", "item_id", where)), new Dictionary<string, PolicyValue>()),
            TargetSqlDialect.PostgreSql,
            Renderer);
    }

    /// <param name="maskEmail">The email column is masked (typed REDACT mask plus the Mask access level), as a production decision carries it.</param>
    /// <param name="extraAccess">Further column levels (a restricted-scope token denies columns, Issue #143).</param>
    public static TableAccessDecision Decision(
        TableIdentifier table, IRowPolicy? policy, bool maskEmail = false, IReadOnlyDictionary<string, ColumnAccessLevel>? extraAccess = null)
    {
        var access = new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase) { ["tenant_id"] = ColumnAccessLevel.Deny };
        if (maskEmail)
        {
            access["email"] = ColumnAccessLevel.Mask;
        }

        foreach (var (column, level) in extraAccess ?? new Dictionary<string, ColumnAccessLevel>())
        {
            access[column] = level;
        }

        var decision = TypedTreeRowPolicy.Allowed(table, access, policy);
        return maskEmail
            ? decision with
            {
                ColumnMasks = new ColumnMaskPolicy(new Dictionary<string, MaskSpec>
                {
                    ["email"] = Autheris.Application.Sql.Masking.SqlDataMaskingProvider.BuildUnkeyedMaskSpec(null, "email", DatabaseDialect.PostgreSql)
                })
            }
            : decision;
    }

    public static ResolvedTableAccess Resolved(TableMetadata metadata, TableAccessDecision decision, string tenant) =>
        new(metadata, decision, new TenantId(tenant), new Sid("user"), new ClaimsPrincipal());

    // ---- the two paths ---------------------------------------------------------------------------------------------------

    public async Task<TreeTableAccess> TreeAccessAsync(
        TableIdentifier table, IRowPolicy? policy, string tenant, bool maskEmail = false, IReadOnlyDictionary<string, ColumnAccessLevel>? extraAccess = null)
    {
        var meta = Meta(table, maskEmail);
        var decision = Decision(table, policy, maskEmail, extraAccess);
        TreeSecuredSource? source = null;
        if (TreeSqlCompiler.RowPolicyRequiresEnginePipeline(decision))
        {
            source = await Builder.BuildAsync(Resolved(meta, decision, tenant), DataSource, DatabaseDialect.PostgreSql, CancellationToken.None);
        }

        return new TreeTableAccess(meta, decision, "tenant_id", tenant, source);
    }
}
