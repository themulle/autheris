namespace Autheris.Tests.Unit.Federation;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Analysis;
using Autheris.Application.Services;
using Autheris.Application.Sql;
using Autheris.Application.Sql.Federation.Subtrees;
using Autheris.Application.Sql.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

/// <summary>
/// Parity and fail-closed security tests for federation explain subtree eligibility (#612).
/// </summary>
public sealed class ExplainSubtreeEligibilityTests
{
    private static readonly FastSqlEngine Engine = new();

    private static ClaimsPrincipal Caller(string sid = "user-1") =>
        new(new ClaimsIdentity(
        [
            DefaultTenantCaller.Claim,
            new Claim(ClaimTypes.NameIdentifier, sid),
            new Claim("sid", sid)
        ],
        "TestAuth"));

    private static TableMetadata CreateTable(
        string domain,
        string schema,
        string table,
        IReadOnlyList<string> columns,
        DatabaseDialect dialect = DatabaseDialect.PostgreSql,
        Dictionary<string, MaskingRule>? masking = null,
        DatasetVisibility visibility = DatasetVisibility.Full,
        bool isSensitiveColumn = false,
        string? sensitiveColName = null)
    {
        return new TableMetadata
        {
            Identifier = new TableIdentifier(domain, schema, table),
            Table = new Table
            {
                TableName = table,
                SchemaName = schema,
                SourceType = dialect.ToString(),
                DataSourceType = DataSourceType.Sql,
                SourceName = domain,
                IsActive = true,
                Visibility = visibility
            },
            Columns = columns.Select(c => new TableColumn
            {
                ColumnName = c,
                DataType = "varchar",
                IsSensitive = isSensitiveColumn && string.Equals(c, sensitiveColName, StringComparison.OrdinalIgnoreCase)
            }).ToList(),
            ColumnMaskingRules = masking ?? new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static Consent AllowConsent(TableMetadata table, string sid = "user-1", params ConsentColumnRule[] columnRules) =>
        new()
        {
            TenantId = Autheris.Tests.Common.TestTenants.Default,
            TableIdentifier = table.Identifier,
            Effect = ConsentEffect.Allow,
            GranteeSid = new Sid(sid),
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(10),
            ColumnRules = columnRules.ToList()
        };

    private static QueryAnalysisService CreateService(
        IReadOnlyList<TableMetadata> tables,
        IReadOnlyList<Consent> consents,
        GatewayOptions? gatewayOptions = null,
        IConsentResolutionService? consentResolution = null)
    {
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(tables);

        var consentRepo = Substitute.For<IConsentRepository>();
        consentRepo.GetAllActiveConsentsForSubjectsAsync(
                Arg.Any<IEnumerable<Sid>>(),
                Arg.Any<IEnumerable<string>?>(),
                Arg.Any<DateTimeOffset?>(),
                Arg.Any<TenantId?>(),
                Arg.Any<CancellationToken>())
            .Returns(consents);

        return new QueryAnalysisService(
            metadataRepo,
            consentRepo,
            gatewayOptions: gatewayOptions is null ? null : Options.Create(gatewayOptions),
            analysisOptions: new QueryAnalysisOptions(),
            consentResolution: consentResolution);
    }

    [Fact]
    public async Task Explain_MaskedMember_NotShownInSubtree()
    {
        const string sql =
            "SELECT o.id, c.region, s.status " +
            "FROM sales.public.orders o " +
            "JOIN sales.public.customers c ON o.customer_id = c.id " +
            "JOIN inventory.dbo.stocks s ON o.id = s.id";

        var orders = CreateTable("sales", "public", "orders", ["id", "customer_id"]);
        var customers = CreateTable("sales", "public", "customers", ["id", "region", "ssn"]);
        var stocks = CreateTable("inventory", "dbo", "stocks", ["id", "status"], DatabaseDialect.SqlServer);

        var service = CreateService(
            [orders, customers, stocks],
            [
                AllowConsent(orders),
                AllowConsent(customers, columnRules: [new ConsentColumnRule { ColumnName = "ssn", AccessLevel = ColumnAccessLevel.Mask }]),
                AllowConsent(stocks)
            ]);

        var result = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromSql(sql, name: "masked_member")], Caller());

        result.ValidationReport.IsValid.ShouldBeTrue();
        result.Federation.ShouldNotBeNull();
        (result.Federation.Subtrees ?? []).ShouldBeEmpty();
    }

