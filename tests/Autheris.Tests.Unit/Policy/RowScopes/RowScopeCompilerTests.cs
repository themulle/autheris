namespace Autheris.Tests.Unit.Policy.RowScopes;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.RowScopes;
using Autheris.Application.Policy.Services;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Application.Services;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Governance;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Ast.Nodes;
using TrinoSqlEngine.Governance;
using Xunit;

[Trait("Category", "Security")]
public sealed class RowScopeCompilerTests
{
    private static readonly TenantId Tenant = new("tenant-a");
    private static readonly Sid UserSid = new("S-1-5-21-USER-1");
    private static readonly Sid GroupSid = new("S-1-5-21-GROUP-1");
    private static readonly TableIdentifier OrdersId = new("prod", "sales", "orders");

    private static TableMetadata OrdersTable(DatabaseDialect dialect = DatabaseDialect.PostgreSql, bool includeRegion = true, bool includeOrg = true)
    {
        var cols = new List<TableColumn>
        {
            new() { ColumnName = "id", DataType = "bigint" },
            new() { ColumnName = "client_id", DataType = "integer" },
            new() { ColumnName = "tenant_code", DataType = "varchar" },
            new() { ColumnName = "amount", DataType = "decimal" }
        };
        if (includeRegion)
        {
            cols.Add(new TableColumn { ColumnName = "region", DataType = "varchar" });
        }
        if (includeOrg)
        {
            cols.Add(new TableColumn { ColumnName = "org_id", DataType = "integer" });
        }

        return new TableMetadata
        {
            Identifier = OrdersId,
            Table = new Table
            {
                SourceName = OrdersId.Domain,
                SchemaName = OrdersId.Schema,
                TableName = OrdersId.TableName,
                SourceType = dialect.ToString(),
                DataSourceType = DataSourceType.Sql,
                IsActive = true
            },
            Columns = cols
        };
    }

    private static VirtualFilter LiveParamPolicy(string name, string paramName, int maxValues = 10, string keyCol = "client.client_id") => new()
    {
        TenantId = Tenant,
        Name = name,
        Source = "prod",
        EvaluationMode = RowPolicyEvaluationMode.Live,
        KeyColumns = [keyCol],
        Parameters = [new RowPolicyParameter(paramName, RowPolicyParameterType.String, IsList: true, MaxValues: maxValues)],
        Structured = new StructuredFilterDefinition
        {
            From = new TableIdentifier("prod", "conf", "client"),
            FromAlias = "client",
            Where = [new FilterCondition("client.region", FilterConditionOperator.Eq, ":" + paramName)]
        }
    };

    private static VirtualFilter LiveStatusPolicy(string name, string keyCol = "org.org_id", string col = "org.status", string val = "ACTIVE") => new()
    {
        TenantId = Tenant,
        Name = name,
        Source = "prod",
        EvaluationMode = RowPolicyEvaluationMode.Live,
        KeyColumns = [keyCol],
        Structured = new StructuredFilterDefinition
        {
            From = new TableIdentifier("prod", "conf", "org"),
            FromAlias = "org",
            Where = [new FilterCondition(col, FilterConditionOperator.Eq, val)]
        }
    };

    private static VirtualFilter CompositeKeySetPolicy(string name, string source = "ref_src") => new()
    {
        TenantId = Tenant,
        Name = name,
        Source = source,
        EvaluationMode = RowPolicyEvaluationMode.KeySet,
        KeyColumns = ["acl.tenant_code", "acl.client_id"],
        Structured = new StructuredFilterDefinition
        {
            From = new TableIdentifier(source, "auth", "acl"),
            FromAlias = "acl",
            Where = [new FilterCondition("acl.active", FilterConditionOperator.Eq, "Y")]
        }
    };

    private static JsonElement JsonOf(object? value) => JsonSerializer.SerializeToElement(value);

    private static Dictionary<string, JsonElement> Params(string name, params string[] values) =>
        new(StringComparer.OrdinalIgnoreCase) { [name] = JsonOf(values) };

