namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Policy.RowScopes;
using Autheris.Application.Policy.Services;
using Autheris.Application.Services;
using Autheris.Application.Sql.Catalog;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Integration.Security;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Emit;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// Dialect cells of the row-scope compiler: composite KeySet (row-value IN or VALUES join), Live EXISTS and the per-cell CASE with a flag
/// join, on SqlServer, PostgreSql, Oracle, Databricks, DuckDb and Ansi. The cells compile and render SQL only; they need no database, so
/// they carry no container skip.
/// </summary>
[Trait("Category", "Security")]
public sealed class RowScopeDialectTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly Sid UserSid = new("S-1-5-21-100-200");
    private static readonly TableIdentifier OrdersId = new("prod", "dbo", "orders");
    private static readonly TableIdentity OrdersIdentity = new("dbo", "orders");

    public static TheoryData<TargetSqlDialect, string> CompositeKeySetFragments => new()
    {
        { TargetSqlDialect.PostgreSql, "\"autheris_target\".\"tenant_code\", \"autheris_target\".\"client_id\") IN ((@" },
        { TargetSqlDialect.Oracle, "\"AUTHERIS_TARGET\".\"tenant_code\", \"AUTHERIS_TARGET\".\"client_id\") IN ((@" },
        { TargetSqlDialect.Databricks, "`autheris_target`.`tenant_code`, `autheris_target`.`client_id`) IN ((@" },
        { TargetSqlDialect.DuckDb, "\"autheris_target\".\"tenant_code\", \"autheris_target\".\"client_id\") IN ((@" },
        { TargetSqlDialect.SqlServer, "AS [v] (" },
        { TargetSqlDialect.Ansi, "AS \"v\" (" }
    };

    public static TheoryData<TargetSqlDialect> AllDialects => new()
    {
        TargetSqlDialect.SqlServer,
        TargetSqlDialect.PostgreSql,
        TargetSqlDialect.Oracle,
        TargetSqlDialect.Databricks,
        TargetSqlDialect.DuckDb,
        TargetSqlDialect.Ansi
    };

    [Theory]
    [MemberData(nameof(CompositeKeySetFragments))]
    public async Task Composite_keyset_renders_pairwise_comparison(TargetSqlDialect dialect, string expectedFragment)
    {
        var keySetPolicy = CompositePolicy("ks_composite", "ref_src", RowPolicyEvaluationMode.KeySet);
        var keySet = VirtualFilterKeySet.Create(
            [VirtualFilterKeyType.Text, VirtualFilterKeyType.IntegerNumber],
            new object?[][] { ["T1", 10L], ["T2", 20L] });
        var compiler = Compiler(new VirtualFilterSnapshot(1, [keySetPolicy], []), new FixedKeySetProvider(keySet));
        var grant = new RowScopeGrant("g1", ConsentGrantSource.ConsentRequest,
            [new GrantRowScopeEntry("ks_composite", new Dictionary<string, JsonElement>())]);

        var outcome = await compiler.CompileAsync(new RowScopeCompileRequest(
            Tenant, UserSid, new HashSet<Sid>(), CompileTable(), [grant], TargetDialect: dialect));

        outcome.IsDenied.ShouldBeFalse();
        outcome.Predicate.ShouldNotBeNull();
        RenderSql(outcome.Predicate!, dialect).ShouldContain(expectedFragment);
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public async Task Live_policy_renders_correlated_exists_with_all_key_columns(TargetSqlDialect dialect)
    {
        var live = CompositePolicy("live_composite", "prod", RowPolicyEvaluationMode.Live);
        var compiler = Compiler(new VirtualFilterSnapshot(1, [live], []));
        var grant = new RowScopeGrant("g2", ConsentGrantSource.ConsentRequest,
            [new GrantRowScopeEntry("live_composite", new Dictionary<string, JsonElement>())]);

        var outcome = await compiler.CompileAsync(new RowScopeCompileRequest(
            Tenant, UserSid, new HashSet<Sid>(), CompileTable(), [grant], TargetDialect: dialect));

        outcome.IsDenied.ShouldBeFalse();
        outcome.Predicate.ShouldNotBeNull();
        var sql = RenderSql(outcome.Predicate!, dialect);
        sql.ShouldContain("EXISTS (SELECT 1 FROM");
        sql.ShouldContain("tenant_code");
        sql.ShouldContain("client_id");
    }

    [Theory]
    [MemberData(nameof(AllDialects))]
    public async Task Per_cell_case_with_flag_join_for_two_signatures(TargetSqlDialect dialect)
    {
        var euClear = Consent("region", "EU", ColumnAccessLevel.Clear, ColumnAccessLevel.Mask);
        var vipMask = Consent("tier", "VIP", ColumnAccessLevel.Mask, ColumnAccessLevel.Deny);

        var evalDialect = dialect is TargetSqlDialect.DuckDb or TargetSqlDialect.Ansi ? TargetSqlDialect.PostgreSql : dialect;
        var metadata = PerCellMetadata(evalDialect);
        var decision = await DecideAsync([euClear, vipMask], metadata);
        decision.IsAllowed.ShouldBeTrue();
        decision.GetColumnAccess("email").ShouldBe(ColumnAccessLevel.RowDependent);
        decision.PerCellColumns.ShouldNotBeNull();

        var compiled = await CompileSqlAsync(decision, dialect, "SELECT id, email, ssn FROM dbo.orders");
        compiled.Sql.ShouldContain("CASE WHEN", Case.Insensitive);
        compiled.Sql.ShouldContain("ELSE NULL END", Case.Insensitive);
    }

    private static string RenderSql(PolicyPredicate predicate, TargetSqlDialect dialect) =>
        new PolicyPredicateRenderer().Render(predicate, dialect, RowFilterAliases.Target).Sql;

    private static RowScopeCompiler Compiler(VirtualFilterSnapshot snapshot, IVirtualFilterKeySetProvider? keySets = null) =>
        new(
            new FixedSnapshotProvider(snapshot),
            new StructuredFilterSqlBuilder(),
            keySets,
            new RowPolicyFactory(new PolicyPredicateRenderer(), new PolicyExpressionParser(FastSqlEngine.Default)));

    private static VirtualFilter CompositePolicy(string name, string source, RowPolicyEvaluationMode mode) => new()
    {
        TenantId = Tenant,
        Name = name,
        Source = source,
        EvaluationMode = mode,
        KeyColumns = ["acl.tenant_code", "acl.client_id"],
        Structured = new StructuredFilterDefinition
        {
            From = new TableIdentifier(source, "auth", "acl"),
            FromAlias = "acl",
            Where = [new FilterCondition("acl.active", FilterConditionOperator.Eq, "Y")]
        }
    };

    private static TableMetadata CompileTable() => new()
    {
        Identifier = OrdersId,
        Table = new Table
        {
            SourceName = OrdersId.Domain,
            SchemaName = OrdersId.Schema,
            TableName = OrdersId.TableName,
            SourceType = "PostgreSql",
            DataSourceType = DataSourceType.Sql,
            IsActive = true
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "bigint" },
            new TableColumn { ColumnName = "client_id", DataType = "integer" },
            new TableColumn { ColumnName = "tenant_code", DataType = "varchar" }
        ]
    };

    private static string SourceTypeFor(TargetSqlDialect dialect) => dialect switch
    {
        TargetSqlDialect.SqlServer => "SqlServer",
        TargetSqlDialect.Oracle => "Oracle",
        TargetSqlDialect.Databricks => "Databricks",
        _ => "PostgreSQL"
    };

    private static TableMetadata PerCellMetadata(TargetSqlDialect dialect) => new()
    {
        ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor("email", "ssn"),
        Identifier = OrdersId,
        Table = new Table { Id = Guid.NewGuid(), SourceName = "prod", SchemaName = "dbo", TableName = "orders", SourceType = SourceTypeFor(dialect), IsActive = true },
        PrimaryKeyColumns = ["id"],
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "INTEGER" },
            new TableColumn { ColumnName = "region", DataType = "VARCHAR(32)" },
            new TableColumn { ColumnName = "tier", DataType = "VARCHAR(32)" },
            new TableColumn { ColumnName = "email", DataType = "VARCHAR(128)" },
            new TableColumn { ColumnName = "ssn", DataType = "VARCHAR(64)" }
        ],
        ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
        {
            ["email"] = new() { RuleType = "MASK_EMAIL" },
            ["ssn"] = new() { RuleType = "REDACT", Replacement = "[REDACTED]" }
        }
    };

    private static Consent Consent(string filterColumn, string filterValue, ColumnAccessLevel email, ColumnAccessLevel ssn) => new()
    {
        TableIdentifier = OrdersId,
        TenantId = Tenant,
        GranteeType = GranteeType.User,
        GranteeSid = UserSid,
        Effect = ConsentEffect.Allow,
        ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
        ValidTo = DateTimeOffset.UtcNow.AddDays(1),
        RowFilters = [new ConsentRowFilter { ColumnName = filterColumn, Operator = "EQ", ValueType = "string", ValueJson = JsonSerializer.Serialize(filterValue) }],
        ColumnRules =
        [
            new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
            new ConsentColumnRule { ColumnName = "region", AccessLevel = ColumnAccessLevel.Clear },
            new ConsentColumnRule { ColumnName = "tier", AccessLevel = ColumnAccessLevel.Clear },
            new ConsentColumnRule { ColumnName = "email", AccessLevel = email },
            new ConsentColumnRule { ColumnName = "ssn", AccessLevel = ssn }
        ]
    };

    private static async Task<TableAccessDecision> DecideAsync(IReadOnlyList<Consent> consents, TableMetadata metadata)
    {
        var options = new GatewayOptions { DataMasking = new DataMaskingOptions() };
        var policy = new TableAccessPolicy(
            new CrossTransportParityHarness.InMemoryConsentRepo(consents),
            new ConsentResolutionService(),
            NullConsentCacheService.Instance,
            options,
            NullMandatoryRowFilterResolver.Instance,
            rowPolicyFactory: new RowPolicyFactory(new PolicyPredicateRenderer()),
            maskPolicyFactory: new ColumnMaskPolicyFactory(new SqlDataMaskingProvider(Options.Create(options))),
            rowScopeCompiler: Compiler(VirtualFilterSnapshot.Empty));
        var query = new TableAccessQuery(UserSid, Tenant, new HashSet<Sid>(), new HashSet<string>(), metadata, Claims: []);
        return await policy.DecideAsync(query, CancellationToken.None);
    }

    private static async Task<CompiledSql> CompileSqlAsync(TableAccessDecision decision, TargetSqlDialect dialect, string sql)
    {
        var metadata = PerCellMetadata(dialect is TargetSqlDialect.DuckDb or TargetSqlDialect.Ansi ? TargetSqlDialect.PostgreSql : dialect);
        var catalog = new InMemoryTableCatalog(
            [new TableCatalogEntry(OrdersIdentity, metadata.Columns.Select(c => new CatalogColumn(c.ColumnName, c.DataType)).ToImmutableArray(), metadata.TenantColumnName, 1L)],
            "dbo");
        var options = new GatewayOptions { DataMasking = new DataMaskingOptions() };
        var factory = new GovernancePolicyFactory(
            new FixedCatalogProvider(catalog),
            new CrossTransportParityHarness.InMemoryTableRepo([metadata]),
            Options.Create(options));
        var policy = await factory.CreateAsync(new GovernedPolicyInput(
            "prod",
            Tenant,
            new Dictionary<TableIdentity, TableAccessDecision> { [OrdersIdentity] = decision },
            new HashSet<TableIdentity>(),
            GovernedSubqueryStrategy.Correlated)
        {
            Metadata = new Dictionary<TableIdentity, TableMetadata> { [OrdersIdentity] = metadata }
        }, CancellationToken.None);

        if (dialect is TargetSqlDialect.DuckDb or TargetSqlDialect.Ansi)
        {
            policy = policy with { RowFilters = new DialectPassThroughRowFilterProvider(policy.RowFilters) };
        }

        var request = CompileRequestFactory.Create(policy, dialect, new WebSqlOptions(), isDml: false, enforcedMaxRows: 100,
            allowedFunctions: new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        return new FastSqlEngine().Compile(sql.AsMemory(), request, CancellationToken.None);
    }

    private sealed class FixedSnapshotProvider(VirtualFilterSnapshot snapshot) : IVirtualFilterSnapshotProvider
    {
        public ValueTask<VirtualFilterSnapshot> GetAsync(CancellationToken ct = default) => ValueTask.FromResult(snapshot);
        public void Invalidate() { }
    }

    private sealed class FixedKeySetProvider(VirtualFilterKeySet keySet) : IVirtualFilterKeySetProvider
    {
        public ValueTask<VirtualFilterKeySet> GetAsync(
            VirtualFilter filter,
            IReadOnlyList<VirtualFilterKeyType> keyTypes,
            TenantId tenant,
            CancellationToken ct = default) => ValueTask.FromResult(keySet);
    }

    private sealed class FixedCatalogProvider(ITableCatalog catalog) : IGovernedCatalogProvider
    {
        public ValueTask<ITableCatalog> GetCatalogAsync(GovernedDataSource dataSource, IReadOnlyCollection<TableIdentifier> tables, CancellationToken ct) =>
            ValueTask.FromResult(catalog);
    }

    private sealed class DialectPassThroughRowFilterProvider(IPolicyPredicateProvider inner) : IPolicyPredicateProvider
    {
        public bool ShouldApplyPolicy(TableIdentity table) => inner.ShouldApplyPolicy(table);
        public PolicyPredicate GetPredicate(TableIdentity table) => inner.GetPredicate(table);
    }
}
