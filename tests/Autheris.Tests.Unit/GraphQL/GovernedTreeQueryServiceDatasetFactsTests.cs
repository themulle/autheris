using Autheris.Tests.Unit.TestSupport;
using System.Data.Common;
using System.Security.Claims;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Services;
using Autheris.Application.Services;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Sql.Tree;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;
using BinaryOperator = TrinoSqlEngine.Ast.Nodes.BinaryOperator;

namespace Autheris.Tests.Unit.GraphQL;

public sealed class GovernedTreeQueryServiceDatasetFactsTests
{
    private static readonly TableIdentifier Authors = new("blog", "main", "authors");
    private static readonly TableIdentifier Articles = new("blog", "main", "articles");

    private sealed class StubFactory : ISqlConnectionFactory
    {
        public Task<DbConnection> CreateOpenConnectionAsync(DataSourceConnectionOptions options, CancellationToken ct = default) =>
            Task.FromResult<DbConnection>(new StubJsonDbConnection("[]"));
    }

    private static TableMetadata Meta(TableIdentifier id, params string[] columns) => new()
    {
        Identifier = id,
        Table = new Table { SourceName = "blog", SchemaName = id.Schema, TableName = id.TableName, SourceType = "PostgreSql" },
        PrimaryKeyColumns = ["id"],
        Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = c == "name" || c == "title" ? "text" : "integer" }).ToList()
    };

    private static ResolvedTableAccess Access(TableMetadata m, Func<TableAccessDecision, TableAccessDecision>? tweak = null)
    {
        var decision = TableAccessDecision.Allowed(m.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        return new ResolvedTableAccess(m, tweak?.Invoke(decision) ?? decision, new TenantId("t1"), new Sid("S-1-5-21-DAVID"), DefaultTenantCaller.Principal());
    }

    private static GovernedTreeQueryService Create(Dictionary<TableIdentifier, ResolvedTableAccess> access)
    {
        var resolver = new StubTableAccessResolver((_, table) => access[table]);
        var options = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["blog"] = new() { Provider = "PostgreSql", ConnectionString = "Host=stub;Database=test;" }
                }
            }
        };
        return new GovernedTreeQueryService(resolver, new StubFactory(), new InMemoryAuditLogRepository(),
            StubColumnMaskingProvider.Null, Options.Create(options));
    }

    private static Dictionary<TableIdentifier, ResolvedTableAccess> Tables(
        Func<TableAccessDecision, TableAccessDecision>? authors = null,
        Func<TableAccessDecision, TableAccessDecision>? articles = null) => new()
    {
        [Authors] = Access(Meta(Authors, "id", "name"), authors),
        [Articles] = Access(Meta(Articles, "id", "author_id", "title"), articles)
    };

    private static TreeQueryNode Tree() => new(Authors, ["id", "name"])
    {
        Relations = [new TreeRelationNode("articles", ["id"], ["author_id"], true, new TreeQueryNode(Articles, ["title"]))],
        OrderBy = [new TreeOrder("id")]
    };

    private static async Task<(GovernedTreeQueryService Service, string OpId)> ExecutedAsync(Dictionary<TableIdentifier, ResolvedTableAccess> access)
    {
        var service = Create(access);
        var opId = Guid.NewGuid().ToString("N");
        using var _ = await service.ExecuteAsync(DefaultTenantCaller.Principal(), Tree(), null, opId);
        return (service, opId);
    }

    [Fact]
    public async Task AfterExecute_ReturnsRootAndRelationFactsWithPaths()
    {
        var (service, opId) = await ExecutedAsync(Tables());

        var facts = service.GetDatasetFacts(DefaultTenantCaller.Principal(), Tree(), opId);

        facts.Count.ShouldBe(2);
        facts[0].Table.ShouldBe(Authors);
        facts[0].Path.ShouldBeEmpty();
        facts[1].Table.ShouldBe(Articles);
        facts[1].Path.ShouldBe(["articles"]);
    }

    [Fact]
    public async Task ConsentRowFilter_ReportsOwnerDefinedDescription()
    {
        var (service, opId) = await ExecutedAsync(Tables(authors: d => d.WithAdditionalRowPolicy(TypedTreeRowPolicy.Int("id", BinaryOperator.GreaterThan, 1))));

        var facts = service.GetDatasetFacts(DefaultTenantCaller.Principal(), Tree(), opId);

        facts[0].RowFilterApplied.ShouldBeTrue();
        facts[0].RowFilterDescriptions.ShouldBe([EffectiveAccessExplanationService.RowFilterDescription]);
        facts[1].RowFilterApplied.ShouldBeFalse();
    }

    [Fact]
    public async Task AttributeRowFilter_ReportsAttributeDescriptionOnly()
    {
        var recipe = new ConsentRowFilterRecipe(
            [[new ConsentRowFilter { ColumnName = "id", Operator = "IN", ValueSource = "USER_ATTRIBUTE", UserAttribute = "secret_attribute_name" }]], []);
        var (service, opId) = await ExecutedAsync(Tables(authors: d => d.WithAdditionalRowPolicy(TypedTreeRowPolicy.Int("id", BinaryOperator.GreaterThan, 1)) with { ConsentFilterRecipe = recipe }));

        var facts = service.GetDatasetFacts(DefaultTenantCaller.Principal(), Tree(), opId);

        facts[0].RowFilterDescriptions.ShouldBe([EffectiveAccessExplanationService.AttributeRowFilterDescription]);
    }

    [Fact]
    public async Task NoRowFilter_ReportsEmpty()
    {
        var (service, opId) = await ExecutedAsync(Tables());

        service.GetDatasetFacts(DefaultTenantCaller.Principal(), Tree(), opId).ShouldAllBe(f => !f.RowFilterApplied && f.RowFilterDescriptions.Count == 0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task ConsentAndVirtualFilter_ReportsOnlyConstantDescriptions()
    {
        var (service, opId) = await ExecutedAsync(Tables(authors: d => d
            .WithAdditionalRowPolicy(TypedTreeRowPolicy.Int("id", BinaryOperator.GreaterThan, 41))
            .WithMandatoryPredicate(TypedTreeRowPolicy.Int("id", BinaryOperator.LessThan, 41), ["vf-secret-name"])));

        var facts = service.GetDatasetFacts(DefaultTenantCaller.Principal(), Tree(), opId);

        facts[0].RowFilterDescriptions.ShouldBe([EffectiveAccessExplanationService.RowFilterDescription]);
        string.Join(' ', facts.SelectMany(f => f.RowFilterDescriptions)).ShouldNotContain("vf-secret-name");
        string.Join(' ', facts.SelectMany(f => f.RowFilterDescriptions)).ShouldNotContain("41");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task VirtualFilterOnly_ReportsConstantLikeDescribeMyAccess()
    {
        var (service, opId) = await ExecutedAsync(Tables(authors: d => d.WithMandatoryPredicate(TypedTreeRowPolicy.Int("id", BinaryOperator.LessThan, 41), ["vf-secret-name"])));

        var facts = service.GetDatasetFacts(DefaultTenantCaller.Principal(), Tree(), opId);

        // Same decision shape as describe_my_access: the virtual predicate is part of the combined filter, so only the constant shows.
        facts[0].RowFilterDescriptions.ShouldBe([EffectiveAccessExplanationService.RowFilterDescription]);
        string.Join(' ', facts[0].RowFilterDescriptions).ShouldNotContain("virtual-secret");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task NeverContainsRowFilterSqlOrParameterValues()
    {
        var (service, opId) = await ExecutedAsync(Tables(authors: d => d.WithAdditionalRowPolicy(TypedTreeRowPolicy.Text("name", BinaryOperator.Equal, "param-secret-value"))));

        var text = string.Join(' ', service.GetDatasetFacts(DefaultTenantCaller.Principal(), Tree(), opId).SelectMany(f => f.RowFilterDescriptions));

        text.ShouldNotContain("name");
        text.ShouldNotContain("param-secret-value");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DeniedTableInTree_ReturnsEmpty()
    {
        var access = Tables();
        var service = Create(access);
        var opId = Guid.NewGuid().ToString("N");
        using (await service.ExecuteAsync(DefaultTenantCaller.Principal(), Tree(), null, opId))
        {
        }

        // A tree that names a table the memo never saw (or saw as denied) yields nothing, never a partial list.
        var unseen = new TreeQueryNode(Authors, ["id"])
        {
            Relations = [new TreeRelationNode("x", ["id"], ["id"], true, new TreeQueryNode(new TableIdentifier("blog", "main", "unseen"), ["id"]))]
        };
        service.GetDatasetFacts(DefaultTenantCaller.Principal(), unseen, opId).ShouldBeEmpty();
        access[Articles] = access[Articles] with { Decision = TableAccessDecision.Denied(Articles, "no consent") };
        var service2 = Create(access);
        var opId2 = Guid.NewGuid().ToString("N");
        await Should.ThrowAsync<Exception>(() => service2.ExecuteAsync(DefaultTenantCaller.Principal(), Tree(), null, opId2));
        service2.GetDatasetFacts(DefaultTenantCaller.Principal(), Tree(), opId2).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void WithoutPrecedingExecute_ReturnsEmpty()
    {
        Create(Tables()).GetDatasetFacts(DefaultTenantCaller.Principal(), Tree(), "never-executed").ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task AfterClearOperation_ReturnsEmpty()
    {
        var (service, opId) = await ExecutedAsync(Tables());

        service.ClearOperation(opId);

        service.GetDatasetFacts(DefaultTenantCaller.Principal(), Tree(), opId).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task DifferentPrincipalOrTenant_ReturnsEmpty()
    {
        var (service, opId) = await ExecutedAsync(Tables());
        var other = new ClaimsPrincipal(new ClaimsIdentity([DefaultTenantCaller.Claim, new Claim(ClaimTypes.PrimarySid, "S-1-5-21-OTHER")], "test"));
        var otherTenant = new ClaimsPrincipal(new ClaimsIdentity([new Claim("tenant_id", "another-tenant"), new Claim(ClaimTypes.PrimarySid, "S-1-5-21-DAVID")], "test"));

        service.GetDatasetFacts(other, Tree(), opId).ShouldBeEmpty();
        service.GetDatasetFacts(otherTenant, Tree(), opId).ShouldBeEmpty();
    }
}
