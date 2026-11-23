namespace Autheris.Tests.Unit.Policy.Pdp;


using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Plugins.Auth;
using Autheris.Application.Policy;
using Autheris.Application.Policy.Classification;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.RowScopes;
using Autheris.Application.Policy.Services;
using Autheris.Application.Services;
using Autheris.Application.Sql.Masking;
using Autheris.Application.Sql.Policy;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Options.Plugins;
using Autheris.Domain.Plugins.Auth;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Governance;
using Autheris.Tests.Unit.Governance.Classification;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using TrinoSqlEngine;
using TrinoSqlEngine.Governance;
using Xunit;

/// <summary>
/// Issue #1047: the <c>accessStatus</c> filter decides Discover with <see cref="IAccessDecisionPoint.DecideAllowedManyAsync"/>, which skips
/// the classification stage (and its column-row reads) whenever that stage cannot turn an allow into a deny. The allow bit must equal the
/// full decision of <see cref="IAccessDecisionPoint.DecideManyAsync"/> for every kind of grant, deny and table state.
/// </summary>
[Trait("Category", "Security")]
public sealed class AllowOnlyDiscoverDecisionTests
{
    private static readonly TenantId Tenant = new("tenant-allowonly");
    private static readonly Sid User = new("S-1-5-21-1047-1047-1047-1001");
    private static readonly Sid Group = new("S-1-5-21-1047-1047-1047-3001");
    private static readonly DateTimeOffset Now = new(2026, 11, 23, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task AllowOnly_EqualsTheFullDecision_ForEveryFixture()
    {
        var fixture = Fixture.Create();
        var pdp = fixture.CreatePdp();

        var full = await pdp.DecideManyAsync(Subject(), fixture.Tables, AccessOperation.Discover, AccessTransport.Discovery);
        var allowOnly = await pdp.DecideAllowedManyAsync(Subject(), fixture.Tables);

        Describe(fixture.Tables, allowOnly).ShouldBe(Describe(fixture.Tables, full.Select(static d => d.IsAllowed).ToList()));

        // The matrix is not trivial: both outcomes occur, and the expected ones are where the policy puts them.
        var byName = fixture.Tables.Select((t, i) => (t.Identifier.TableName, allowOnly[i])).ToDictionary(static x => x.TableName, static x => x.Item2);
        byName["plain"].ShouldBeTrue();
        byName["per_cell"].ShouldBeTrue();
        byName["column_deny"].ShouldBeTrue();
        byName["denied"].ShouldBeFalse();
        byName["no_grant"].ShouldBeFalse();
        byName["inactive"].ShouldBeFalse();
        byName["expired"].ShouldBeFalse();
    }

    [Fact]
    public async Task AllowOnly_EqualsTheFullDecision_WithAuthorizationHooks()
    {
        // Hooks may deny any table; with hooks the classification stage is never skipped (hooks run on the full decision).
        var hooks = new DenyingHookHost(hasHooks: true, "plain");
        var fixture = Fixture.Create();
        var pdp = fixture.CreatePdp(hooks);

        var full = await pdp.DecideManyAsync(Subject(), fixture.Tables, AccessOperation.Discover, AccessTransport.Discovery);
        var allowOnly = await pdp.DecideAllowedManyAsync(Subject(), fixture.Tables);

        Describe(fixture.Tables, allowOnly).ShouldBe(Describe(fixture.Tables, full.Select(static d => d.IsAllowed).ToList()));
        allowOnly[fixture.Tables.FindIndex(static t => t.Identifier.TableName == "plain")].ShouldBeFalse();
    }

    [Fact]
    public async Task AllowOnly_DeniesEverything_WhenGroupResolutionIsDegraded()
    {
        var fixture = Fixture.Create();
        var pdp = fixture.CreatePdp();
        var degraded = Subject() with { GroupResolutionStatus = GroupResolutionStatus.Degraded };

        var allowOnly = await pdp.DecideAllowedManyAsync(degraded, fixture.Tables);

        allowOnly.ShouldAllBe(static allowed => !allowed);
    }

    [Fact]
    public async Task DefaultInterfaceImplementation_ReturnsTheAllowBitOfDecideMany()
    {
        // Decision points without an allow-only path (test doubles, other implementations) answer with their full decisions.
        var table = Fixture.Create().Tables[0];
        IAccessDecisionPoint defaultImpl = new DenyingDecisionPoint();

        var allowed = await defaultImpl.DecideAllowedManyAsync(Subject(), [table]);

        allowed.ShouldBe([false]);
    }

    [Fact]
    public async Task BatchedPageDecisions_EqualThePerTableDecisions_ForReadAndWriteOperations()
    {
        // A discovery page decides Discover, Insert, Update and Delete with one shared consent read (issue #1047); each decision must be
        // the one a single, unbatched DecideAsync returns (allow bit, deny code, write row scope and writable columns).
        var fixture = Fixture.Create();
        fixture.AddWriteGrants();
        var pdp = fixture.CreatePdp();
        AccessOperation[] operations = [AccessOperation.Discover, AccessOperation.Insert, AccessOperation.Update, AccessOperation.Delete];

        var batched = await pdp.DecideOperationsManyAsync(Subject(), fixture.Tables, operations, AccessTransport.Discovery);

        for (var op = 0; op < operations.Length; op++)
        {
            for (var t = 0; t < fixture.Tables.Count; t++)
            {
                var single = await pdp.DecideAsync(new AccessRequest(Subject(), fixture.Tables[t], operations[op], AccessTransport.Discovery));
                Describe(batched[op][t]).ShouldBe(Describe(single), $"{operations[op]} on {fixture.Tables[t].Identifier}");
            }
        }

        var plain = fixture.Tables.FindIndex(static t => t.Identifier.TableName == "plain");
        batched[1][plain].IsAllowed.ShouldBeTrue();
        batched[3][plain].IsAllowed.ShouldBeFalse();
        batched[1][fixture.Tables.FindIndex(static t => t.Identifier.TableName == "denied")].IsAllowed.ShouldBeFalse();
    }

    private static string Describe(TableAccessDecision d) =>
        $"{d.IsAllowed}|{d.WriteGrant?.DenyCode}|{d.WriteGrant?.RowScope}|{string.Join(",", d.WriteGrant?.WritableColumns.Order(StringComparer.OrdinalIgnoreCase).ToArray() ?? [])}|{d.DenialKind}";

    private static string Describe(IReadOnlyList<TableMetadata> tables, IReadOnlyList<bool> allowed) =>
        string.Join(";", tables.Select((t, i) => $"{t.Identifier.TableName}={allowed[i]}"));

    private static SecurityPrincipalContext Subject() => new()
    {
        IsAuthenticated = true,
        AuthenticationScheme = "Test",
        UserSid = User,
        ResolvedTenant = Tenant,
        GroupResolutionStatus = GroupResolutionStatus.Complete,
        GroupSids = new HashSet<Sid> { Group },
        TenantRoles = new HashSet<string>(),
        ClusterRoles = new HashSet<string>(),
        Claims = [new Claim("scp", "Autheris.Catalog.Read")]
    };

    /// <summary>A decision point without an allow-only path that denies everything.</summary>
    private sealed class DenyingDecisionPoint : IAccessDecisionPoint
    {
        public ValueTask<TableAccessDecision> DecideAsync(AccessRequest request, CancellationToken ct = default) =>
            ValueTask.FromResult(TableAccessDecision.Denied(request.Object.Identifier, "no"));

        public ValueTask<IReadOnlyList<TableAccessDecision>> DecideManyAsync(SecurityPrincipalContext subject, IReadOnlyList<TableMetadata> objects, AccessOperation operation, AccessTransport transport, CancellationToken ct = default) =>
            ValueTask.FromResult<IReadOnlyList<TableAccessDecision>>(objects.Select(static o => TableAccessDecision.Denied(o.Identifier, "no")).ToList());

        public ValueTask<AccessExplanation> ExplainAsync(AccessRequest request, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class Fixture
    {
        public List<TableMetadata> Tables { get; } = [];

        public FakeConsentRepository Consents { get; } = new();

        public TestInMemoryClassificationStore Classifications { get; } = new(Tenant.Value);

        public static Fixture Create()
        {
            var f = new Fixture();
            var plain = f.Table("dbo", "plain", classify: true);
            f.Grant(plain);

            var denied = f.Table("dbo", "denied", classify: true);
            f.Grant(denied);
            f.Add(Consent(denied.Identifier, User, GranteeType.User, ConsentEffect.Deny));

            f.Table("dbo", "no_grant", classify: true);

            var columnDeny = f.Table("dbo", "column_deny", classify: true);
            f.Grant(columnDeny);
            f.Add(Consent(columnDeny.Identifier, Group, GranteeType.Group, ConsentEffect.Deny,
                columnRules: [new ConsentColumnRule { ColumnName = "ssn", AccessLevel = ColumnAccessLevel.Deny }]));

            // Two row-filtered grants on a classified table: the per-cell compilation applies, so allow-only evaluates the stage.
            var perCell = f.Table("dbo", "per_cell", classify: true);
            f.Add(Consent(perCell.Identifier, Group, GranteeType.Group, ConsentEffect.Allow, rowFilters: [RowFilter("region", "EU")]));
            f.Add(Consent(perCell.Identifier, User, GranteeType.User, ConsentEffect.Allow, rowFilters: [RowFilter("tier", "VIP")]));

            var inactive = f.Table("dbo", "inactive", classify: true, active: false);
            f.Grant(inactive);

            var expired = f.Table("dbo", "expired", classify: false);
            f.Add(Consent(expired.Identifier, Group, GranteeType.Group, ConsentEffect.Allow, validFrom: Now.AddDays(-10), validTo: Now.AddDays(-1)));

            var future = f.Table("dbo", "future", classify: false);
            f.Add(Consent(future.Identifier, Group, GranteeType.Group, ConsentEffect.Allow, validFrom: Now.AddDays(1), validTo: Now.AddDays(10)));

            // Pattern grants: an allow over a schema (one visible, one hidden table) and a pattern deny over a directly granted table.
            f.Table("pat", "visible", classify: true);
            f.Table("pat", "hidden", classify: true, hidden: true);
            f.Add(Pattern("prod.pat.*", ConsentEffect.Allow));
            var patternDenied = f.Table("pd", "granted", classify: true);
            f.Grant(patternDenied);
            f.Add(Pattern("prod.pd.*", ConsentEffect.Deny));
            return f;
        }

        /// <summary>Write grants: insert and update on "plain" and "denied" (the latter also carries a table deny), an expired insert on "expired".</summary>
        public void AddWriteGrants()
        {
            foreach (var name in new[] { "plain", "denied" })
            {
                var table = Tables.Single(t => t.Identifier.TableName == name);
                var grant = Consent(table.Identifier, Group, GranteeType.Group, ConsentEffect.Allow);
                grant.WriteRules =
                [
                    new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id", "region"], WriteRowScope.AllRows),
                    new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["tier"], WriteRowScope.AllRows)
                ];
                Add(grant);
            }

            var expired = Consent(Tables.Single(static t => t.Identifier.TableName == "expired").Identifier, User, GranteeType.User, ConsentEffect.Allow,
                validFrom: Now.AddDays(-10), validTo: Now.AddDays(-1));
            expired.WriteRules = [new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id"], WriteRowScope.AllRows)];
            Add(expired);
        }

        public AccessDecisionPoint CreatePdp(IAuthPluginHost? hooks = null)
        {
            var time = new AdjustableClassificationTimeProvider(Now);
            var options = new GatewayOptions
            {
                DataMasking = new DataMaskingOptions(),
                Plugins = hooks is null ? new PluginsOptions() : new PluginsOptions { Auth = new AuthPluginOptions { Enabled = true, AllowAuthorizationHooks = true } }
            };
            var masking = new SqlDataMaskingProvider(Options.Create(options), timeProvider: time);
            var typed = new TypedPolicySources(
                new RowPolicyFactory(new PolicyPredicateRenderer()),
                new ColumnMaskPolicyFactory(masking),
                ClassificationResolver: new ClassificationAccessResolver(masking, Classifications, time));
            return new AccessDecisionPoint(
                Options.Create(options),
                resolutionService: new ConsentResolutionService(logger: NullLogger<ConsentResolutionService>.Instance),
                consentRepository: Consents,
                cacheService: NullConsentCacheService.Instance,
                mandatoryFilters: NullMandatoryRowFilterResolver.Instance,
                typedPolicies: typed,
                authPluginHost: hooks,
                timeProvider: time,
                rowScopeCompiler: new RowScopeCompiler(
                    new EmptySnapshot(),
                    new StructuredFilterSqlBuilder(),
                    rowPolicyFactory: new RowPolicyFactory(new PolicyPredicateRenderer(), new PolicyExpressionParser(FastSqlEngine.Default))));
        }

        private TableMetadata Table(string schema, string name, bool classify, bool active = true, bool hidden = false)
        {
            var columns = new[] { "id", "region", "tier", "email", "ssn" }
                .Select(static c => new TableColumn { Id = Guid.NewGuid(), ColumnName = c, DataType = "TEXT" })
                .ToList();
            if (classify)
            {
                foreach (var col in columns.Where(static c => c.ColumnName is "email" or "ssn"))
                {
                    Classifications.ColumnClassifications.Add(new ColumnClassification
                    {
                        TableColumnId = col.Id.ToString(), Scope = ClassificationScopes.Global, Code = ClassificationScopes.PiiCode,
                        Source = "test", AssignedBy = "test", AssignedAt = Now
                    });
                }
            }

            var table = new TableMetadata
            {
                Identifier = new TableIdentifier("prod", schema, name),
                Table = new Table
                {
                    Id = Guid.NewGuid(), SourceName = "prod", SchemaName = schema, TableName = name, SourceType = "PostgreSQL",
                    IsActive = active, DiscoveryHidden = hidden
                },
                Columns = columns
            };
            Tables.Add(table);
            return table;
        }

        private void Grant(TableMetadata table) => Add(Consent(table.Identifier, Group, GranteeType.Group, ConsentEffect.Allow));

        private void Add(Consent consent) => Consents.CreateConsentAsync(consent).GetAwaiter().GetResult();

        private static Consent Consent(
            TableIdentifier table,
            Sid grantee,
            GranteeType type,
            ConsentEffect effect,
            List<ConsentColumnRule>? columnRules = null,
            List<ConsentRowFilter>? rowFilters = null,
            DateTimeOffset? validFrom = null,
            DateTimeOffset? validTo = null) => new()
        {
            Id = Guid.NewGuid(),
            TableIdentifier = table,
            TenantId = Tenant,
            GranteeType = type,
            GranteeSid = grantee,
            Effect = effect,
            ValidFrom = validFrom ?? Now.AddDays(-1),
            ValidTo = validTo ?? Now.AddDays(30),
            RowFilters = rowFilters ?? [],
            ColumnRules = columnRules ?? (effect == ConsentEffect.Allow
                ? new[] { "id", "region", "tier", "email", "ssn" }.Select(static c => new ConsentColumnRule { ColumnName = c, AccessLevel = ColumnAccessLevel.Clear }).ToList()
                : [])
        };

        private static Consent Pattern(string pattern, ConsentEffect effect) => new()
        {
            Id = Guid.NewGuid(),
            TableId = Guid.Empty,
            ObjectPattern = pattern,
            DataSource = pattern.Split('.')[0],
            ColumnSetMode = ColumnSetMode.AllNonHidden,
            WorkflowMode = ConsentWorkflowMode.External,
            TenantId = Tenant,
            GranteeType = GranteeType.Group,
            GranteeSid = Group,
            Effect = effect,
            ValidFrom = Now.AddDays(-1),
            ValidTo = Now.AddDays(30)
        };

        private static ConsentRowFilter RowFilter(string column, string value) => new()
        {
            ColumnName = column, Operator = "EQ", ValueType = "string", ValueJson = JsonSerializer.Serialize(value)
        };
    }

    private sealed class EmptySnapshot : IVirtualFilterSnapshotProvider
    {
        public ValueTask<VirtualFilterSnapshot> GetAsync(CancellationToken ct = default) => ValueTask.FromResult(VirtualFilterSnapshot.Empty);

        public void Invalidate()
        {
        }
    }
}

/// <summary>An authorization plugin host whose hook denies the tables named in <paramref name="deny"/> (issue #1047 tests).</summary>
internal sealed class DenyingHookHost(bool hasHooks, params string[] deny) : IAuthPluginHost
{
    public bool HasRoleProviders => false;
    public bool HasCallerAttributeProviders => false;
    public bool HasAuthorizationHooks => hasHooks;

    public int LoadPluginsFromDirectory(string directoryPath) => 0;

    public ValueTask<PluginRoleEnrichmentExecutionResult> EnrichRolesAsync(PluginSecurityContext context, CancellationToken ct) => throw new NotSupportedException();

    public bool TryGetCallerAttributeProvider(string kind, out IPluginCallerAttributeProvider? provider)
    {
        provider = null;
        return false;
    }

    public ValueTask<PluginAuthorizationResult> EvaluateAuthorizationHooksAsync(PluginAuthorizationContext context, CancellationToken ct) =>
        ValueTask.FromResult(new PluginAuthorizationResult
        {
            Outcome = deny.Contains(context.TableIdentifier.TableName) ? PluginAuthorizationOutcome.Deny : PluginAuthorizationOutcome.Allow
        });
}