    private static RowScopeCompiler CreateCompiler(VirtualFilterSnapshot snapshot, FakeKeySetProvider? keySets = null) =>
        new(
            new FixedSnapshotProvider(snapshot),
            new StructuredFilterSqlBuilder(),
            keySets,
            new RowPolicyFactory(new PolicyPredicateRenderer(), new PolicyExpressionParser(FastSqlEngine.Default)));

    private static string RenderSql(PolicyPredicate predicate, TargetSqlDialect dialect) =>
        new PolicyPredicateRenderer().Render(predicate, dialect, RowFilterAliases.Target).Sql;

    [Fact]
    public async Task Same_policy_two_grants_merges_parameter_list()
    {
        var policy = LiveParamPolicy("region_policy", "regions", maxValues: 5);
        var snapshot = new VirtualFilterSnapshot(1, [policy], []);
        var compiler = CreateCompiler(snapshot);

        var grant1 = new RowScopeGrant("g1", ConsentGrantSource.ConsentRequest, [new GrantRowScopeEntry("region_policy", Params("regions", "EU", "UK"))]);
        var grant2 = new RowScopeGrant("g2", ConsentGrantSource.ConsentRequest, [new GrantRowScopeEntry("region_policy", Params("regions", "US", "UK"))]);

        var outcome = await compiler.CompileAsync(new RowScopeCompileRequest(
            Tenant, UserSid, new HashSet<Sid>(), OrdersTable(), [grant1, grant2]));

        outcome.IsDenied.ShouldBeFalse();
        outcome.Predicate.ShouldNotBeNull();
        // Merged into a single EXISTS predicate rather than ORing two separate EXISTS subqueries.
        var existsCount = TrinoSqlEngine.Ast.Security.AstReflection.Collect<ExistsExpression>(outcome.Predicate!.Expression).Count;
        existsCount.ShouldBe(1);
        outcome.Predicate.Parameters.Values.Select(v => v.Value).ShouldBe(["EU", "UK", "US"], ignoreOrder: true);
    }

    [Fact]
    public async Task Two_entries_same_grant_anded()
    {
        var p1 = LiveParamPolicy("region_policy", "regions");
        var p2 = LiveStatusPolicy("org_policy");
        var snapshot = new VirtualFilterSnapshot(1, [p1, p2], []);
        var compiler = CreateCompiler(snapshot);

        var grant = new RowScopeGrant("g1", ConsentGrantSource.ConsentRequest,
        [
            new GrantRowScopeEntry("region_policy", Params("regions", "EU")),
            new GrantRowScopeEntry("org_policy", new Dictionary<string, JsonElement>())
        ]);

        var outcome = await compiler.CompileAsync(new RowScopeCompileRequest(
            Tenant, UserSid, new HashSet<Sid>(), OrdersTable(), [grant]));

        outcome.IsDenied.ShouldBeFalse();
        outcome.Predicate.ShouldNotBeNull();
        var topBinary = outcome.Predicate!.Expression.ShouldBeOfType<BinaryExpression>();
        topBinary.Operator.ShouldBe(BinaryOperator.And);
        TrinoSqlEngine.Ast.Security.AstReflection.Collect<ExistsExpression>(outcome.Predicate.Expression).Count.ShouldBe(2);
    }

