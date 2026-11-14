namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataSources.Connectors.CrossDomain;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CrossTransportAuthorizationParityTests
{
    private static readonly IReadOnlyList<ParityScenario> Scenarios = CrossTransportParityHarness.BuildScenarios();
    private static readonly IReadOnlyDictionary<ParityScenarioId, ParityScenario> ScenarioById =
        Scenarios.ToDictionary(s => s.Id);

    public static IEnumerable<object[]> AllMatrixPairs()
    {
        foreach (var surface in CrossTransportParityHarness.AllSurfaces)
        {
            foreach (var scenario in Scenarios)
            {
                yield return [surface, scenario.Id];
            }
        }
    }

    [Fact]
    public void Matrix_complete_for_all_transports_and_sources()
    {
        var expectedTransports = Enum.GetValues<AccessTransport>()
            .Where(t => t != AccessTransport.Internal)
            .OrderBy(t => t)
            .ToArray();

        CrossTransportParityHarness.TransportToSurface.Keys
            .OrderBy(t => t)
            .ToArray()
            .ShouldBe(expectedTransports);

        CrossTransportParityHarness.AllSurfaces.ShouldContain(ParitySurface.Explain);
        CrossTransportParityHarness.AllSurfaces.ShouldContain(ParitySurface.SqlEndpointsGet);
        CrossTransportParityHarness.AllSurfaces.ShouldContain(ParitySurface.HttpApiSource);
        CrossTransportParityHarness.AllSurfaces.ShouldContain(ParitySurface.DatabricksDelegated);

        ScenarioById.Keys.OrderBy(id => id).ToArray()
            .ShouldBe(Enum.GetValues<ParityScenarioId>().OrderBy(id => id).ToArray());
    }

    [Theory]
    [MemberData(nameof(AllMatrixPairs))]
    public async Task Parity_matrix_matches_oracle_across_all_surfaces_and_scenarios(
        ParitySurface surface,
        ParityScenarioId scenarioId)
    {
        var scenario = ScenarioById[scenarioId];
        var expected = CrossTransportParityHarness.ProjectFor(surface, scenario.Expected);

        await using var harness = new CrossTransportParityHarness();
        var actual = await harness.EvaluateAsync(surface, scenario, CancellationToken.None);

        actual.Allowed.ShouldBe(expected.Allowed, $"Surface={surface}, Scenario={scenarioId} Allowed mismatch.");
        if (expected.Allowed)
        {
            actual.SsnMasked.ShouldBe(expected.SsnMasked, $"Surface={surface}, Scenario={scenarioId} SsnMasked mismatch.");
            actual.SsnDenied.ShouldBe(expected.SsnDenied, $"Surface={surface}, Scenario={scenarioId} SsnDenied mismatch.");
            actual.HasRowFilter.ShouldBe(expected.HasRowFilter, $"Surface={surface}, Scenario={scenarioId} HasRowFilter mismatch.");
            actual.HasMandatoryFilter.ShouldBe(expected.HasMandatoryFilter, $"Surface={surface}, Scenario={scenarioId} HasMandatoryFilter mismatch.");
        }
    }

    [Fact]
    public async Task Degraded_groups_without_principal_denies_across_pdp_and_olap()
    {
        var now = DateTimeOffset.UtcNow;
        var table = ScenarioById[ParityScenarioId.S02_GroupGrant].Table;
        var consent = ScenarioById[ParityScenarioId.S02_GroupGrant].Consents;
        var consentRepo = new DegradedConsentRepo(consent);
        var options = Options.Create(new GatewayOptions());
        var resolution = new ConsentResolutionService(new RowFilterSqlBuilder());

        var pdp = new AccessDecisionPoint(options, resolution, consentRepo, NullConsentCacheService.Instance);
        var olap = new DefaultCrossDomainAccessResolver(consentRepo, resolution, options: options, consentCache: NullConsentCacheService.Instance);

        var degradedSubject = new SecurityPrincipalContext
        {
            UserSid = new Sid("S-1-5-21-PARITY-BOB"),
            TenantId = CrossTransportParityHarness.TenantA,
            GroupSids = new HashSet<Sid>(),
            GroupResolutionStatus = GroupResolutionStatus.Degraded,
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "Bearer",
            IsAuthenticated = true,
            Principal = null
        };

        var pdpDecision = await pdp.DecideAsync(
            new AccessRequest(degradedSubject, table, AccessOperation.Read, AccessTransport.GraphQl),
            CancellationToken.None);
        pdpDecision.IsAllowed.ShouldBeFalse();

        var explanation = await pdp.ExplainAsync(
            new AccessRequest(degradedSubject, table, AccessOperation.Read, AccessTransport.GraphQl),
            CancellationToken.None);
        explanation.Allowed.ShouldBeFalse();

        var emptyPrincipal = new ClaimsPrincipal(new ClaimsIdentity());
        await Should.ThrowAsync<GatewayUnauthorizedException>(() =>
            olap.ResolveAccessAsync(emptyPrincipal, table.Identifier, table, CrossTransportParityHarness.TenantA, CancellationToken.None));
    }

    [Fact]
    public async Task Fail_closed_scenarios_S20_to_S22_deny_across_every_surface()
    {
        var failClosedIds = new[]
        {
            ParityScenarioId.S20_InvalidTenantClaimDenied,
            ParityScenarioId.S21_MissingUserSidDenied,
            ParityScenarioId.S22_ForeignTenantObject
        };

        await using var harness = new CrossTransportParityHarness();
        foreach (var id in failClosedIds)
        {
            var scenario = ScenarioById[id];
            foreach (var surface in CrossTransportParityHarness.AllSurfaces)
            {
                var outcome = await harness.EvaluateAsync(surface, scenario, CancellationToken.None);
                outcome.Allowed.ShouldBeFalse($"Fail-closed scenario {id} must deny on {surface}.");
            }
        }
    }

    private sealed class DegradedConsentRepo(IReadOnlyList<Consent> consents) : Autheris.Application.Common.Interfaces.IConsentRepository
    {
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, TableIdentifier table, DateTimeOffset atTime, TenantId? tenantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Consent>>(consents.Where(c => c.TenantId == tenantId && c.TableIdentifier.Equals(table)).ToList());
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles = null, DateTimeOffset? atTime = null, CancellationToken ct = default) => Task.FromResult(consents);
        public Task<IReadOnlyList<Consent>> GetAllActiveConsentsForSubjectsAsync(IEnumerable<Sid> subjects, IEnumerable<string>? roles, DateTimeOffset? atTime, TenantId? tenantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Consent>>(consents.Where(c => c.TenantId == tenantId).ToList());
        public Task<Consent> CreateConsentAsync(Consent consent, CancellationToken ct = default) => Task.FromResult(consent);
        public Task<Consent?> GetConsentByIdAsync(Guid consentId, CancellationToken ct = default) => Task.FromResult<Consent?>(null);
        public Task RevokeConsentAsync(Guid consentId, Sid revokedBySid, string reason, ConsentRevocationAuthority authority, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetExpiringConsentsAsync(DateTimeOffset threshold, int maxResults = 1000, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task ExtendConsentExpiryAsync(Guid consentId, DateTimeOffset newValidTo, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default) => Task.FromResult(new Autheris.Application.Governance.Paging.ConsentPage(consents, null));
    }
}
