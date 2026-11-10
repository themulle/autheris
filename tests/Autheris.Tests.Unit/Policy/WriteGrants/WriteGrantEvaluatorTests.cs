namespace Autheris.Tests.Unit.Policy.WriteGrants;

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.WriteGrants;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Tenancy;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class WriteGrantEvaluatorTests
{
    private static readonly TableIdentifier TestTable = new("test_domain", "dbo", "customers");
    private static readonly TenantId TestTenant = new("tenant-test");
    private static readonly Sid UserSid = new("S-1-5-21-1-2-3-1001");
    private static readonly Sid GroupSid = new("S-1-5-21-1-2-3-4001");

    private static ClaimsPrincipal CreateUserPrincipal(
        Sid? userSid = null,
        Sid? groupSid = null,
        string? role = null,
        GroupResolutionStatus? groupResolution = GroupResolutionStatus.Complete,
        bool isPluginGroup = false)
    {
        var claims = new List<Claim>();
        var sid = userSid ?? UserSid;
        claims.Add(new Claim(ClaimTypes.PrimarySid, sid.Value));
        claims.Add(new Claim("tenant_id", TestTenant.Value));

        if (groupResolution.HasValue)
        {
            claims.Add(GroupResolutionClaims.CreateMarker());
            claims.Add(GroupResolutionClaims.CreateStatus(groupResolution.Value));
        }

        if (groupSid.HasValue)
        {
            if (isPluginGroup)
            {
                claims.Add(new Claim(ClaimTypes.GroupSid, groupSid.Value.Value, ClaimValueTypes.String, "AuthPlugin"));
            }
            else
            {
                claims.Add(IdentityClaimIssuers.CreateGroupSidClaim(groupSid.Value.Value));
            }
        }

        if (!string.IsNullOrEmpty(role))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static TableMetadata CreateTableMetadata(params (string Name, bool WriteProtected)[] columns)
    {
        return new TableMetadata
        {
            Identifier = TestTable,
            Table = new Table
            {
                SourceName = "default",
                SchemaName = "dbo",
                TableName = "customers",
                IsActive = true
            },
            Columns = columns.Select(c => new TableColumn
            {
                ColumnName = c.Name,
                DataType = "varchar",
                WriteProtected = c.WriteProtected
            }).ToList()
        };
    }

    private static Consent CreateConsent(
        GranteeType granteeType = GranteeType.User,
        Sid? granteeSid = null,
        IReadOnlyList<WriteRule>? writeRules = null,
        IReadOnlyList<ConsentColumnRule>? columnRules = null,
        DateTimeOffset? validFrom = null,
        DateTimeOffset? validTo = null,
        bool isRevoked = false)
    {
        var now = DateTimeOffset.UtcNow;
        return new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = TestTable,
            TenantId = TestTenant,
            GranteeType = granteeType,
            GranteeSid = granteeSid ?? UserSid,
            ValidFrom = validFrom ?? now.AddHours(-1),
            ValidTo = validTo ?? now.AddHours(1),
            IsRevoked = isRevoked,
            WriteRules = writeRules ?? [],
            ColumnRules = columnRules ?? []
        };
    }

    [Fact]
    public async Task Insert_Allowed_OnlyForGrantedColumns()
    {
        var meta = CreateTableMetadata(("id", false), ("name", false), ("email", false));
        var rule = new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id", "name"], WriteRowScope.OwnRows);
        var consent = CreateConsent(writeRules: [rule]);

        var repo = new TestConsentRepository(consent);
        var sut = new WriteGrantEvaluator(repo);
        var query = new WriteGrantQuery(
            CreateUserPrincipal(),
            TestTenant,
            meta,
            WriteOperation.Insert,
            new HashSet<string>(["id", "name"], StringComparer.OrdinalIgnoreCase));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeTrue();
        decision.RowScope.ShouldBe(WriteRowScope.OwnRows);
        decision.WritableColumns.ShouldBe(new HashSet<string>(["id", "name"], StringComparer.OrdinalIgnoreCase));
        decision.GrantIds.ShouldBe([consent.Id.ToString()]);
        decision.DenyCode.ShouldBeNull();
    }

    [Fact]
    public async Task Insert_UngrantedColumn_IsDenied()
    {
        var meta = CreateTableMetadata(("id", false), ("name", false), ("email", false));
        var rule = new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id", "name"], WriteRowScope.OwnRows);
        var consent = CreateConsent(writeRules: [rule]);

        var repo = new TestConsentRepository(consent);
        var sut = new WriteGrantEvaluator(repo);
        var query = new WriteGrantQuery(
            CreateUserPrincipal(),
            TestTenant,
            meta,
            WriteOperation.Insert,
            new HashSet<string>(["id", "name", "email"], StringComparer.OrdinalIgnoreCase));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenyCode.ShouldBe("unauthorized_column");
    }

    [Fact]
    public async Task Update_ColumnGrant_DoesNotCoverOtherColumns()
    {
        var meta = CreateTableMetadata(("name", false), ("email", false));
        var rule = new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["name"], WriteRowScope.AllRows);
        var consent = CreateConsent(writeRules: [rule]);

        var repo = new TestConsentRepository(consent);
        var sut = new WriteGrantEvaluator(repo);
        var query = new WriteGrantQuery(
            CreateUserPrincipal(),
            TestTenant,
            meta,
            WriteOperation.Update,
            new HashSet<string>(["email"], StringComparer.OrdinalIgnoreCase));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenyCode.ShouldBe("unauthorized_column");
    }

    [Fact]
    public async Task Update_MixedScopes_UsesNarrowestScope()
    {
        var meta = CreateTableMetadata(("colA", false), ("colB", false));
        var ruleA = new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["colA"], WriteRowScope.AllRows);
        var ruleB = new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["colB"], WriteRowScope.OwnRows);
        var consent = CreateConsent(writeRules: [ruleA, ruleB]);

        var repo = new TestConsentRepository(consent);
        var sut = new WriteGrantEvaluator(repo);
        var query = new WriteGrantQuery(
            CreateUserPrincipal(),
            TestTenant,
            meta,
            WriteOperation.Update,
            new HashSet<string>(["colA", "colB"], StringComparer.OrdinalIgnoreCase));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeTrue();
        decision.RowScope.ShouldBe(WriteRowScope.OwnRows); // OwnRows wins over AllRows
    }

    [Fact]
    public async Task Update_AllRowsGrant_YieldsAllRowsScope()
    {
        var meta = CreateTableMetadata(("colA", false), ("colB", false));
        var rule = new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["colA", "colB"], WriteRowScope.AllRows);
        var consent = CreateConsent(writeRules: [rule]);

        var repo = new TestConsentRepository(consent);
        var sut = new WriteGrantEvaluator(repo);
        var query = new WriteGrantQuery(
            CreateUserPrincipal(),
            TestTenant,
            meta,
            WriteOperation.Update,
            new HashSet<string>(["colA", "colB"], StringComparer.OrdinalIgnoreCase));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeTrue();
        decision.RowScope.ShouldBe(WriteRowScope.AllRows);
    }

    [Fact]
    public async Task Delete_Denied_WhenAllowDeleteGrantsFalse()
    {
        var meta = CreateTableMetadata(("id", false));
        var rule = new WriteRule(Guid.NewGuid(), WriteOperation.Delete, [], WriteRowScope.AllRows);
        var consent = CreateConsent(writeRules: [rule]);

        var repo = new TestConsentRepository(consent);
        var sut = new WriteGrantEvaluator(repo, allowDeleteGrants: false);
        var query = new WriteGrantQuery(
            CreateUserPrincipal(),
            TestTenant,
            meta,
            WriteOperation.Delete,
            ImmutableHashSet<string>.Empty);

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenyCode.ShouldBe("delete_grants_disabled");
    }

    [Fact]
    public async Task Delete_Allowed_WithDeleteGrant_WhenSwitchOn()
    {
        var meta = CreateTableMetadata(("id", false));
        var rule = new WriteRule(Guid.NewGuid(), WriteOperation.Delete, [], WriteRowScope.AllRows);
        var consent = CreateConsent(writeRules: [rule]);

        var repo = new TestConsentRepository(consent);
        var sut = new WriteGrantEvaluator(repo, allowDeleteGrants: true);
        var query = new WriteGrantQuery(
            CreateUserPrincipal(),
            TestTenant,
            meta,
            WriteOperation.Delete,
            ImmutableHashSet<string>.Empty);

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeTrue();
        decision.RowScope.ShouldBe(WriteRowScope.AllRows);
        decision.GrantIds.ShouldBe([consent.Id.ToString()]);
    }

    [Fact]
    public async Task Delete_OwnRows_ScopePreserved()
    {
        var meta = CreateTableMetadata(("id", false));
        var rule = new WriteRule(Guid.NewGuid(), WriteOperation.Delete, [], WriteRowScope.OwnRows);
        var consent = CreateConsent(writeRules: [rule]);

        var repo = new TestConsentRepository(consent);
        var sut = new WriteGrantEvaluator(repo, allowDeleteGrants: true);
        var query = new WriteGrantQuery(
            CreateUserPrincipal(),
            TestTenant,
            meta,
            WriteOperation.Delete,
            ImmutableHashSet<string>.Empty);

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeTrue();
        decision.RowScope.ShouldBe(WriteRowScope.OwnRows);
    }

    [Fact]
    public async Task ReadConsentWithoutWriteRule_GrantsNoWrite()
    {
        var meta = CreateTableMetadata(("id", false));
        var columnRule = new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear };
        var consent = CreateConsent(columnRules: [columnRule], writeRules: []);

        var repo = new TestConsentRepository(consent);
        var sut = new WriteGrantEvaluator(repo);
        var query = new WriteGrantQuery(
            CreateUserPrincipal(),
            TestTenant,
            meta,
            WriteOperation.Insert,
            new HashSet<string>(["id"]));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenyCode.ShouldBe("no_write_grant");
    }

    [Fact]
    public async Task ClusterAdmin_WithoutWriteRule_IsDenied()
    {
        var meta = CreateTableMetadata(("id", false));
        var repo = new TestConsentRepository();
        var sut = new WriteGrantEvaluator(repo);
        var user = CreateUserPrincipal(role: "ClusterAdmin");
        var query = new WriteGrantQuery(
            user,
            TestTenant,
            meta,
            WriteOperation.Insert,
            new HashSet<string>(["id"]));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenyCode.ShouldBe("no_write_grant");
    }

    [Fact]
    public async Task WriterRoleClaim_GrantsNothing()
    {
        var meta = CreateTableMetadata(("id", false));
        var repo = new TestConsentRepository();
        var sut = new WriteGrantEvaluator(repo);
        var user = CreateUserPrincipal(role: "WebSqlWriter");
        var query = new WriteGrantQuery(
            user,
            TestTenant,
            meta,
            WriteOperation.Insert,
            new HashSet<string>(["id"]));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenyCode.ShouldBe("no_write_grant");
    }

    [Fact]
    public async Task GroupGrant_AppliesToMember_ByCanonicalGroupSid()
    {
        var meta = CreateTableMetadata(("id", false));
        var rule = new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id"], WriteRowScope.AllRows);
        var consent = CreateConsent(granteeType: GranteeType.Group, granteeSid: GroupSid, writeRules: [rule]);

        var repo = new TestConsentRepository(consent);
        var sut = new WriteGrantEvaluator(repo);
        var user = CreateUserPrincipal(groupSid: GroupSid, isPluginGroup: false);
        var query = new WriteGrantQuery(
            user,
            TestTenant,
            meta,
            WriteOperation.Insert,
            new HashSet<string>(["id"]));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeTrue();
        decision.RowScope.ShouldBe(WriteRowScope.AllRows);
        decision.GrantIds.ShouldBe([consent.Id.ToString()]);
    }

    [Fact]
    public async Task PluginEnrichedGroupSid_NeverMatchesWriteGrant()
    {
        var meta = CreateTableMetadata(("id", false));
        var rule = new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id"], WriteRowScope.AllRows);
        var consent = CreateConsent(granteeType: GranteeType.Group, granteeSid: GroupSid, writeRules: [rule]);

        var repo = new TestConsentRepository(consent);
        var sut = new WriteGrantEvaluator(repo);
        var user = CreateUserPrincipal(groupSid: GroupSid, isPluginGroup: true); // minted with "AuthPlugin" issuer
        var query = new WriteGrantQuery(
            user,
            TestTenant,
            meta,
            WriteOperation.Insert,
            new HashSet<string>(["id"]));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenyCode.ShouldBe("no_write_grant");
    }

    [Fact]
    public async Task GroupResolutionDegraded_DeniesWrite()
    {
        var meta = CreateTableMetadata(("id", false));
        var rule = new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id"], WriteRowScope.AllRows);
        var consent = CreateConsent(writeRules: [rule]);

        var repo = new TestConsentRepository(consent);
        var sut = new WriteGrantEvaluator(repo);
        var user = CreateUserPrincipal(groupResolution: GroupResolutionStatus.Degraded);
        var query = new WriteGrantQuery(
            user,
            TestTenant,
            meta,
            WriteOperation.Insert,
            new HashSet<string>(["id"]));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenyCode.ShouldBe("identity_groups_unresolved");
    }

    [Fact]
    public async Task RevokedOrExpiredGrant_GrantsNoWrite()
    {
        var meta = CreateTableMetadata(("id", false));
        var rule = new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id"], WriteRowScope.AllRows);

        var revokedConsent = CreateConsent(writeRules: [rule], isRevoked: true);
        var expiredConsent = CreateConsent(writeRules: [rule], validTo: DateTimeOffset.UtcNow.AddMinutes(-5));

        var repo = new TestConsentRepository(revokedConsent, expiredConsent);
        var sut = new WriteGrantEvaluator(repo);
        var query = new WriteGrantQuery(
            CreateUserPrincipal(),
            TestTenant,
            meta,
            WriteOperation.Insert,
            new HashSet<string>(["id"]));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenyCode.ShouldBe("no_write_grant");
    }

    [Fact]
    public async Task WriteGrant_BeforeValidFrom_IsDenied()
    {
        var meta = CreateTableMetadata(("id", false));
        var rule = new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id"], WriteRowScope.AllRows);
        var futureConsent = CreateConsent(writeRules: [rule], validFrom: DateTimeOffset.UtcNow.AddHours(2), validTo: DateTimeOffset.UtcNow.AddHours(4));

        var repo = new TestConsentRepository(futureConsent);
        var sut = new WriteGrantEvaluator(repo);
        var query = new WriteGrantQuery(
            CreateUserPrincipal(),
            TestTenant,
            meta,
            WriteOperation.Insert,
            new HashSet<string>(["id"]));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenyCode.ShouldBe("no_write_grant");
    }

    [Fact]
    public async Task AccessProfileWriteRule_WithoutMaterializedGrantRecord_GrantsNothing()
    {
        var meta = CreateTableMetadata(("id", false));
        var repo = new TestConsentRepository(); // No materialized consent record exists

        var sut = new WriteGrantEvaluator(repo);
        var query = new WriteGrantQuery(
            CreateUserPrincipal(),
            TestTenant,
            meta,
            WriteOperation.Insert,
            new HashSet<string>(["id"]));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenyCode.ShouldBe("no_write_grant");
    }

    [Fact]
    public async Task MaterializedProfileWriteGrant_ExpiresAtValidUntil()
    {
        var meta = CreateTableMetadata(("id", false));
        var rule = new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id"], WriteRowScope.AllRows);
        var now = DateTimeOffset.UtcNow;
        var expiredMaterializedGrant = CreateConsent(
            writeRules: [rule],
            validFrom: now.AddHours(-2),
            validTo: now.AddSeconds(-1));

        var repo = new TestConsentRepository(expiredMaterializedGrant);
        var sut = new WriteGrantEvaluator(repo);
        var query = new WriteGrantQuery(
            CreateUserPrincipal(),
            TestTenant,
            meta,
            WriteOperation.Insert,
            new HashSet<string>(["id"]));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenyCode.ShouldBe("no_write_grant");
    }

    [Fact]
    public async Task PolicyDependencyTable_IsDenied()
    {
        var meta = CreateTableMetadata(("id", false));
        var rule = new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id"], WriteRowScope.AllRows);
        var consent = CreateConsent(writeRules: [rule]);

        var repo = new TestConsentRepository(consent);
        var depIndex = new TestPolicyDependencyIndex(isDependency: true);

        var sut = new WriteGrantEvaluator(repo, policyDependencyIndex: depIndex);
        var query = new WriteGrantQuery(
            CreateUserPrincipal(),
            TestTenant,
            meta,
            WriteOperation.Insert,
            new HashSet<string>(["id"]));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenyCode.ShouldBe("write_grant_policy_dependency");
    }

    [Fact]
    public async Task EvaluatorException_FailsClosed()
    {
        var meta = CreateTableMetadata(("id", false));
        var repo = new TestConsentRepository { ShouldThrow = true };

        var sut = new WriteGrantEvaluator(repo);
        var query = new WriteGrantQuery(
            CreateUserPrincipal(),
            TestTenant,
            meta,
            WriteOperation.Insert,
            new HashSet<string>(["id"]));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenyCode.ShouldBe("evaluator_error");
    }

    [Fact]
    public async Task MaxRowsPerStatement_MinAcrossContributingRules()
    {
        var meta = CreateTableMetadata(("colA", false), ("colB", false));
        var ruleA = new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["colA"], WriteRowScope.AllRows, MaxRowsPerStatement: 100);
        var ruleB = new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["colB"], WriteRowScope.AllRows, MaxRowsPerStatement: 50);
        var consent = CreateConsent(writeRules: [ruleA, ruleB]);

        var repo = new TestConsentRepository(consent);
        var sut = new WriteGrantEvaluator(repo);
        var query = new WriteGrantQuery(
            CreateUserPrincipal(),
            TestTenant,
            meta,
            WriteOperation.Insert,
            new HashSet<string>(["colA", "colB"]));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeTrue();
        decision.MaxRowsPerStatement.ShouldBe(50);
    }

    [Fact]
    public async Task MultipleContributingGrantIds_Collected()
    {
        var meta = CreateTableMetadata(("colA", false), ("colB", false));
        var ruleA = new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["colA"], WriteRowScope.AllRows);
        var ruleB = new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["colB"], WriteRowScope.AllRows);
        var consentA = CreateConsent(writeRules: [ruleA]);
        var consentB = CreateConsent(writeRules: [ruleB]);

        var repo = new TestConsentRepository(consentA, consentB);
        var sut = new WriteGrantEvaluator(repo);
        var query = new WriteGrantQuery(
            CreateUserPrincipal(),
            TestTenant,
            meta,
            WriteOperation.Update,
            new HashSet<string>(["colA", "colB"]));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeTrue();
        decision.GrantIds.ShouldContain(consentA.Id.ToString());
        decision.GrantIds.ShouldContain(consentB.Id.ToString());
        decision.GrantIds.Count.ShouldBe(2);
    }

    [Fact]
    public async Task WriteProtectedColumn_IsDenied()
    {
        var meta = CreateTableMetadata(("id", false), ("secret_key", true));
        var rule = new WriteRule(Guid.NewGuid(), WriteOperation.Update, ["id", "secret_key"], WriteRowScope.AllRows);
        var consent = CreateConsent(writeRules: [rule]);

        var repo = new TestConsentRepository(consent);
        var sut = new WriteGrantEvaluator(repo);
        var query = new WriteGrantQuery(
            CreateUserPrincipal(),
            TestTenant,
            meta,
            WriteOperation.Update,
            new HashSet<string>(["id", "secret_key"]));

        var decision = await sut.EvaluateAsync(query);

        decision.IsAllowed.ShouldBeFalse();
        decision.DenyCode.ShouldBe("write_protected_column");
    }

    private sealed class TestConsentRepository : IConsentRepository
    {
        private readonly List<Consent> _consents = [];
        public bool ShouldThrow { get; set; }

        public TestConsentRepository(params Consent[] consents)
        {
            _consents.AddRange(consents);
        }

        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(
            IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
        {
            if (ShouldThrow)
            {
                throw new InvalidOperationException("Simulated database failure");
            }

            var subjectSet = subjects.ToHashSet();
            IReadOnlyList<Consent> result = _consents
                .Where(c => c.TableIdentifier.Equals(table) &&
                            (tenantId == null || c.TenantId == tenantId.Value) &&
                            c.IsActive(atTime) &&
                            c.GranteeSid.HasValue && subjectSet.Contains(c.GranteeSid.Value))
                .ToList();
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default) =>
            GetActiveConsentsForSubjectsAsync(subjects, table, atTime, null, ct);

        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>([]);

        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>([]);

        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) =>
            Task.FromResult(consent);

        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) =>
            Task.FromResult(_consents.FirstOrDefault(c => c.Id == consentId));

        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>([]);

        public Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<Consent>>([]);

        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) =>
            Task.FromResult(false);

        public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(
            Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default) =>
            throw new NotImplementedException();

        public Task ReplaceConsentsAsync(
            IReadOnlyList<Guid> revokeConsentIds,
            IReadOnlyList<Consent> create,
            Sid actor,
            string reason,
            IReadOnlyList<AuditLogEntry>? auditEntries = null,
            CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private sealed class TestPolicyDependencyIndex(bool isDependency = true) : IRowPolicyDependencyIndex
    {
        public bool IsPolicyDependency(TenantId tenant, TableIdentifier table) => isDependency;
    }
}