    [Fact]
    public async Task Uncovered_entry_denies_grant_or_skips_by_setting()
    {
        var pRegion = LiveParamPolicy("region_policy", "regions", keyCol: "client.region");
        var pOrg = LiveStatusPolicy("org_policy");
        var snapshot = new VirtualFilterSnapshot(1, [pRegion, pOrg], []);
        var compiler = CreateCompiler(snapshot);
        var tableWithoutRegion = OrdersTable(includeRegion: false, includeOrg: true);

        // Single grant with UncoveredPolicy.Deny -> grant does not contribute -> denied.
        var denyGrant = new RowScopeGrant("g-deny", ConsentGrantSource.ConsentRequest,
            [new GrantRowScopeEntry("region_policy", Params("regions", "EU"), Uncovered: UncoveredPolicy.Deny)]);
        var deniedOutcome = await compiler.CompileAsync(new RowScopeCompileRequest(
            Tenant, UserSid, new HashSet<Sid>(), tableWithoutRegion, [denyGrant]));
        deniedOutcome.IsDenied.ShouldBeTrue();

        // When paired with a covered grant, the uncovered Deny grant does not contribute and the covered grant applies.
        var coveredGrant = new RowScopeGrant("g-ok", ConsentGrantSource.ConsentRequest,
            [new GrantRowScopeEntry("org_policy", new Dictionary<string, JsonElement>())]);
        var partialOutcome = await compiler.CompileAsync(new RowScopeCompileRequest(
            Tenant, UserSid, new HashSet<Sid>(), tableWithoutRegion, [denyGrant, coveredGrant]));
        partialOutcome.IsDenied.ShouldBeFalse();
        partialOutcome.Predicate.ShouldNotBeNull();
        TrinoSqlEngine.Ast.Security.AstReflection.Collect<ExistsExpression>(partialOutcome.Predicate!.Expression).Count.ShouldBe(1);

        // UncoveredPolicy.Skip drops only that entry -> single-entry grant becomes unfiltered.
        var skipGrant = new RowScopeGrant("g-skip", ConsentGrantSource.ConsentRequest,
            [new GrantRowScopeEntry("region_policy", Params("regions", "EU"), Uncovered: UncoveredPolicy.Skip)]);
        var skippedOutcome = await compiler.CompileAsync(new RowScopeCompileRequest(
            Tenant, UserSid, new HashSet<Sid>(), tableWithoutRegion, [skipGrant]));
        skippedOutcome.IsDenied.ShouldBeFalse();
        skippedOutcome.Predicate.ShouldBeNull();
        skippedOutcome.IsUnfilteredGrantUnion.ShouldBeTrue();
    }

    [Fact]
    public async Task Unfiltered_grant_collapses_filter()
    {
        var p1 = LiveParamPolicy("region_policy", "regions");
        var snapshot = new VirtualFilterSnapshot(1, [p1], []);
        var compiler = CreateCompiler(snapshot);

        var filtered = new RowScopeGrant("g1", ConsentGrantSource.ConsentRequest,
            [new GrantRowScopeEntry("region_policy", Params("regions", "EU"))]);
        var unfiltered = new RowScopeGrant("g2", ConsentGrantSource.ConsentRequest, []);

        var outcome = await compiler.CompileAsync(new RowScopeCompileRequest(
            Tenant, UserSid, new HashSet<Sid>(), OrdersTable(), [filtered, unfiltered]));

        outcome.IsDenied.ShouldBeFalse();
        outcome.IsUnfilteredGrantUnion.ShouldBeTrue();
        outcome.Predicate.ShouldBeNull();
        outcome.RowPolicy.ShouldBeNull();
    }

