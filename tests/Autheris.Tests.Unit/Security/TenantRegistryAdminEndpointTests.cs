namespace Autheris.Tests.Unit.Security;

using System.Security.Claims;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestDoubles;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Shouldly;
using Xunit;

/// <summary>M-A2: the audited ClusterAdmin operation that resolves a tenant id collision.</summary>
[Trait("Category", "Security")]
public sealed class TenantRegistryAdminEndpointTests
{
    private static DefaultHttpContext Context(params string[] roles)
    {
        var claims = new List<Claim> { new("sub", "S-1-5-21-ADMIN1"), new("tenant_id", "home") };
        claims.AddRange(roles.Select(r => new Claim(ClaimTypes.Role, r)));
        return new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
    }

    private static ValueTask<TenantCollisionResolution> Resolved(string previous) =>
        new(new TenantCollisionResolution(TenantCollisionResolutionStatus.Resolved, previous));

    private static int Status(IResult result) => ((IStatusCodeHttpResult)result).StatusCode!.Value;

    [Fact]
    public async Task ClusterAdmin_Resolves_AndTheOperationIsAudited()
    {
        var registry = new StubTenantRegistry
        {
            OnResolveCollision = (t, _, _) => t == new TenantId("Acme")
                ? Resolved("acme")
                : new(new TenantCollisionResolution(TenantCollisionResolutionStatus.NotFound, null))
        };
        var audit = new InMemoryAuditLogRepository();

        var result = await TenantRegistryAdminEndpoints.ResolveCollisionAsync("Acme", null, Context("ClusterAdmin"), registry, AuditWriterTestServices.Over(audit), default);

        Status(result).ShouldBe(StatusCodes.Status200OK);
        audit.Entries.ShouldContain(e => e.EventType == AuditEventTypes.TenantCollisionResolved && e.DetailsJson!.Contains("Acme"));
    }

