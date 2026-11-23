namespace Autheris.Tests.Unit.Security;

using Autheris.Application.Governance.Ownership;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Linq;
using System.Reflection;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.ClassificationClear;
using Autheris.Application.ConsentRequests.ProfileAssignments;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.ConsentRequests;
using Autheris.Application.Governance.Grants;
using Autheris.Application.Governance.Grants.Records;
using Autheris.Application.Governance.Services;
using Autheris.Application.Security.Totp.Interfaces;
using Autheris.Application.Security.Totp.Services;
using Autheris.Application.Services;
using Autheris.Application.State;
using Autheris.Domain.Common;
using Autheris.Domain.Governance;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Infrastructure.State;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using StackExchange.Redis;
using Xunit;

[Trait("Category", "Security")]
public sealed partial class AdvisoryRegressionTests
{
    // GHSA-g4xx-34fc-ghq4 (details: private advisory).
    [Theory]
    [InlineData("case_04_access_planning_group_and_degraded_sod")]
    [InlineData("case_06_grant_policy_normalized_approver_dedup")]
    [InlineData("case_09_totp_replay_lockout_and_local_purge")]
    [InlineData("case_10_totp_secret_durable_redis_and_reenroll_requires_current_code")]
    public Task Ghsa_g4xx_ghq4_regression(string bullet) => bullet switch
    {
        "case_04_access_planning_group_and_degraded_sod" => AssertAccessPlanningGroupAndDegradedSodAsync(),
        "case_06_grant_policy_normalized_approver_dedup" => Task.Run(AssertGrantPolicyNormalizedApproverDedup),
        "case_09_totp_replay_lockout_and_local_purge" => AssertTotpReplayLockoutAndLocalPurgeAsync(),
        "case_10_totp_secret_durable_redis_and_reenroll_requires_current_code" => AssertTotpSecretDurabilityAndReenrollAsync(),
        _ => throw new ArgumentOutOfRangeException(nameof(bullet), bullet, null)
    };