    [Fact]
    public async Task Hard_row_policy_anded_and_applies_to_owner_grant()
    {
        var grantPolicy = LiveParamPolicy("region_policy", "regions");
        var hardPolicy = LiveStatusPolicy("hard_org_policy");
        var hardProfile = new VirtualFilterAccessProfile
        {
            TenantId = Tenant,
            Name = "hard-group",
            GranteeType = GranteeType.Group,
            GranteeSid = GroupSid,
            Scope = "prod.sales.*",
            Uncovered = UncoveredPolicy.Deny,
            Bindings = [new FilterBinding { FilterName = "hard_org_policy" }]
        };
        var snapshot = new VirtualFilterSnapshot(1, [grantPolicy, hardPolicy], [hardProfile]);
        var compiler = CreateCompiler(snapshot);

        // Constrained consent + hard policy -> ANDed.
        var consentGrant = new RowScopeGrant("g1", ConsentGrantSource.ConsentRequest,
            [new GrantRowScopeEntry("region_policy", Params("regions", "EU"))]);
        var constrained = await compiler.CompileAsync(new RowScopeCompileRequest(
            Tenant, UserSid, new HashSet<Sid> { GroupSid }, OrdersTable(), [consentGrant]));
        constrained.IsDenied.ShouldBeFalse();
        constrained.Predicate.ShouldNotBeNull();
        constrained.Predicate!.Expression.ShouldBeOfType<BinaryExpression>().Operator.ShouldBe(BinaryOperator.And);
        constrained.AppliedHardPolicies.ShouldBe(["hard_org_policy"]);

        // Owner unfiltered grant + hard policy -> hard policy STILL applies.
        var ownerUnfiltered = new RowScopeGrant("owner-1", ConsentGrantSource.Owner, []);
        var ownerOutcome = await compiler.CompileAsync(new RowScopeCompileRequest(
            Tenant, UserSid, new HashSet<Sid> { GroupSid }, OrdersTable(), [ownerUnfiltered]));
        ownerOutcome.IsDenied.ShouldBeFalse();
        ownerOutcome.IsUnfilteredGrantUnion.ShouldBeTrue();
        ownerOutcome.Predicate.ShouldNotBeNull();
        ownerOutcome.AppliedHardPolicies.ShouldBe(["hard_org_policy"]);
        RenderSql(ownerOutcome.Predicate!, TargetSqlDialect.PostgreSql).ShouldContain("\"org\".\"status\"");
    }

    [Fact]
    public async Task Hard_row_policy_uncovered_denies_access()
    {
        var hardPolicy = LiveStatusPolicy("hard_region_policy", keyCol: "org.region");
        var denyProfile = new VirtualFilterAccessProfile
        {
            TenantId = Tenant,
            Name = "hard-uncovered",
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            Scope = "prod.sales.*",
            Uncovered = UncoveredPolicy.Deny,
            Bindings = [new FilterBinding { FilterName = "hard_region_policy" }]
        };
        var compiler = CreateCompiler(new VirtualFilterSnapshot(1, [hardPolicy], [denyProfile]));
        var ownerGrant = new RowScopeGrant("owner-1", ConsentGrantSource.Owner, []);

        var outcome = await compiler.CompileAsync(new RowScopeCompileRequest(
            Tenant, UserSid, new HashSet<Sid>(), OrdersTable(includeRegion: false), [ownerGrant]));
        outcome.IsDenied.ShouldBeTrue();
        outcome.DenyCode.ShouldBe(RowScopeCompiler.HardPolicyUncoveredCode);
        outcome.DenyStep.ShouldBe(DecisionStep.ExplicitDeny);

        // An unsupported grantee type on a hard row policy is never skipped: the compile throws, so the decision denies.
        var unsupportedProfile = new VirtualFilterAccessProfile
        {
            TenantId = Tenant,
            Name = "unsupported-hard",
            GranteeType = (GranteeType)3,
            Scope = "prod.sales.*",
            Uncovered = UncoveredPolicy.Skip,
            Bindings = [new FilterBinding { FilterName = "hard_region_policy" }]
        };
        var unsupportedCompiler = CreateCompiler(new VirtualFilterSnapshot(1, [hardPolicy], [unsupportedProfile]));
        await Should.ThrowAsync<ArgumentOutOfRangeException>(async () => await unsupportedCompiler.CompileAsync(new RowScopeCompileRequest(
            Tenant, UserSid, new HashSet<Sid>(), OrdersTable(includeRegion: true), [ownerGrant])));
    }