    [Theory]
    [InlineData("GovernanceAdmin")]
    [InlineData("TenantAdmin")]
    [InlineData("home:ClusterAdmin")]
    public async Task NonClusterAdmin_Forbidden_NothingChanges(string role)
    {
        var registry = new StubTenantRegistry();
        var audit = new InMemoryAuditLogRepository();

        var result = await TenantRegistryAdminEndpoints.ResolveCollisionAsync("Acme", null, Context(role), registry, AuditWriterTestServices.Over(audit), default);

        Status(result).ShouldBe(StatusCodes.Status403Forbidden);
        registry.ResolveCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task InvalidTenantId_BadRequest()
    {
        var registry = new StubTenantRegistry();

        var result = await TenantRegistryAdminEndpoints.ResolveCollisionAsync("not valid!", null, Context("ClusterAdmin"), registry, AuditWriterTestServices.Over(new InMemoryAuditLogRepository()), default);

        Status(result).ShouldBe(StatusCodes.Status400BadRequest);
        registry.ResolveCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task NoCollisionMark_NotFound_AndNoAuditOfAChange()
    {
        var registry = new StubTenantRegistry();
        var audit = new InMemoryAuditLogRepository();

        var result = await TenantRegistryAdminEndpoints.ResolveCollisionAsync("Acme", null, Context("ClusterAdmin"), registry, AuditWriterTestServices.Over(audit), default);

        Status(result).ShouldBe(StatusCodes.Status404NotFound);
        audit.Entries.ShouldBeEmpty();
    }

    [Fact]
    public async Task Resolve_AuditsWithoutTheRequestToken_SoADisconnectAfterTheChangeCannotDropTheRecord()
    {
        // L-7: the registry change is already committed when the audit is written.
        using var cts = new CancellationTokenSource();
        var registry = new StubTenantRegistry
        {
            OnResolveCollision = (_, _, _) => { cts.Cancel(); return Resolved("acme"); }
        };
        var audit = new CancellationCheckingAuditRepo();

        var result = await TenantRegistryAdminEndpoints.ResolveCollisionAsync("ACME", null, Context("ClusterAdmin"), registry, AuditWriterTestServices.Over(audit), cts.Token);

        Status(result).ShouldBe(StatusCodes.Status200OK);
        audit.Tokens.ShouldHaveSingleItem().CanBeCanceled.ShouldBeFalse();
    }

    // ---- L-4: the overwritten spelling is audited and returned; expectedCurrent is a compare-and-swap ---------------------------

    [Fact]
    public async Task Resolve_AuditsAndReturnsThePreviousSpelling()
    {
        var registry = new StubTenantRegistry
        {
            OnResolveCollision = (t, _, _) => t == new TenantId("ACME")
                ? Resolved("acme")
                : new(new TenantCollisionResolution(TenantCollisionResolutionStatus.NotFound, null))
        };
        var audit = new InMemoryAuditLogRepository();

        var result = await TenantRegistryAdminEndpoints.ResolveCollisionAsync("ACME", null, Context("ClusterAdmin"), registry, AuditWriterTestServices.Over(audit), default);

        Status(result).ShouldBe(StatusCodes.Status200OK);
        audit.Entries.ShouldContain(e => e.DetailsJson!.Contains("\"previousSpelling\":\"acme\"") && e.DetailsJson.Contains("\"keptSpelling\":\"ACME\""));
        var json = System.Text.Json.JsonSerializer.Serialize(((IValueHttpResult)result).Value);
        json.ShouldContain("\"previous\":\"acme\"");
    }

    [Fact]
    public async Task Resolve_ExpectedCurrentIsPassedToTheRegistry()
    {
        var registry = new StubTenantRegistry { OnResolveCollision = (_, _, _) => Resolved("acme") };

        await TenantRegistryAdminEndpoints.ResolveCollisionAsync(
            "ACME", new TenantRegistryAdminEndpoints.ResolveCollisionRequest("acme"), Context("ClusterAdmin"), registry, AuditWriterTestServices.Over(new InMemoryAuditLogRepository()), default);

        registry.ResolveCalls.ShouldBe([(new TenantId("ACME"), "acme")]);
    }

    [Fact]
    public async Task Resolve_ExpectedCurrentMismatch_Conflict_NothingAudited()
    {
        var registry = new StubTenantRegistry
        {
            OnResolveCollision = (_, _, _) => new(new TenantCollisionResolution(TenantCollisionResolutionStatus.ExpectedMismatch, "acme"))
        };
        var audit = new InMemoryAuditLogRepository();

        var result = await TenantRegistryAdminEndpoints.ResolveCollisionAsync(
            "ACME", new TenantRegistryAdminEndpoints.ResolveCollisionRequest("Acme"), Context("ClusterAdmin"), registry, AuditWriterTestServices.Over(audit), default);

        Status(result).ShouldBe(StatusCodes.Status409Conflict);
        audit.Entries.ShouldBeEmpty();
    }

    private sealed class CancellationCheckingAuditRepo : IAuditLogRepository
    {
        public List<CancellationToken> Tokens { get; } = [];
        public Task RecordAuditEventAsync(AuditLogEntry entry, CancellationToken ct = default)
        {
            Tokens.Add(ct);
            return ct.IsCancellationRequested ? Task.FromCanceled(ct) : Task.CompletedTask;
        }
        public Task RecordAuditEventAsync(AuditLogEntry entry, System.Data.Common.DbTransaction tx, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<AuditLogEntry>> GetAuditLogEntriesAsync(int limit = 100, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<IReadOnlyList<AuditLogEntry>> QueryAuditLogsAsync(string? targetTable = null, Sid? actorSid = null, DateTimeOffset? since = null, int limit = 1000, TenantId? tenantId = null, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AuditLogEntry>>([]);
        public Task<bool> VerifyAuditHashChainAsync(CancellationToken ct = default) => Task.FromResult(true);
        public Task<Autheris.Domain.Audit.AuditPurgeCheckpoint?> GetLatestPurgeCheckpointAsync(CancellationToken ct = default) => Task.FromResult<Autheris.Domain.Audit.AuditPurgeCheckpoint?>(null);
        public Task SavePurgeCheckpointAsync(Autheris.Domain.Audit.AuditPurgeCheckpoint checkpoint, CancellationToken ct = default) => Task.CompletedTask;
        public Task<(long MaxEligibleSequence, long FirstSurvivingSequence, string SurvivingPrevHash, long PurgeCount)?> PlanPurgePrefixAsync(DateTimeOffset thresholdUtc, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<int> ExecutePurgeBatchAsync(long maxSequenceInclusive, int batchSize, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