    [Fact]
    public async Task Explain_AllClearMembers_ShownAsSubtree()
    {
        const string sql =
            "SELECT o.id, oi.item_name, s.status " +
            "FROM sales.public.orders o " +
            "JOIN sales.public.order_items oi ON o.id = oi.order_id " +
            "JOIN inventory.dbo.stocks s ON oi.item_id = s.id";

        var orders = CreateTable("sales", "public", "orders", ["id"]);
        var items = CreateTable("sales", "public", "order_items", ["id", "order_id", "item_name", "item_id"]);
        var stocks = CreateTable("inventory", "dbo", "stocks", ["id", "status"], DatabaseDialect.SqlServer);

        var service = CreateService(
            [orders, items, stocks],
            [AllowConsent(orders), AllowConsent(items), AllowConsent(stocks)]);

        var result = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromSql(sql, name: "all_clear")], Caller());

        result.Federation.ShouldNotBeNull();
        result.Federation.Subtrees.ShouldNotBeNull();
        result.Federation.Subtrees.Count.ShouldBe(1);
        result.Federation.Subtrees[0].SourceName.ShouldBe("sales");
        result.Federation.Subtrees[0].MemberTables.ShouldBe(
            [orders.Identifier.ToString(), items.Identifier.ToString()],
            ignoreOrder: true);
    }

    [Fact]
    public async Task Explain_UsesConfiguredMaxTablesPerSubtree()
    {
        const string sql =
            "SELECT a.id, b.name, c.label, d.status " +
            "FROM sales.public.t_a a " +
            "JOIN sales.public.t_b b ON a.id = b.a_id " +
            "JOIN sales.public.t_c c ON b.id = c.b_id " +
            "JOIN inventory.dbo.t_d d ON a.id = d.id";

        var ta = CreateTable("sales", "public", "t_a", ["id"]);
        var tb = CreateTable("sales", "public", "t_b", ["id", "a_id", "name"]);
        var tc = CreateTable("sales", "public", "t_c", ["id", "b_id", "label"]);
        var td = CreateTable("inventory", "dbo", "t_d", ["id", "status"], DatabaseDialect.SqlServer);

        var cappedOptions = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                CrossSource = new CrossSourceOptions { Enabled = true, MaxTablesPerSubtree = 2 }
            }
        };

        var cappedService = CreateService(
            [ta, tb, tc, td],
            [AllowConsent(ta), AllowConsent(tb), AllowConsent(tc), AllowConsent(td)],
            cappedOptions);

        var cappedResult = await cappedService.AnalyzeQueriesAsync([QueryAnalysisItem.FromSql(sql, name: "capped")], Caller());

        cappedResult.Federation.ShouldNotBeNull();
        cappedResult.Federation.Subtrees.ShouldNotBeNull();
        cappedResult.Federation.Subtrees.Count.ShouldBe(1);
        cappedResult.Federation.Subtrees[0].MemberTables.Count.ShouldBe(2);
        cappedResult.Federation.Subtrees[0].MemberTables.ShouldNotContain(tc.Identifier.ToString());

        // Non-positive option normalizes identically to execution's DefaultMaxTablesPerSubtree (12) instead of blocking subtrees.
        var nonPositiveOptions = new GatewayOptions
        {
            WebSql = new WebSqlOptions
            {
                CrossSource = new CrossSourceOptions { Enabled = true, MaxTablesPerSubtree = 0 }
            }
        };

        var normalizedService = CreateService(
            [ta, tb, tc, td],
            [AllowConsent(ta), AllowConsent(tb), AllowConsent(tc), AllowConsent(td)],
            nonPositiveOptions);

        var normalizedResult = await normalizedService.AnalyzeQueriesAsync([QueryAnalysisItem.FromSql(sql, name: "normalized")], Caller());

        normalizedResult.Federation.ShouldNotBeNull();
        normalizedResult.Federation.Subtrees.ShouldNotBeNull();
        normalizedResult.Federation.Subtrees.Count.ShouldBe(1);
        normalizedResult.Federation.Subtrees[0].MemberTables.Count.ShouldBe(3);
    }

    [Fact]
    public async Task Explain_SubtreeMembers_EqualCrossSourcePlannerPartition_ForSameDecisions()
    {
        const string sql =
            "SELECT o.id, oi.item_name, c.region, s.status " +
            "FROM sales.public.orders o " +
            "JOIN sales.public.order_items oi ON o.id = oi.order_id " +
            "JOIN sales.public.customers c ON o.customer_id = c.id " +
            "JOIN inventory.dbo.stocks s ON oi.item_id = s.id";

        var orders = CreateTable("sales", "public", "orders", ["id", "customer_id"]);
        var items = CreateTable("sales", "public", "order_items", ["id", "order_id", "item_name", "item_id"]);
        var customers = CreateTable(
            "sales",
            "public",
            "customers",
            ["id", "region", "ssn"],
            masking: new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["ssn"] = new MaskingRule { RuleType = "REDACT" }
            });
        var stocks = CreateTable("inventory", "dbo", "stocks", ["id", "status"], DatabaseDialect.SqlServer);

        var allTables = new[] { orders, items, customers, stocks };
        var crossSourceOpts = new CrossSourceOptions { Enabled = true, MaxTablesPerSubtree = 5 };
        var service = CreateService(
            allTables,
            [AllowConsent(orders), AllowConsent(items), AllowConsent(customers), AllowConsent(stocks)],
            new GatewayOptions { WebSql = new WebSqlOptions { CrossSource = crossSourceOpts } });

        var explainResult = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromSql(sql, name: "parity")], Caller());

        var sqlMeta = Engine.Analyze(sql.AsMemory());
        var byId = allTables.ToDictionary(t => t.Identifier.ToString(), StringComparer.OrdinalIgnoreCase);
        var resolved = sqlMeta.ReferencedTables
            .Select(t =>
            {
                var cat = byId[t.FullName];
                return new ResolvedSourceTable(t, cat.Identifier, cat, cat.Table.SourceName ?? cat.Identifier.Domain);
            })
            .ToList();

        var decisions = new Dictionary<TableIdentifier, TableAccessDecision>
        {
            [orders.Identifier] = TableAccessDecision.Allowed(orders.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            [items.Identifier] = TableAccessDecision.Allowed(items.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true),
            [customers.Identifier] = TableAccessDecision.Allowed(
                customers.Identifier,
                new Dictionary<string, ColumnAccessLevel>
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["region"] = ColumnAccessLevel.Clear,
                    ["ssn"] = ColumnAccessLevel.Mask
                },
                hasUnconstrainedColumnAllow: false),
            [stocks.Identifier] = TableAccessDecision.Allowed(stocks.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
        };

        var plan = new CrossSourcePlanner(Engine).Plan(
            sql,
            sqlMeta,
            resolved,
            decisions,
            crossSourceOpts,
            Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.User,
            Autheris.Tests.Unit.TestDoubles.PlannerTestIdentity.Tenant);

        var plannerSubtreeMembers = plan.StagingRequests
            .Where(r => r.Subtree != null)
            .Select(r => r.Subtree!.MemberTables.Select(m => m.Metadata.Identifier.ToString()).OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList())
            .ToList();

        var explainSubtreeMembers = (explainResult.Federation?.Subtrees ?? [])
            .Select(s => s.MemberTables.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList())
            .ToList();

        explainSubtreeMembers.ShouldBe(plannerSubtreeMembers);
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Explain_IneligibleMember_ExposesNoColumnOrReasonDetail()
    {
        // Pin exact API shape of AnalyzedSubtree and QueryFederationAnalysis (C-6).
        typeof(AnalyzedSubtree).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ShouldBe(
        [
            nameof(AnalyzedSubtree.GeneratedSql),
            nameof(AnalyzedSubtree.MemberTables),
            nameof(AnalyzedSubtree.ProjectedColumns),
            nameof(AnalyzedSubtree.SourceName),
            nameof(AnalyzedSubtree.SubtreeId)
        ]);

        typeof(QueryFederationAnalysis).GetProperties().Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ShouldBe(
        [
            nameof(QueryFederationAnalysis.CoLocatedGroups),
            nameof(QueryFederationAnalysis.KeySetPushdowns),
            nameof(QueryFederationAnalysis.Subtrees),
            nameof(QueryFederationAnalysis.Tables)
        ]);

        const string sql =
            "SELECT o.id, c.region, s.status " +
            "FROM sales.public.orders o " +
            "JOIN sales.public.customers c ON o.customer_id = c.id " +
            "JOIN inventory.dbo.stocks s ON o.id = s.id";

        var orders = CreateTable("sales", "public", "orders", ["id", "customer_id"]);
        var customers = CreateTable("sales", "public", "customers", ["id", "region", "secret_tax_id"], isSensitiveColumn: true, sensitiveColName: "secret_tax_id");
        var stocks = CreateTable("inventory", "dbo", "stocks", ["id", "status"], DatabaseDialect.SqlServer);

        var service = CreateService(
            [orders, customers, stocks],
            [AllowConsent(orders), AllowConsent(customers), AllowConsent(stocks)]);

        var result = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromSql(sql, name: "c6_shape")], Caller());

        var json = JsonSerializer.Serialize(result.Federation);
        json.ShouldNotContain("secret_tax_id", Case.Insensitive);
        json.ShouldNotContain("ineligible", Case.Insensitive);
        json.ShouldNotContain("MaskReason", Case.Insensitive);
        json.ShouldNotContain("EligibilityReason", Case.Insensitive);
        (result.Federation!.Subtrees ?? []).ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Explain_MissingDecision_TreatedAsIneligible()
    {
        var table = CreateTable("sales", "public", "orders", ["id"]);
        var sqlTarget = Engine.Analyze("SELECT id FROM sales.public.orders".AsMemory()).ReferencedTables[0];
        var resolved = new ResolvedSourceTable(sqlTarget, table.Identifier, table, "sales");

        QueryAnalysisService.TryIsPushdownEligible(resolved, new Dictionary<TableIdentifier, TableAccessDecision>()).ShouldBeFalse();

        const string sql =
            "SELECT o.id, h.secret, s.status " +
            "FROM sales.public.orders o " +
            "JOIN sales.public.hidden_orders h ON o.id = h.id " +
            "JOIN inventory.dbo.stocks s ON o.id = s.id";

        var hiddenOrders = CreateTable("sales", "public", "hidden_orders", ["id", "secret"], visibility: DatasetVisibility.Hidden);
        var stocks = CreateTable("inventory", "dbo", "stocks", ["id", "status"], DatabaseDialect.SqlServer);

        // Caller lacks consent for hiddenOrders, so no positive decision exists for it.
        var service = CreateService([table, hiddenOrders, stocks], [AllowConsent(table), AllowConsent(stocks)]);
        var result = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromSql(sql, name: "missing_decision")], Caller());

        result.Federation.ShouldNotBeNull();
        (result.Federation.Subtrees ?? []).ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Explain_DecisionThrows_TableIneligible_NotSubtree()
    {
        var orders = CreateTable("sales", "public", "orders", ["id", "customer_id"]);
        var customers = CreateTable("sales", "public", "customers", ["id", "region"]);
        var stocks = CreateTable("inventory", "dbo", "stocks", ["id", "status"], DatabaseDialect.SqlServer);

        var sqlTarget = Engine.Analyze("SELECT id FROM sales.public.orders".AsMemory()).ReferencedTables[0];
        var resolvedOrders = new ResolvedSourceTable(sqlTarget, orders.Identifier, orders, "sales");

        QueryAnalysisService.TryIsPushdownEligible(
            resolvedOrders,
            _ => throw new InvalidOperationException("decision lookup fault"))
            .ShouldBeFalse();

        var realResolver = new ConsentResolutionService();
        var throwingResolver = Substitute.For<IConsentResolutionService>();
        throwingResolver.ResolveAccess(
                Arg.Any<Sid>(),
                Arg.Any<IReadOnlySet<Sid>>(),
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<IReadOnlyList<Consent>>(),
                Arg.Any<DatabaseDialect>(),
                Arg.Any<IReadOnlySet<Sid>?>(),
                Arg.Any<IReadOnlySet<string>?>())
            .Returns(ci =>
            {
                var tid = ci.ArgAt<TableIdentifier>(3);
                if (tid == customers.Identifier)
                {
                    throw new InvalidOperationException("resolver fault for customers");
                }

                return realResolver.ResolveAccess(
                    ci.ArgAt<Sid>(0),
                    ci.ArgAt<IReadOnlySet<Sid>>(1),
                    ci.ArgAt<IReadOnlySet<string>>(2),
                    tid,
                    ci.ArgAt<IReadOnlyList<Consent>>(4),
                    ci.ArgAt<DatabaseDialect>(5),
                    ci.ArgAt<IReadOnlySet<Sid>?>(6),
                    ci.ArgAt<IReadOnlySet<string>?>(7));
            });

        const string sql =
            "SELECT o.id, c.region, s.status " +
            "FROM sales.public.orders o " +
            "JOIN sales.public.customers c ON o.customer_id = c.id " +
            "JOIN inventory.dbo.stocks s ON o.id = s.id";

        var service = CreateService(
            [orders, customers, stocks],
            [AllowConsent(orders), AllowConsent(customers), AllowConsent(stocks)],
            consentResolution: throwingResolver);

        var result = await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromSql(sql, name: "throwing_decision")], Caller());

        result.Federation.ShouldNotBeNull();
        (result.Federation.Subtrees ?? []).ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task Explain_IneligibleMember_DoesNotAddExtraDecisionCalls()
    {
        const string sql =
            "SELECT o.id, h.secret, c.region, s.status " +
            "FROM sales.public.orders o " +
            "JOIN sales.public.hidden_orders h ON o.id = h.id " +
            "JOIN sales.public.customers c ON o.id = c.id " +
            "JOIN inventory.dbo.stocks s ON o.id = s.id";

        var orders = CreateTable("sales", "public", "orders", ["id"]);
        var hiddenOrders = CreateTable("sales", "public", "hidden_orders", ["id", "secret"], visibility: DatasetVisibility.Hidden);
        var customers = CreateTable("sales", "public", "customers", ["id", "region", "ssn"], isSensitiveColumn: true, sensitiveColName: "ssn");
        var stocks = CreateTable("inventory", "dbo", "stocks", ["id", "status"], DatabaseDialect.SqlServer);

        var callsByTable = new Dictionary<TableIdentifier, int>();
        var realResolver = new ConsentResolutionService();
        var countingResolver = Substitute.For<IConsentResolutionService>();
        countingResolver.ResolveAccess(
                Arg.Any<Sid>(),
                Arg.Any<IReadOnlySet<Sid>>(),
                Arg.Any<IReadOnlySet<string>>(),
                Arg.Any<TableIdentifier>(),
                Arg.Any<IReadOnlyList<Consent>>(),
                Arg.Any<DatabaseDialect>(),
                Arg.Any<IReadOnlySet<Sid>?>(),
                Arg.Any<IReadOnlySet<string>?>())
            .Returns(ci =>
            {
                var tid = ci.ArgAt<TableIdentifier>(3);
                callsByTable[tid] = callsByTable.GetValueOrDefault(tid) + 1;
                return realResolver.ResolveAccess(
                    ci.ArgAt<Sid>(0),
                    ci.ArgAt<IReadOnlySet<Sid>>(1),
                    ci.ArgAt<IReadOnlySet<string>>(2),
                    tid,
                    ci.ArgAt<IReadOnlyList<Consent>>(4),
                    ci.ArgAt<DatabaseDialect>(5),
                    ci.ArgAt<IReadOnlySet<Sid>?>(6),
                    ci.ArgAt<IReadOnlySet<string>?>(7));
            });

        var service = CreateService(
            [orders, hiddenOrders, customers, stocks],
            [AllowConsent(orders), AllowConsent(customers), AllowConsent(stocks)],
            consentResolution: countingResolver);

        await service.AnalyzeQueriesAsync([QueryAnalysisItem.FromSql(sql, name: "call_count")], Caller());

        callsByTable.GetValueOrDefault(hiddenOrders.Identifier).ShouldBe(0);
        callsByTable.GetValueOrDefault(orders.Identifier).ShouldBe(1);
        callsByTable.GetValueOrDefault(customers.Identifier).ShouldBe(1);
        callsByTable.GetValueOrDefault(stocks.Identifier).ShouldBe(1);
    }
}