    [Theory]
    [InlineData(TargetSqlDialect.PostgreSql, "\"autheris_target\".\"tenant_code\", \"autheris_target\".\"client_id\") IN ((@")]
    [InlineData(TargetSqlDialect.Oracle, "\"AUTHERIS_TARGET\".\"tenant_code\", \"AUTHERIS_TARGET\".\"client_id\") IN ((@")]
    [InlineData(TargetSqlDialect.Databricks, "`autheris_target`.`tenant_code`, `autheris_target`.`client_id`) IN ((@")]
    [InlineData(TargetSqlDialect.DuckDb, "\"autheris_target\".\"tenant_code\", \"autheris_target\".\"client_id\") IN ((@")]
    [InlineData(TargetSqlDialect.SqlServer, "AS [v] (")]
    [InlineData(TargetSqlDialect.Ansi, "AS \"v\" (")]
    public async Task Composite_key_live_and_keyset_dialects(TargetSqlDialect dialect, string expectedFragment)
    {
        var keySetPolicy = CompositeKeySetPolicy("ks_composite", source: "ref_src");
        var keySet = VirtualFilterKeySet.Create(
            [VirtualFilterKeyType.Text, VirtualFilterKeyType.IntegerNumber],
            new object?[][] { ["T1", 10L], ["T2", 20L] });
        var provider = new FakeKeySetProvider(keySet);
        var compiler = CreateCompiler(new VirtualFilterSnapshot(1, [keySetPolicy], []), provider);

        var grant = new RowScopeGrant("g1", ConsentGrantSource.ConsentRequest,
            [new GrantRowScopeEntry("ks_composite", new Dictionary<string, JsonElement>())]);

        var outcome = await compiler.CompileAsync(new RowScopeCompileRequest(
            Tenant, UserSid, new HashSet<Sid>(), OrdersTable(), [grant], TargetDialect: dialect));

        outcome.IsDenied.ShouldBeFalse();
        outcome.Predicate.ShouldNotBeNull();
        var sql = RenderSql(outcome.Predicate!, dialect);
        sql.ShouldContain(expectedFragment);

        // Live composite policy emits correlated EXISTS with both key columns joined.
        var liveComposite = keySetPolicy with { Name = "live_composite", Source = "prod", EvaluationMode = RowPolicyEvaluationMode.Live };
        var liveCompiler = CreateCompiler(new VirtualFilterSnapshot(1, [liveComposite], []));
        var liveGrant = new RowScopeGrant("g2", ConsentGrantSource.ConsentRequest,
            [new GrantRowScopeEntry("live_composite", new Dictionary<string, JsonElement>())]);
        var liveOutcome = await liveCompiler.CompileAsync(new RowScopeCompileRequest(
            Tenant, UserSid, new HashSet<Sid>(), OrdersTable(), [liveGrant], TargetDialect: dialect));
        liveOutcome.IsDenied.ShouldBeFalse();
        liveOutcome.Predicate.ShouldNotBeNull();
        var liveSql = RenderSql(liveOutcome.Predicate!, dialect);
        liveSql.ShouldContain("EXISTS (SELECT 1 FROM");
        liveSql.ShouldContain("tenant_code");
        liveSql.ShouldContain("client_id");
    }

    [Fact]
    public async Task Composite_key_null_never_matches()
    {
        var keySetPolicy = CompositeKeySetPolicy("ks_nulls");
        var nullOnlySet = VirtualFilterKeySet.Create(
            [VirtualFilterKeyType.Text, VirtualFilterKeyType.IntegerNumber],
            new object?[][] { ["T1", null], [null, 10L], [null, null] });
        nullOnlySet.Count.ShouldBe(0);

        var compiler = CreateCompiler(
            new VirtualFilterSnapshot(1, [keySetPolicy], []),
            new FakeKeySetProvider(nullOnlySet));

        var grant = new RowScopeGrant("g1", ConsentGrantSource.ConsentRequest,
            [new GrantRowScopeEntry("ks_nulls", new Dictionary<string, JsonElement>())]);
        var outcome = await compiler.CompileAsync(new RowScopeCompileRequest(
            Tenant, UserSid, new HashSet<Sid>(), OrdersTable(), [grant], TargetDialect: TargetSqlDialect.PostgreSql));

        outcome.IsDenied.ShouldBeFalse();
        outcome.Predicate.ShouldNotBeNull();
        RenderSql(outcome.Predicate!, TargetSqlDialect.PostgreSql).ShouldBe("1 = 0");
    }

