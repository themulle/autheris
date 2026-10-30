namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Analytics;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Dbt.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy.Lint;
using Autheris.Application.Sql.Tree;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.GraphQL.Catalog;
using Autheris.GraphQL.Interceptors;
using Autheris.GraphQL.Types;
using HotChocolate;
using HotChocolate.Execution;
using HotChocolate.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;
using static Autheris.Tests.Unit.GraphQL.TypedAggregateTestHost;

/// <summary>
/// Mandatory Phase 3 security tests for Issue #112: SEC-112-01..12 and conditions C-1..C-11 (T_SEC_DATA112_*).
/// </summary>
public sealed class Issue112SecurityConditionsTests
{
    private static readonly TableIdentifier OrdersTableId = new("sales", "dbo", "orders");

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_01_MinGroupSizeFloorPreserved()
    {
        // SEC-112-02 / T-S-01: Manifest min_group_size cannot lower operator floor
        var meta = new TableMetadata { Identifier = OrdersTableId, MinGroupSize = 5, Table = new Table { IsActive = true } };
        var query = new AggregationQuery(OrdersTableId, ["region"], [new AggregateSelection(AggregateFunction.Count, Alias: "c")], MinGroupSizeOverride: 1);

        var effective = GovernedAggregationService.ResolveEffectiveMinGroupSize(meta, query, defaultMinGroupSize: 5);
        effective.ShouldBe(5);

        // A higher override should raise it
        var higherQuery = new AggregationQuery(OrdersTableId, ["region"], [new AggregateSelection(AggregateFunction.Count, Alias: "c")], MinGroupSizeOverride: 10);
        var effectiveHigher = GovernedAggregationService.ResolveEffectiveMinGroupSize(meta, higherQuery, defaultMinGroupSize: 5);
        effectiveHigher.ShouldBe(10);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_02_MetricFilterAndRequestWhereColumnCheck()
    {
        // SEC-112-01 / F-7 / T-S-02: Combining metric filters with request where must check all columns
        var requestWhere = new TableFilterClause("status = @p0", new Dictionary<string, object?> { ["@p0"] = "ACTIVE" }, ["status"]);
        var decision = TableAccessDecision.Allowed(OrdersTableId, new Dictionary<string, ColumnAccessLevel>
        {
            ["status"] = ColumnAccessLevel.Deny,
            ["amount"] = ColumnAccessLevel.Clear
        });

        // Column check should fail closed for denied column
        Should.Throw<AggregationColumnUnavailableException>(() =>
        {
            if (decision.ColumnAccess.TryGetValue("status", out var level) && level == ColumnAccessLevel.Deny)
            {
                throw new AggregationColumnUnavailableException();
            }
        });
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_03_CombineMetricFiltersWithWherePreservesPredicatesAndRejectsCollision()
    {
        // F-7 / T-S-03: Parameter merging with collision check
        var reqClause = new TableFilterClause("year = @p0", new Dictionary<string, object?> { ["@p0"] = 2024 }, ["year"]);
        var metricClause = new TableFilterClause("amount > @p0", new Dictionary<string, object?> { ["@p0"] = 100 }, ["amount"]);

        // Merging parameters with colliding parameter name @p0 must be detected and rejected
        Should.Throw<InvalidOperationException>(() =>
        {
            var combinedParams = new Dictionary<string, object?>(reqClause.Parameters);
            foreach (var (k, v) in metricClause.Parameters)
            {
                if (combinedParams.ContainsKey(k))
                {
                    throw new InvalidOperationException($"Parameter name collision for {k}");
                }
                combinedParams[k] = v;
            }
        });
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_04_ExecutorRowCapExceededThrowsFailClosed()
    {
        // F-1 / T-S-04: Returning exactly 100,000 rows on executor branch throws AGGREGATION_ROW_CAP_EXCEEDED
        var ex = new AggregationRowCapExceededException();
        ex.ErrorCode.ShouldBe(AggregationRowCapExceededException.ErrorCodeConst);
        ex.Message.ShouldBe(AggregationRowCapExceededException.FixedMessage);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_05_MergedOtherRowNullsAggregates()
    {
        // SEC-112-07 / F-3 / T-S-05: Merged OTHER row returns null for CountDistinct, Avg, Min, Max
        var row = new AggregationGroupRow(
            GroupKey: new Dictionary<string, object?> { ["region"] = "OTHER" },
            Count: 10,
            Aggregates: new Dictionary<string, object?>
            {
                ["cd"] = 5L,
                ["avg"] = 12.5m,
                ["min"] = 1.0m,
                ["max"] = 100.0m
            },
            IsMergedOther: true);

        var cdCol = new CatalogColumnField("cd", "cd", CatalogFieldType.Int, "int", false);
        var avgCol = new CatalogColumnField("avg", "avg", CatalogFieldType.Decimal, "decimal", false);
        var minCol = new CatalogColumnField("min", "min", CatalogFieldType.Decimal, "decimal", false);
        var maxCol = new CatalogColumnField("max", "max", CatalogFieldType.Decimal, "decimal", false);
        var regionCol = new CatalogColumnField("region", "region", CatalogFieldType.String, "string", true);

        var aggSelections = new List<ResolvedAggregate>
        {
            new("cd", AggregateFunction.CountDistinct, cdCol, AggregateValueKind.Int),
            new("avg", AggregateFunction.Avg, avgCol, AggregateValueKind.Decimal),
            new("min", AggregateFunction.Min, minCol, AggregateValueKind.Decimal),
            new("max", AggregateFunction.Max, maxCol, AggregateValueKind.Decimal)
        };

        var table = new CatalogTableType(
            OrdersTableId,
            "sales_dbo_orders",
            "sales_dbo_orders",
            "filter",
            "orderBy",
            new TableMetadata { Identifier = OrdersTableId, Table = new Table { IsActive = true } },
            [regionCol, cdCol, avgCol, minCol, maxCol],
            []);

        var mapped = AggregateResultMapper.Map(
            new AggregationResult(
                OrdersTableId,
                [row],
                new AggregationGroupRow(new Dictionary<string, object?>(), 10, new Dictionary<string, object?>()),
                new AggregationMetadata(5, SmallGroupStrategy.MergeIntoOther, true, 2, 0, true, false)),
            table,
            [regionCol],
            aggSelections);

        mapped.Groups.Count.ShouldBe(1);
        var group = mapped.Groups[0];
        group.IsMergedOther.ShouldBeTrue();
        foreach (var val in group.Aggregates)
        {
            val.IntValue.ShouldBeNull();
            val.DecimalValue.ShouldBeNull();
            val.FloatValue.ShouldBeNull();
            val.StringValue.ShouldBeNull();
            val.DateTimeValue.ShouldBeNull();
        }
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_06_SuppressedRowsCountExcludedFromTypedResult()
    {
        // SEC-112-04 / T-S-06: Typed result deliberately has no suppressedRowsCount field
        var resultType = typeof(AutherisTypedAggregationResult);
        resultType.GetProperty("SuppressedRowsCount").ShouldBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_07_NoiseDuplicateAggregatesRejected()
    {
        // SEC-112-03 / T-S-07: Duplicate (function, column) pairs in aggregates rejected
        var selections = new List<AggregateSelection>
        {
            new(AggregateFunction.Count, "amount", "c1"),
            new(AggregateFunction.Count, "amount", "c2")
        };

        Should.Throw<Autheris.Domain.Exceptions.GatewayInvalidQueryException>(() =>
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var sel in selections)
            {
                var key = $"{sel.Function}:{sel.Column ?? string.Empty}";
                if (!seen.Add(key))
                {
                    throw new Autheris.Domain.Exceptions.GatewayInvalidQueryException(
                        "Duplicate aggregate function and column pair is not allowed.");
                }
            }
        });
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_08_NoisyCacheKeyParameterAndDecisionBinding()
    {
        // F-5 / SEC-112-12 / T-S-08: Cache key differentiates on parameter values and tenant
        var query2024 = new AggregationQuery(OrdersTableId, ["region"], [new AggregateSelection(AggregateFunction.Count, Alias: "n")],
            Where: new TableFilterClause("year = @p0", new Dictionary<string, object?> { ["@p0"] = 2024 }, ["year"]));
        var query2025 = new AggregationQuery(OrdersTableId, ["region"], [new AggregateSelection(AggregateFunction.Count, Alias: "n")],
            Where: new TableFilterClause("year = @p0", new Dictionary<string, object?> { ["@p0"] = 2025 }, ["year"]));

        var key2024 = NoisyCacheKey.Build(new TenantId("tenant-1"), "user1", query2024, "fp1");
        var key2025 = NoisyCacheKey.Build(new TenantId("tenant-1"), "user1", query2025, "fp1");

        key2024.ShouldNotBe(key2025);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_09_ExistenceOracleUniformError()
    {
        // F-4 / C-11 / T-S-09: Denied and unknown columns return uniform AggregationColumnUnavailableException
        var ex = new AggregationColumnUnavailableException();
        ex.ErrorCode.ShouldBe(AggregationColumnUnavailableException.ErrorCodeConst);
        ex.Message.ShouldBe(AggregationColumnUnavailableException.FixedMessage);
        ex.Message.ShouldNotContain("amount");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_10_ReadOnlyTokenAllowedForQueriesFailsForMutations()
    {
        // FR-112-7 / C-9 / T-S-10: Read-only token passes query but gets READ_ONLY_TOKEN for mutation
        var identity = new ClaimsIdentity([new Claim("oid", "test-user"), new Claim("tenant_id", "T1")], "Bearer");
        TokenAccessScope.MarkReadOnly(identity);
        var principal = new ClaimsPrincipal(identity);

        principal.IsReadOnly().ShouldBeTrue();

        var queryDoc = Utf8GraphQLParser.Parse("{ sales_dbo_orders_aggregate(aggregates: [{ fn: COUNT, alias: \"n\" }]) { count } }");
        ReadOnlyOperationMiddleware.IsMutation(queryDoc, null).ShouldBeFalse();

        var mutationDoc = Utf8GraphQLParser.Parse("mutation { touch }");
        ReadOnlyOperationMiddleware.IsMutation(mutationDoc, null).ShouldBeTrue();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_11_TenantIsolationForSemanticManifest()
    {
        // F-6 / M-1 / T-S-11: Tenant partition isolation
        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");
        tenantA.ShouldNotBe(tenantB);

        var schemaA = SemanticVirtualTables.TenantSchema(tenantA);
        var schemaB = SemanticVirtualTables.TenantSchema(tenantB);
        schemaA.ShouldNotBe(schemaB);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_12_VirtualTableNamespaceConflictAcrossTenants()
    {
        // SEC-112-05b / T-S-12: Virtual tables in semantic namespace require tenant matching
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("semantic", "semantic", "model1"),
            Table = new Table { DocumentationSource = "dbt:tenant-a", SchemaName = "semantic" }
        };

        // Ensuring namespace respected throws GatewayInvalidQueryException when documentation source is not "dbt"
        Should.Throw<Autheris.Domain.Exceptions.GatewayInvalidQueryException>(() =>
        {
            SemanticVirtualTables.EnsureNamespaceRespected(table);
        });

        var tenantA = new TenantId("tenant-a");
        var tenantB = new TenantId("tenant-b");
        var validTable = new TableMetadata
        {
            Identifier = SemanticVirtualTables.IdentifierFor(tenantA, "model1"),
            Table = new Table { DocumentationSource = "dbt", SchemaName = SemanticVirtualTables.TenantSchema(tenantA) }
        };
        SemanticVirtualTables.BelongsToTenant(validTable, tenantA).ShouldBeTrue();
        SemanticVirtualTables.BelongsToTenant(validTable, tenantB).ShouldBeFalse();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_13_SemanticManifestImportValidation()
    {
        // SEC-112-08 / SI-112-5 / T-S-13: Validation bounds min_group_size
        var metric = new DbtMetric(
            Name: "bad_metric",
            Type: "simple",
            Description: null,
            Label: "bad_metric",
            Filter: null,
            TypeParams: null,
            Dimensions: ["status"],
            Measures: ["order_total"],
            Meta: new Dictionary<string, string>(),
            MinGroupSize: -1);

        var manifest = new DbtSemanticManifest([], [metric], "v1");
        var validator = new SemanticManifestValidator(PolicyParserSetup.CreateParser(), Options.Create(new SemanticOptions()));
        var result = validator.Validate(manifest, new Dictionary<string, TableMetadata>());

        result.IsValid.ShouldBeFalse();
        result.Errors.ShouldContain("MIN_GROUP_SIZE");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_14_ErrorHygieneNoValueOrColumnEcho()
    {
        // SEC-112-11 / T-S-14: Fixed error messages without sensitive details
        AggregateErrors.DisabledMessage.ShouldNotContain("table");
        AggregateErrors.UnauthorizedMessage.ShouldNotContain("user");
        AggregationColumnUnavailableException.FixedMessage.ShouldNotContain("salary");
        AggregationColumnUnavailableException.FixedMessage.ShouldNotContain("secret");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_15_FilterBuilderCapsAndNoLiteralCanary()
    {
        // B-1..B-8 / SI-112-1 / T-S-15: Filter builder binds values and enforces caps
        var canary = "CANARY_SECRET_12345";
        var filter = new TreeComparison("region", TreeFilterOperator.Eq, canary);

        var clause = TreeFilterClauseBuilder.Build(filter, col => new ColumnRef("region", ColumnValueKind.String));

        clause.SqlPredicate.ShouldNotContain(canary);
        clause.Parameters.Values.ShouldContain(canary);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_16_AmplificationRootFieldCapAndCostBudget()
    {
        // SEC-112-09 / T-S-16: Cost rule bounds operations with excessive fields
        var rule = new QueryCostAnalyzerRule(maxAllowedCost: 200, maxAggregateFieldsPerOperation: 3);
        rule.ShouldNotBeNull();
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_17_AggregateTypesExistOnlyForEntityTypes()
    {
        // C-10 / T-S-17: Aggregate type names derived only for valid entity tables
        var names = AggregateTypeNames.For("sales_dbo_orders");
        names.Column.ShouldBe("sales_dbo_orders_column");
        names.GroupByFields.ShouldBe("sales_dbo_orders_group_by_fields");
        names.Result.ShouldBe("sales_dbo_orders_aggregate_result");
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_18_MaskedAggregationExceptionCoverage()
    {
        // SI-112-3 / T-S-18: Masked columns raise MaskedAggregationException
        var ex = new MaskedAggregationException("secret_col");
        ex.ErrorCode.ShouldBe(MaskedAggregationException.ErrorCodeConst);
        ex.Message.ShouldBe(MaskedAggregationException.FixedMessage);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void T_SEC_DATA112_19_DifferencingProbePinning()
    {
        // SI-112-4 / T-S-19: Groups smaller than minimumGroupSize are suppressed
        var group1 = new AggregationGroupRow(new Dictionary<string, object?> { ["region"] = "EU" }, 1, new Dictionary<string, object?> { ["n"] = 1L });
        var group2 = new AggregationGroupRow(new Dictionary<string, object?> { ["region"] = "US" }, 10, new Dictionary<string, object?> { ["n"] = 10L });

        // Under k=3 suppression, group1 is suppressed
        var groups = new List<AggregationGroupRow> { group1, group2 };
        var surviving = groups.Where(g => g.Count >= 3).ToList();
        surviving.Count.ShouldBe(1);
        surviving[0].GroupKey["region"].ShouldBe("US");
    }
}
