namespace Autheris.Tests.Unit.Identity;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Identity;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Unit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class IdentityPipelineSubjectRoleTests
{
    private static readonly string[] None = [];

    private static RoleResolution Resolution(params string[] roles) =>
        new(new HashSet<string>(), new HashSet<string>(roles, StringComparer.OrdinalIgnoreCase), new Dictionary<string, string>(),
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5), "resolved");

    private static IRoleResolver Resolver(RoleResolution? resolution = null) => new FakeRoleResolver(resolution ?? Resolution());

    private static IdentityPipeline Pipeline(IdentityRealm realm, IRoleResolver resolver, IAuditWriter? audit, FakeIdentityRealmRegistry? registry = null)
    {
        return new IdentityPipeline(
            registry ?? new FakeIdentityRealmRegistry(realm), new FakeGroupResolver(), resolver, null, null,
            Options.Create(new GatewayOptions()), null, null, audit);
    }

    private static async Task<ClaimsPrincipal> Run(IdentityPipeline pipeline, ClaimsIdentity token) =>
        await pipeline.ProcessAsync(new ClaimsPrincipal(token), "Bearer", null, CancellationToken.None);

    private static string[] Roles(ClaimsPrincipal principal) => [.. principal.FindAll(ClaimTypes.Role).Select(c => c.Value)];

    private static RecordingAuditWriter Audit(List<AuditLogEntry>? sink = null) => new(sink);

    [Fact]
    public async Task Mapped_ClusterAdmin_IsCanonicalClusterAdmin()
    {
        var realm = SubjectRoleMappingTests.Realm();

        var principal = await Run(Pipeline(realm, Resolver(), Audit()), SubjectRoleMappingTests.Token());

        principal.Identity!.IsAuthenticated.ShouldBeTrue();
        Roles(principal).ShouldContain("ClusterAdmin");
        ClusterAdminPolicy.IsCanonicalClusterAdmin(principal).ShouldBeTrue();
    }

    [Fact]
    public async Task OtherSubject_GetsNoMappedRole()
    {
        var principal = await Run(Pipeline(SubjectRoleMappingTests.Realm(), Resolver(), Audit()), SubjectRoleMappingTests.Token("system:serviceaccount:data:other"));

        Roles(principal).ShouldBeEmpty();
        ClusterAdminPolicy.IsCanonicalClusterAdmin(principal).ShouldBeFalse();
    }

    [Fact]
    public async Task IssuerNotOfRealm_DefaultRealmFallback_NoMapping()
    {
        var realm = SubjectRoleMappingTests.Realm();
        var audit = Audit();

        var principal = await Run(Pipeline(realm, Resolver(), audit), SubjectRoleMappingTests.Token(iss: "https://unknown.example"));

        Roles(principal).ShouldBeEmpty();
        audit.Written.ShouldBe(0);
    }

    [Fact]
    public async Task Mapped_Roles_AreUnionWithProviderAndTokenRoles()
    {
        var realm = SubjectRoleMappingTests.Realm(roles: [GatewayRole.TenantAdmin]);
        var token = SubjectRoleMappingTests.Token(extra: new Claim(ClaimTypes.Role, "TokenRole"));

        var principal = await Run(Pipeline(realm, Resolver(Resolution("ProviderRole")), Audit()), token);

        var roles = Roles(principal);
        roles.ShouldContain("ProviderRole");
        roles.ShouldContain("TenantAdmin");
        roles.ShouldContain("TokenRole");
    }

    [Fact]
    public async Task Mapped_Identity_UsesRealmFixedTenant_IgnoresTokenTenantClaim()
    {
        var realm = SubjectRoleMappingTests.Realm();
        var token = SubjectRoleMappingTests.Token(extra: new Claim("tenant_id", "tenant-b"));

        var principal = await Run(Pipeline(realm, Resolver(), Audit()), token);

        principal.FindAll("tenant_id").Select(c => c.Value).ShouldNotContain("tenant-b");
        if (principal.Identity!.IsAuthenticated)
        {
            principal.FindAll("tenant_id").Select(c => c.Value).ShouldAllBe(v => v == "tenant-a");
        }
    }

    [Fact]
    public async Task RoleResolutionDegraded_NoMappedRolesApplied()
    {
        var degraded = Resolution() with { Status = RoleResolutionStatus.Degraded };
        var audit = Audit();

        var principal = await Run(Pipeline(SubjectRoleMappingTests.Realm(), Resolver(degraded), audit), SubjectRoleMappingTests.Token());

        Roles(principal).ShouldBeEmpty();
        principal.HasClaim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded).ShouldBeTrue();
        audit.Written.ShouldBe(0);
    }

    [Fact]
    public async Task RoleCacheHit_StillWritesAuditRecord()
    {
        var audit = Audit();
        var pipeline = Pipeline(SubjectRoleMappingTests.Realm(), Resolver(), audit);

        await Run(pipeline, SubjectRoleMappingTests.Token());
        await Run(pipeline, SubjectRoleMappingTests.Token());

        audit.Written.ShouldBe(2);
        audit.Entries.ShouldAllBe(e => e.EventType == AuditEventTypes.WorkloadRoleMappingApplied);
    }

    [Fact]
    public async Task AuditFailure_MappedRolesNotApplied()
    {
        var audit = new RecordingAuditWriter(null) { Fail = true };

        var principal = await Run(Pipeline(SubjectRoleMappingTests.Realm(), Resolver(Resolution("ProviderRole")), audit), SubjectRoleMappingTests.Token());

        Roles(principal).ShouldBe(["ProviderRole"]);
        ClusterAdminPolicy.IsCanonicalClusterAdmin(principal).ShouldBeFalse();
    }

    [Fact]
    public async Task NoAuditWriter_MappedRolesNotApplied()
    {
        var principal = await Run(Pipeline(SubjectRoleMappingTests.Realm(), Resolver(), null), SubjectRoleMappingTests.Token());

        Roles(principal).ShouldBeEmpty();
    }

    [Fact]
    public async Task AuditRecord_ContainsRealmSubjectRoles_NoTokenOrJti()
    {
        var sink = new List<AuditLogEntry>();
        var token = SubjectRoleMappingTests.Token(extra: [new Claim("jti", "jti-secret-value"), new Claim("access_token", "token-secret-value")]);

        await Run(Pipeline(SubjectRoleMappingTests.Realm(), Resolver(), Audit(sink)), token);

        var entry = sink.ShouldHaveSingleItem();
        entry.EventType.ShouldBe("WORKLOAD_ROLE_MAPPING_APPLIED");
        entry.DetailsJson.ShouldContain("workload");
        entry.DetailsJson.ShouldContain(SubjectRoleMappingTests.Subject);
        entry.DetailsJson.ShouldContain("ClusterAdmin");
        entry.DetailsJson.ShouldNotContain("jti-secret-value");
        entry.DetailsJson.ShouldNotContain("token-secret-value");
        AuditPolicyTable.For(AuditEventTypes.WorkloadRoleMappingApplied).ShouldBe(AuditFailurePolicy.FailClosed);
    }

    [Fact]
    public async Task AuditRecord_SubjectIsCapped()
    {
        var sink = new List<AuditLogEntry>();
        var longSubject = "s:" + new string('a', 254);
        var realm = SubjectRoleMappingTests.Realm(longSubject);

        await Run(Pipeline(realm, Resolver(), Audit(sink)), SubjectRoleMappingTests.Token(longSubject));

        sink.ShouldHaveSingleItem().DetailsJson.ShouldContain(longSubject);
    }

    [Fact]
    public void StartupSummary_LogsCountsOnly_NoSubjects()
    {
        var logger = new CapturingLogger();

        SubjectRoleStartupLog.Write(logger, [SubjectRoleMappingTests.Realm()]);

        logger.Entries.ShouldContain(e => e.Level == LogLevel.Information && e.Message.Contains("1 workload subjects"));
        logger.Entries.ShouldContain(e => e.Level == LogLevel.Warning && e.Message.Contains("ClusterAdmin"));
        logger.Entries.ShouldNotContain(e => e.Message.Contains(SubjectRoleMappingTests.Subject) || e.Message.Contains(SubjectRoleMappingTests.Issuer));
    }

    private sealed class FakeRoleResolver(RoleResolution resolution) : IRoleResolver
    {
        public Task<RoleResolution> ResolveAsync(ResolvedIdentity identity, IdentityRealm realm, CancellationToken cancellationToken = default) => Task.FromResult(resolution);

        public Task<RoleResolution> ResolveRolesAsync(ResolvedIdentity identity, CancellationToken cancellationToken = default) => Task.FromResult(resolution);
    }

    private sealed class FakeGroupResolver : IGroupSidResolver
    {
        public ValueTask<GroupResolution> ResolveAsync(ClaimsPrincipal normalized, IdentityRealm realm, TenantId tenant, string scheme, CancellationToken ct) =>
            ValueTask.FromResult(new GroupResolution(new HashSet<Sid>(), GroupResolutionStatus.Complete, "test", DateTimeOffset.UtcNow.AddMinutes(1)));
    }

    private sealed class RecordingAuditWriter(List<AuditLogEntry>? sink) : IAuditWriter
    {
        public bool Fail { get; init; }

        public List<AuditLogEntry> Entries { get; } = [];

        public int Written => Entries.Count;

        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            if (Fail)
            {
                throw new AuditUnavailableException("down", new InvalidOperationException());
            }

            Entries.Add(entry);
            sink?.Add(entry);
            return Task.CompletedTask;
        }

        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) => RecordRequiredAsync(entry, ct);

        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) => RecordRequiredAsync(entry, ct);
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