    [Fact]
    public async Task Own_rows_write_applies_row_scope_and_fails_closed()
    {
        var writePolicy = LiveParamPolicy("write_region_policy", "regions");
        var hardPolicy = LiveStatusPolicy("hard_org_policy");
        var hardProfile = new VirtualFilterAccessProfile
        {
            TenantId = Tenant,
            Name = "hard-user",
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            Scope = "prod.sales.*",
            Uncovered = UncoveredPolicy.Deny,
            Bindings = [new FilterBinding { FilterName = "hard_org_policy" }]
        };
        var snapshot = new VirtualFilterSnapshot(1, [writePolicy, hardPolicy], [hardProfile]);
        var compiler = CreateCompiler(snapshot);

        var writeConsent = new Consent
        {
            Id = Guid.NewGuid(),
            TenantId = Tenant,
            TableIdentifier = OrdersId,
            GranteeType = GranteeType.User,
            GranteeSid = UserSid,
            Effect = ConsentEffect.Allow,
            ValidFrom = DateTimeOffset.UtcNow.AddHours(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            WriteRules =
            [
                new WriteRule(
                    Guid.NewGuid(),
                    WriteOperation.Update,
                    ["amount"],
                    WriteRowScope.OwnRows,
                    RowFilter: [new GrantRowScopeEntry("write_region_policy", Params("regions", "EU"))])
            ]
        };

        var consentRepo = new FakeConsentRepository();
        await consentRepo.CreateConsentAsync(writeConsent);
        var options = Options.Create(new GatewayOptions
        {
            WebSql = new WebSqlOptions { Enabled = true, AllowDml = true }
        });
        var evaluator = new WriteGrantEvaluator(consentRepo);
        var pdp = new AccessDecisionPoint(
            options,
            new ConsentResolutionService(),
            consentRepo,
            writeGrantEvaluator: evaluator,
            rowScopeCompiler: compiler);

        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserSid.Value)], "Test"));
        var subject = new SecurityPrincipalContext
        {
            IsAuthenticated = true,
            AuthenticationScheme = "Test",
            UserSid = UserSid,
            ResolvedTenant = Tenant,
            TenantClaimInvalid = false,
            GroupResolutionStatus = GroupResolutionStatus.Complete,
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            ClusterRoles = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            Claims = principal.Claims,
            ClientIp = IPAddress.Loopback,
            Principal = principal
        };

        var writtenColumns = new HashSet<string>(["amount"], StringComparer.OrdinalIgnoreCase);
        var tableWithOrg = OrdersTable();
        var writeQueryWithOrg = new Autheris.Application.Policy.Pdp.WriteGrantQuery(principal, Tenant, tableWithOrg, writtenColumns);
        var decision = await pdp.DecideAsync(
            new AccessRequest(
                subject,
                tableWithOrg,
                AccessOperation.Update,
                AccessTransport.WebSql,
                Write: writeQueryWithOrg));

        decision.IsAllowed.ShouldBeTrue();
        decision.RowPolicy.ShouldNotBeNull();
        decision.RequireTypedRowPolicy().ShouldBeSameAs(decision);
        var rendered = decision.RowPolicy!.Project();
        rendered.Sql.ShouldContain("\"client\".\"region\"");
        rendered.Sql.ShouldContain("\"org\".\"status\"");

        // Fails closed when RowScopeCompiler is missing on OwnRows write with WriteRule.RowFilter.
        var pdpWithoutCompiler = new AccessDecisionPoint(
            options,
            new ConsentResolutionService(),
            consentRepo,
            writeGrantEvaluator: evaluator);
        var deniedNoCompiler = await pdpWithoutCompiler.DecideAsync(
            new AccessRequest(
                subject,
                tableWithOrg,
                AccessOperation.Update,
                AccessTransport.WebSql,
                Write: writeQueryWithOrg));
        deniedNoCompiler.IsAllowed.ShouldBeFalse();
        deniedNoCompiler.Reasons.ShouldContain(r => r.Code == "write_grant_row_scope_unsupported");

        // Fails closed when hard row policy target column is missing.
        var tableWithoutOrg = OrdersTable(includeOrg: false);
        var deniedUncovered = await pdp.DecideAsync(
            new AccessRequest(
                subject,
                tableWithoutOrg,
                AccessOperation.Update,
                AccessTransport.WebSql,
                Write: new Autheris.Application.Policy.Pdp.WriteGrantQuery(principal, Tenant, tableWithoutOrg, writtenColumns)));
        deniedUncovered.IsAllowed.ShouldBeFalse();
        deniedUncovered.Reasons.ShouldContain(r => r.Code == RowScopeCompiler.HardPolicyUncoveredCode && r.Step == DecisionStep.ExplicitDeny);
    }

    [Fact]
    public async Task Parameter_count_exceeds_max_values_splits_or_denies()
    {
        var policy = LiveParamPolicy("region_policy", "regions", maxValues: 2);
        var compiler = CreateCompiler(new VirtualFilterSnapshot(1, [policy], []));

        // Two grants each within MaxValues=2, combined 3 distinct values -> split into 2 ORed EXISTS predicates (no truncation).
        var g1 = new RowScopeGrant("g1", ConsentGrantSource.ConsentRequest, [new GrantRowScopeEntry("region_policy", Params("regions", "EU", "UK"))]);
        var g2 = new RowScopeGrant("g2", ConsentGrantSource.ConsentRequest, [new GrantRowScopeEntry("region_policy", Params("regions", "US"))]);
        var splitOutcome = await compiler.CompileAsync(new RowScopeCompileRequest(
            Tenant, UserSid, new HashSet<Sid>(), OrdersTable(), [g1, g2]));

        splitOutcome.IsDenied.ShouldBeFalse();
        splitOutcome.Predicate.ShouldNotBeNull();
        splitOutcome.Predicate!.Expression.ShouldBeOfType<BinaryExpression>().Operator.ShouldBe(BinaryOperator.Or);
        TrinoSqlEngine.Ast.Security.AstReflection.Collect<ExistsExpression>(splitOutcome.Predicate.Expression).Count.ShouldBe(2);
        splitOutcome.Predicate.Parameters.Values.Select(v => v.Value).ShouldBe(["EU", "UK", "US"], ignoreOrder: true);

        // Single grant exceeding MaxValues=2 -> denied fail-closed.
        var overCapGrant = new RowScopeGrant("g-over", ConsentGrantSource.ConsentRequest, [new GrantRowScopeEntry("region_policy", Params("regions", "EU", "UK", "US"))]);
        var deniedOutcome = await compiler.CompileAsync(new RowScopeCompileRequest(
            Tenant, UserSid, new HashSet<Sid>(), OrdersTable(), [overCapGrant]));
        deniedOutcome.IsDenied.ShouldBeTrue();
        deniedOutcome.DenyCode.ShouldBe(VirtualFilterErrorCodes.RowPolicyParameterInvalid);
    }

    private sealed class FixedSnapshotProvider(VirtualFilterSnapshot snapshot) : IVirtualFilterSnapshotProvider
    {
        public ValueTask<VirtualFilterSnapshot> GetAsync(CancellationToken ct = default) => ValueTask.FromResult(snapshot);
        public void Invalidate() { }
    }

    private sealed class FakeKeySetProvider(VirtualFilterKeySet keySet) : IVirtualFilterKeySetProvider
    {
        public ValueTask<VirtualFilterKeySet> GetAsync(
            VirtualFilter filter,
            IReadOnlyList<VirtualFilterKeyType> keyTypes,
            TenantId tenant,
            CancellationToken ct = default) =>
            ValueTask.FromResult(keySet);
    }
}
