namespace Autheris.Tests.Unit.AccessProfiles;

using Autheris.Tests.Unit.TestDoubles;
using System;
using System.Collections.Generic;
using System.Net;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using System.Text;
using Autheris.Api.Endpoints;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Application.Services;
using Autheris.Application.State;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;


/// <summary>WP-SC-03 (H3): group/role mappings and every identity of the caller are resolved at runtime, most restrictive wins, one cached set lookup.</summary>
public sealed class AccessProfileRuntimeResolutionTests
{
    private static readonly TableIdentifier TelemetryTable = new("telemetry", "tem", "gps_position");
    private static readonly TenantId TenantLiebherr = new("liebherr");

    private static TableMetadata CreateTelemetryTableMetadata()
    {
        return new TableMetadata
        {
            Identifier = TelemetryTable,
            Table = new Table
            {
                SourceName = "telemetry",
                SchemaName = "tem",
                TableName = "gps_position",
                SourceType = "PostgreSql",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { ColumnName = "crane_id", DataType = "VARCHAR(64)", IsSensitive = false },
                new TableColumn { ColumnName = "latitude", DataType = "DOUBLE PRECISION", IsSensitive = true },
                new TableColumn { ColumnName = "longitude", DataType = "DOUBLE PRECISION", IsSensitive = true },
                new TableColumn { ColumnName = "customer_id", DataType = "VARCHAR(128)", IsSensitive = true },
                new TableColumn { ColumnName = "status", DataType = "VARCHAR(32)", IsSensitive = false }
            ],
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["latitude"] = new MaskingRule { RuleType = "GEO_JITTER", Mode = "round", Decimals = 2 },
                ["longitude"] = new MaskingRule { RuleType = "GEO_JITTER", Mode = "round", Decimals = 2 },
                ["customer_id"] = new MaskingRule { RuleType = "PARTIAL_MASK", KeepPrefix = 2, KeepSuffix = 2 }
            }
        };
    }

    private static TableAccessPolicy CreatePolicy(
        IAccessProfileRepository profileRepo,
        IMemoryCache? memoryCache = null,
        Autheris.Application.Policy.Interfaces.IAccessProfileCache? accessProfileCache = null)
    {
        var consentRepo = Substitute.For<IConsentRepository>();
        var resolutionService = Substitute.For<IConsentResolutionService>();
        // SR-M-07: an access profile never creates an allow; the underlying consent decision allows the table here.
        resolutionService.ResolveAccess(default!, default!, default!, default, default!, default, default, default)
            .ReturnsForAnyArgs(ci => TableAccessDecision.Allowed(
                ci.ArgAt<TableIdentifier>(3), new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true));
        var cacheService = Substitute.For<IConsentCacheService>();
        var policyEnforcementService = Substitute.For<IPolicyEnforcementService>();
        var rebacEvaluator = Substitute.For<IRebacEvaluator>();
        var clientIpResolver = Substitute.For<IClientIpResolver>();
        var mandatoryFilters = NullMandatoryRowFilterResolver.Instance;

        var options = new GatewayOptions();

        return new TableAccessPolicy(
            consentRepo,
            resolutionService,
            cacheService,
            policyEnforcementService,
            rebacEvaluator,
            clientIpResolver,
            options,
            mandatoryFilters,
            contractManager: null,
            accessProfileRepository: profileRepo,
            memoryCache: memoryCache,
            accessProfileCache: accessProfileCache);
    }


    private static AccessProfile Profile(string id, MaskingPolicyMode mode, params AccessProfileAssignment[] assignments)
    {
        var p = new AccessProfile { ProfileId = id, Name = id, TenantId = TenantLiebherr, MaskingMode = mode, TargetTables = ["tem.*"], CreatedAt = DateTimeOffset.UtcNow };
        p.Assignments.AddRange(assignments);
        return p;
    }

    private static AccessProfileAssignment Mapping(AccessProfileSubjectType type, string subject) =>
        new() { Subject = subject, SubjectType = type, AssignedAt = DateTimeOffset.UtcNow, CreatedBy = "adm" };

    private static async Task<TableAccessDecision> DecideAsync(InMemoryAccessProfileRepository repo, TableAccessQuery query, bool useCache)
    {
        Autheris.Application.Policy.Interfaces.IAccessProfileCache? cache = useCache
            ? new Autheris.Application.Policy.Services.AccessProfileCache(repo, null, new MemoryCache(new MemoryCacheOptions()))
            : null;
        return await CreatePolicy(repo, accessProfileCache: cache).DecideAsync(query, CancellationToken.None);
    }

    private static TableAccessQuery Query(string sid, string[]? groups = null, string[]? roles = null, string[]? allSids = null) => new(
        new Sid(sid), TenantLiebherr, (groups ?? []).Select(g => new Sid(g)).ToHashSet(), (roles ?? []).ToHashSet(), CreateTelemetryTableMetadata(),
        AllUserSids: allSids?.Select(s => new Sid(s)).ToHashSet());

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SEC_SC_04_GroupMapping_IsResolvedAtRuntime(bool useCache)
    {
        var repo = new InMemoryAccessProfileRepository();
        await repo.UpsertProfileAsync(Profile("strict-g", MaskingPolicyMode.Strict, Mapping(AccessProfileSubjectType.Group, "S-1-5-21-ANALYSTS")));

        var decision = await DecideAsync(repo, Query("S-1-5-21-ALICE", groups: ["S-1-5-21-ANALYSTS"]), useCache);

        decision.GetEffectiveColumnAccess("latitude", CreateTelemetryTableMetadata()).ShouldBe(ColumnAccessLevel.Deny);
        (await DecideAsync(repo, Query("S-1-5-21-ALICE"), useCache)).GetEffectiveColumnAccess("latitude", CreateTelemetryTableMetadata()).ShouldNotBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    public async Task SEC_SC_04_RoleMapping_IsResolvedAtRuntime()
    {
        var repo = new InMemoryAccessProfileRepository();
        await repo.UpsertProfileAsync(Profile("strict-r", MaskingPolicyMode.Strict, Mapping(AccessProfileSubjectType.Role, "Analyst")));

        var decision = await DecideAsync(repo, Query("S-1-5-21-ALICE", roles: ["analyst"]), useCache: true);

        decision.GetEffectiveColumnAccess("latitude", CreateTelemetryTableMetadata()).ShouldBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    public async Task SEC_SC_04_ProfileOfASecondIdentity_IsNotHiddenByTheFirstSid()
    {
        var repo = new InMemoryAccessProfileRepository();
        await repo.UpsertProfileAsync(Profile("strict-u", MaskingPolicyMode.Strict, Mapping(AccessProfileSubjectType.User, "alias:alice@corp") with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(10) }));

        var decision = await DecideAsync(repo, Query("S-1-5-21-ALICE", allSids: ["S-1-5-21-ALICE", "alias:alice@corp"]), useCache: true);

        decision.GetEffectiveColumnAccess("latitude", CreateTelemetryTableMetadata()).ShouldBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    public async Task SEC_SC_04_MostRestrictiveProfileWinsAcrossAllMatchedProfiles()
    {
        var repo = new InMemoryAccessProfileRepository();
        await repo.UpsertProfileAsync(Profile("a-default", MaskingPolicyMode.Default, Mapping(AccessProfileSubjectType.User, "S-1-5-21-ALICE") with { ExpiresAt = DateTimeOffset.UtcNow.AddDays(10) }));
        await repo.UpsertProfileAsync(Profile("z-strict", MaskingPolicyMode.Strict, Mapping(AccessProfileSubjectType.Group, "S-1-5-21-ANALYSTS")));

        var decision = await DecideAsync(repo, Query("S-1-5-21-ALICE", groups: ["S-1-5-21-ANALYSTS"]), useCache: true);

        decision.GetEffectiveColumnAccess("latitude", CreateTelemetryTableMetadata()).ShouldBe(ColumnAccessLevel.Deny);
    }

    [Fact]
    public void SEC_SC_04_SubjectSetKey_IsOrderAndCaseInsensitive_AndSeparatesSubjectTypes()
    {
        var key = Autheris.Application.Policy.Services.AccessProfileCache.SubjectSetKey(["a", "B"], ["g"], ["r"]);

        Autheris.Application.Policy.Services.AccessProfileCache.SubjectSetKey(["b", "A", " a "], ["G"], ["R"]).ShouldBe(key);
        Autheris.Application.Policy.Services.AccessProfileCache.SubjectSetKey(["a", "B"], ["r"], ["g"]).ShouldNotBe(key);
        Autheris.Application.Policy.Services.AccessProfileCache.SubjectSetKey(["a"], [], []).ShouldNotBe(Autheris.Application.Policy.Services.AccessProfileCache.SubjectSetKey([], ["a"], []));
        Autheris.Application.Policy.Services.AccessProfileCache.SubjectSetKey([], [" "], []).ShouldBeNull();
    }
}