    private static ClaimsPrincipal Wave2Principal(
        string sid,
        string? upn = null,
        string? role = null,
        string[]? groupSids = null,
        Claim[]? extraClaims = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.PrimarySid, sid),
            new("tid", TenantA.Value),
            new("scp", "data.access.review")
        };
        if (upn is not null) claims.Add(new Claim(ClaimTypes.Upn, upn));
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        if (groupSids is not null)
        {
            foreach (var g in groupSids) claims.Add(IdentityClaimIssuers.CreateGroupSidClaim(g));
        }

        if (extraClaims is not null) claims.AddRange(extraClaims);
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    private static TableMetadata Wave2PiiTable(bool highlySensitive = false)
    {
        var tableId = Guid.NewGuid();
        return new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "orders"),
            Table = new Table { Id = tableId, SourceName = "sales", SchemaName = "dbo", TableName = "orders", IsActive = true },
            Columns = [new TableColumn { Id = Guid.NewGuid(), TableId = tableId, ColumnName = "email", DataType = "VARCHAR" }],
            ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor("email")
        };
    }


    private static async Task AssertAccessPlanningGroupAndDegradedSodAsync()
    {
        const string targetGroup = "S-1-5-21-1000-2000-3000-5002";
        const string adminSid = "S-1-5-21-PLAN-ADMIN";
        var effectiveResult = new EffectiveScimGroupsResult(new HashSet<string>(), false, false);
        var groupStore = Wave2ScimGroupProxy((_, _) => Task.FromResult(effectiveResult));

        var cluster = new InMemoryClusterStateProvider();
        var secrets = new ClusterTotpSecretStore(cluster);
        var totp = new TotpVerificationService(cluster);
        const string seed = "JBSWY3DPEHPK3PXP";
        await secrets.SetSecretAsync(adminSid, seed);

        var metaRepo = new InMemoryTableMetadataRepository();
        var meta = Wave2PiiTable();
        meta = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "public", "orders"),
            Table = new Table { Id = Guid.NewGuid(), SchemaName = "public", TableName = "orders", IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };
        await metaRepo.UpsertTableMetadataAsync(meta);

        var service = new AccessPlanningService(
            new InMemoryAuditWriter(),
            tableMetadataRepository: metaRepo,
            consentRepository: new InMemoryConsentRepository(),
            totpVerificationService: totp,
            totpSecretStore: secrets,
            grantPolicy: new GrantPolicy(),
            groupStore: groupStore);

        var planReq = new AdminPlanAccessRequest(
            "sales.public.orders",
            [new PrincipalAccessGrant($"group:{targetGroup}", new Dictionary<string, string> { ["id"] = "clear" }, null, DateTimeOffset.UtcNow.AddDays(10))],
            "Team grant");

        await Should.ThrowAsync<SecurityException>(() => service.PlanAccessAsync(
            planReq, adminSid, "tenant-a", Wave2Principal(adminSid, groupSids: [targetGroup])));
        await Should.ThrowAsync<SecurityException>(() => service.PlanAccessAsync(
            planReq, adminSid, "tenant-a", Wave2Principal(adminSid, extraClaims: [new Claim(RoleResolutionClaims.ClaimType, RoleResolutionClaims.Degraded)])));

        effectiveResult = new EffectiveScimGroupsResult(new HashSet<string>([targetGroup]), false, false);
        await Should.ThrowAsync<SecurityException>(() => service.PlanAccessAsync(planReq, adminSid, "tenant-a"));

        effectiveResult = new EffectiveScimGroupsResult(new HashSet<string>(), false, false);
        await Should.ThrowAsync<SecurityException>(() => service.PlanAccessAsync(planReq, adminSid, null));
        (await service.PlanAccessAsync(planReq, adminSid, null, Wave2Principal(adminSid))).PlanId.ShouldNotBeNullOrWhiteSpace();
        var plan = await service.PlanAccessAsync(planReq, adminSid, "tenant-a");
        var confirmed = await service.ConfirmPlanAsync(plan.PlanId, totp.GenerateTotpCode(seed), adminSid);

        effectiveResult = new EffectiveScimGroupsResult(new HashSet<string>(), false, HasNonCanonicalSid: true);
        await Should.ThrowAsync<SecurityException>(() => service.ApplyAccessAsync(
            new AdminApplyAccessRequest(plan.PlanId, confirmed.ConfirmationToken), adminSid, "tenant-a"));
    }

    private static void AssertGrantPolicyNormalizedApproverDedup()
    {
        var policy = new GrantPolicy();
        var now = DateTimeOffset.UtcNow;

        var dupApprovers = policy.Evaluate(new GrantRequest(
            GranteeType.User, "grantee-1", GrantKind.Consent, now.AddDays(10), now, GrantSensitivity.Sensitive,
            Requester: "requester@corp.com", Approvers: ["user:bob@corp.com", "BOB@CORP.COM"], Justification: "Valid"));
        dupApprovers.Allowed.ShouldBeFalse();
        dupApprovers.ErrorCode.ShouldBe(GrantErrorCodes.FourEyesRequired);

        var reqAliasApprover = policy.Evaluate(new GrantRequest(
            GranteeType.User, "grantee-1", GrantKind.Consent, now.AddDays(10), now, GrantSensitivity.Sensitive,
            Requester: "requester@corp.com", Approvers: ["user:requester@corp.com", "bob@corp.com"], Justification: "Valid"));
        reqAliasApprover.Allowed.ShouldBeFalse();
        reqAliasApprover.ErrorCode.ShouldBe(GrantErrorCodes.FourEyesRequired);

        var distinctTwo = policy.Evaluate(new GrantRequest(
            GranteeType.User, "grantee-1", GrantKind.Consent, now.AddDays(10), now, GrantSensitivity.Sensitive,
            Requester: "requester@corp.com", Approvers: ["bob@corp.com", "carol@corp.com"], Justification: "Valid"));
        distinctTwo.Allowed.ShouldBeTrue();
    }

    private static async Task AssertTotpReplayLockoutAndLocalPurgeAsync()
    {
        var svc = new TotpVerificationService(options: new TotpOptions { MaxFailedAttempts = 3, LockoutMinutes = 5 });
        const string secret = "JBSWY3DPEHPK3PXP";
        var codeNow = svc.GenerateTotpCode(secret, DateTimeOffset.UtcNow);

        (await svc.VerifyAndConsumeTotpAsync("S-1-5-21-U1", secret, codeNow)).ShouldBeTrue();
        for (int i = 0; i < 3; i++)
        {
            (await svc.VerifyAndConsumeTotpAsync("S-1-5-21-U1", secret, codeNow)).ShouldBeFalse();
        }

        var nextStepCode = svc.GenerateTotpCode(secret, DateTimeOffset.UtcNow.AddSeconds(30));
        (await svc.VerifyAndConsumeTotpAsync("S-1-5-21-U1", secret, nextStepCode)).ShouldBeFalse();

        var field = typeof(TotpVerificationService).GetField("_localFailures", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var map = (IDictionary)field.GetValue(svc)!;
        var tupleType = typeof(TotpVerificationService).GetField("_localFailures", BindingFlags.Instance | BindingFlags.NonPublic)!
            .FieldType.GenericTypeArguments[1];
        map["totp:fail:stale-user"] = Activator.CreateInstance(tupleType, 4, DateTimeOffset.UtcNow.AddSeconds(-5))!;

        var freshCode = svc.GenerateTotpCode(secret, DateTimeOffset.UtcNow);
        (await svc.VerifyAndConsumeTotpAsync("S-1-5-21-FRESH", secret, freshCode)).ShouldBeTrue();
        map.Contains("totp:fail:stale-user").ShouldBeFalse();
    }

    private static async Task AssertTotpSecretDurabilityAndReenrollAsync()
    {
        var stringSetCalls = new List<(string Method, object? Expiry)>();
        var dbProxy = Wave2Proxy<IDatabase>.Create((m, args) =>
        {
            if (m.Name == "StringSetAsync")
            {
                stringSetCalls.Add((m.Name, args is { Length: >= 3 } ? args[2] : null));
                return Task.FromResult(true);
            }

            return m.ReturnType == typeof(Task<bool>) ? Task.FromResult(true) : null;
        });
        var muxProxy = Wave2Proxy<IConnectionMultiplexer>.Create((m, _) =>
            m.Name == "GetDatabase" ? dbProxy : null);

        var redis = new RedisClusterStateProvider(muxProxy, Options.Create(new GatewayOptions()), NullLogger<RedisClusterStateProvider>.Instance);
        await redis.SetAsync("totp:secret:u1", "SEED", TimeSpan.MaxValue);
        await redis.SetAsync("totp:secret:u2", "SEED", TimeSpan.FromMinutes(10));

        stringSetCalls.Count.ShouldBe(2);
        (stringSetCalls[0].Expiry is Expiration exp0 && exp0.Equals(default(Expiration))).ShouldBeTrue();
        (stringSetCalls[1].Expiry is TimeSpan ts1 && ts1 == TimeSpan.FromMinutes(10)
            || (stringSetCalls[1].Expiry is Expiration exp1 && !exp1.Equals(default(Expiration)))).ShouldBeTrue();

        TimeSpan? recordedTtl = null;
        var stateSpy = Wave2Proxy<IDistributedClusterStateProvider>.Create((m, args) =>
        {
            if (m.Name == "SetAsync")
            {
                recordedTtl = (TimeSpan)args![2]!;
                return ValueTask.CompletedTask;
            }

            return null;
        });
        var secretStore = new ClusterTotpSecretStore(stateSpy);
        await secretStore.SetSecretAsync("S-1-5-21-U2", "JBSWY3DPEHPK3PXP");
        recordedTtl.ShouldBe(TimeSpan.MaxValue);

        var realStore = new ClusterTotpSecretStore();
        var totp = new TotpVerificationService();
        var enroll = new TotpEnrollmentService(totp, realStore);
        var b1 = (await enroll.BeginEnrollmentAsync("S-1-5-21-U3", "u3@corp.local", "Autheris"))!;
        (await enroll.CompleteEnrollmentAsync("S-1-5-21-U3", totp.GenerateTotpCode(b1.SecretBase32), null)).ShouldBe(TotpEnrollmentOutcome.Completed);

        var b2 = (await enroll.BeginEnrollmentAsync("S-1-5-21-U3", "u3@corp.local", "Autheris"))!;
        (await enroll.CompleteEnrollmentAsync("S-1-5-21-U3", totp.GenerateTotpCode(b2.SecretBase32), null))
            .ShouldBe(TotpEnrollmentOutcome.CurrentCodeRequired);

        await realStore.RemoveSecretAsync("S-1-5-21-U3");
        (await enroll.CompleteEnrollmentAsync(
            "S-1-5-21-U3",
            totp.GenerateTotpCode(b2.SecretBase32, DateTimeOffset.UtcNow.AddSeconds(30)),
            null)).ShouldBe(TotpEnrollmentOutcome.Completed);
    }

    private static IScimGroupStore Wave2ScimGroupProxy(
        Func<string, CancellationToken, Task<EffectiveScimGroupsResult>> onEffective) =>
        Wave2Proxy<IScimGroupStore>.Create((m, args) => m.Name switch
        {
            "GetEffectiveGroupSidsForUserAsync" => onEffective((string)args![2]!, (CancellationToken)args[4]!),
            "GetGroupMembershipsForUserAsync" => Task.FromResult<IReadOnlyList<string>>([]),
            _ => throw new NotSupportedException(m.Name)
        });

#pragma warning disable CA1852 // DispatchProxy requires an unsealed proxy type
    private class Wave2Proxy<T> : DispatchProxy where T : class
#pragma warning restore CA1852
    {
        private Func<MethodInfo, object?[]?, object?> _handler = null!;
        public static T Create(Func<MethodInfo, object?[]?, object?> handler)
        {
            var proxy = Create<T, Wave2Proxy<T>>();
            ((Wave2Proxy<T>)(object)proxy)._handler = handler;
            return proxy;
        }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => _handler(targetMethod!, args);
    }
}
