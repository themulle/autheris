namespace Autheris.Tests.Integration.Security;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.DataSources.Connectors.CrossDomain;
using Autheris.Application.Plugins.Auth;
using Autheris.Application.Policy.Pdp;
using Autheris.Application.Policy.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Identity;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Plugins.Auth;
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
            foreach (var scenario in Scenarios.Where(s => s.GrantModel is null))
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

        actual.Allowed.ShouldBe(expected.Allowed, $"Surface={surface}, Scenario={scenarioId} Allowed mismatch. Diagnostic: {actual.Diagnostic ?? "none"}");
        if (expected.Allowed)
        {
            actual.SsnMasked.ShouldBe(expected.SsnMasked, $"Surface={surface}, Scenario={scenarioId} SsnMasked mismatch.");
            actual.SsnDenied.ShouldBe(expected.SsnDenied, $"Surface={surface}, Scenario={scenarioId} SsnDenied mismatch.");
            actual.HasRowFilter.ShouldBe(expected.HasRowFilter, $"Surface={surface}, Scenario={scenarioId} HasRowFilter mismatch.");
            actual.HasMandatoryFilter.ShouldBe(expected.HasMandatoryFilter, $"Surface={surface}, Scenario={scenarioId} HasMandatoryFilter mismatch.");
        }
    }

    /// <summary>Allow, deny, row filter and mask give the same result on the REST and Analytics read paths, both decided by the access decision point.</summary>
    [Theory]
    [MemberData(nameof(RestAndAnalyticsScenarios))]
    public async Task Security_RestAndAnalyticsReads_DecisionParityAfterAdapterRemoval(ParityScenarioId scenarioId)
    {
        var scenario = ScenarioById[scenarioId];
        await using var harness = new CrossTransportParityHarness();

        var rest = await harness.EvaluateAsync(ParitySurface.Rest, scenario, CancellationToken.None);
        var analytics = await harness.EvaluateAsync(ParitySurface.Analytics, scenario, CancellationToken.None);

        analytics.Allowed.ShouldBe(rest.Allowed, $"Scenario={scenarioId}: REST and Analytics must agree on allow/deny. Diagnostic: {analytics.Diagnostic ?? "none"}");
        if (rest.Allowed)
        {
            analytics.SsnMasked.ShouldBe(rest.SsnMasked, $"Scenario={scenarioId} mask mismatch.");
            analytics.SsnDenied.ShouldBe(rest.SsnDenied, $"Scenario={scenarioId} denied-column mismatch.");
            analytics.HasRowFilter.ShouldBe(rest.HasRowFilter, $"Scenario={scenarioId} row filter mismatch.");
            analytics.HasMandatoryFilter.ShouldBe(rest.HasMandatoryFilter, $"Scenario={scenarioId} mandatory filter mismatch.");
        }
    }

    public static IEnumerable<object[]> RestAndAnalyticsScenarios() =>
        Scenarios.Where(s => s.GrantModel is null).Select(s => new object[] { s.Id });

    public static IEnumerable<object[]> GrantModelRows() =>
        Scenarios.Where(s => s.GrantModel is not null).Select(s => new object[] { s.Id });

    /// <summary>
    /// Rows S30..S37 (grant and pattern model): each asserts allowed, the row set, the per-cell column state and the set of contributing
    /// grant ids on the decision point, and that explain reports the same allowed flag and grant ids.
    /// </summary>
    [Theory]
    [MemberData(nameof(GrantModelRows))]
    public async Task Grant_model_rows_match_expected_decision_and_explain(ParityScenarioId scenarioId)
    {
        var scenario = ScenarioById[scenarioId];
        var expected = scenario.Expected;

        var outcome = await CrossTransportParityGrantModelRows.EvaluateAsync(scenario, CancellationToken.None);

        outcome.Allowed.ShouldBe(expected.Allowed, $"Scenario={scenarioId} Allowed mismatch.");
        outcome.ExplainAllowed.ShouldBe(outcome.Allowed, $"Scenario={scenarioId} explain must equal the decision.");
        outcome.ExplainGrantIds.OrderBy(g => g).ToArray().ShouldBe(outcome.DecisionGrantIds.OrderBy(g => g).ToArray(), $"Scenario={scenarioId} explain grants.");
        if (expected.GrantIds is not null)
        {
            outcome.DecisionGrantIds.OrderBy(g => g).ToArray().ShouldBe(expected.GrantIds.OrderBy(g => g).ToArray(), $"Scenario={scenarioId} contributing grants.");
        }

        if (expected.RowSet is not null)
        {
            outcome.RowSet.ShouldBe(expected.RowSet.Value, $"Scenario={scenarioId} row set.");
        }

        if (expected.ColumnState is not null)
        {
            foreach (var (column, level) in expected.ColumnState)
            {
                outcome.ColumnState[column].ShouldBe(level, $"Scenario={scenarioId} column {column}.");
            }
        }

        if (expected.PredicateSqlFragments is not null)
        {
            foreach (var fragment in expected.PredicateSqlFragments)
            {
                outcome.PredicateSql.ShouldContain(fragment, customMessage: $"Scenario={scenarioId} predicate shape.");
            }
        }

        if (expected.PredicateParameterValues is not null)
        {
            outcome.ParameterValues.OrderBy(v => v, StringComparer.Ordinal).ToArray()
                .ShouldBe(expected.PredicateParameterValues.OrderBy(v => v, StringComparer.Ordinal).ToArray(), $"Scenario={scenarioId} parameters.");
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
            UserSid = CrossTransportParityHarness.BobSid,
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

    /// <summary>
    /// The core grants (consents, profiles, virtual filters) have no client address dimension, so the matrix expects the same outcome for
    /// every address (S15). A restriction by address is an authorization hook; it denies fail-closed, also for an unknown address.
    /// </summary>
    [Fact]
    public async Task Client_ip_restriction_is_enforced_by_the_authorization_hook_and_fails_closed()
    {
        var scenario = ScenarioById[ParityScenarioId.S01_UserGrant];
        var pdp = CrossTransportParityHarness.BuildPdpWithAuthorizationHook(scenario, new ClientIpPrefixHook("10."));

        async Task<bool> AllowedFrom(IPAddress? ip)
        {
            var subject = ip is null
                ? Autheris.Domain.Security.SecurityPrincipalContext.FromPrincipal(scenario.Principal, scenario.EffectiveTenant)
                : CrossTransportParityHarness.SubjectWithClientIp(scenario, ip);
            var decision = await pdp.DecideAsync(new AccessRequest(subject, scenario.Table, AccessOperation.Read, AccessTransport.GraphQl), CancellationToken.None);
            return decision.IsAllowed;
        }

        (await AllowedFrom(IPAddress.Parse("10.1.2.3"))).ShouldBeTrue();
        (await AllowedFrom(IPAddress.Parse("198.51.100.77"))).ShouldBeFalse();
        (await AllowedFrom(null)).ShouldBeFalse();
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

    private sealed class ClientIpPrefixHook(string allowedPrefix) : IAuthPluginHost
    {
        public bool HasRoleProviders => false;
        public bool HasCallerAttributeProviders => false;
        public bool HasAuthorizationHooks => true;
        public int LoadPluginsFromDirectory(string directoryPath) => 0;
        public ValueTask<PluginRoleEnrichmentExecutionResult> EnrichRolesAsync(PluginSecurityContext context, CancellationToken ct) =>
            throw new NotSupportedException();
        public bool TryGetCallerAttributeProvider(string kind, out IPluginCallerAttributeProvider? provider)
        {
            provider = null;
            return false;
        }

        public ValueTask<PluginAuthorizationResult> EvaluateAuthorizationHooksAsync(PluginAuthorizationContext context, CancellationToken ct) =>
            ValueTask.FromResult(
                context.ClientIp is { } ip && !IPAddress.None.Equals(ip) && ip.ToString().StartsWith(allowedPrefix, StringComparison.Ordinal)
                    ? PluginAuthorizationResult.Allow("client-ip")
                    : PluginAuthorizationResult.Deny("CLIENT_IP_NOT_ALLOWED", hookName: "client-ip"));
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
        public Task<IReadOnlyList<Consent>> GetActiveConsentsByConsentRequestIdAsync(Guid consentRequestId, DateTimeOffset atTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<Consent>>([]);
        public Task<bool> RevokeSystemConsentAsync(Guid consentId, Guid consentRequestId, Sid revokedBySid, string reason, CancellationToken ct = default) => Task.FromResult(false);
        public Task ReplaceConsentsAsync(IReadOnlyList<Guid> revokeConsentIds, IReadOnlyList<Consent> create, Sid actor, string reason, ConsentRevocationAuthority authority, IReadOnlyList<AuditLogEntry>? auditEntries = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task<Autheris.Application.Governance.Paging.ConsentPage> GetConsentPageAsync(Autheris.Application.Governance.Paging.ConsentPageQuery query, CancellationToken ct = default) => Task.FromResult(new Autheris.Application.Governance.Paging.ConsentPage(consents, null));
    }
}
