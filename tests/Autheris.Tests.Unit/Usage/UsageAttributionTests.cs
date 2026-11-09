namespace Autheris.Tests.Unit.Usage;

using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using Autheris.Application.Usage;
using Autheris.Domain.Common;
using Shouldly;
using Xunit;

public sealed class UsageAttributionTests
{
    [Fact]
    public void Group_set_id_is_order_independent()
    {
        var sid1 = new Sid("S-1-5-21-111111111-222222222-333333333-1001");
        var sid2 = new Sid("S-1-5-21-111111111-222222222-333333333-1002");
        var sid3 = new Sid("S-1-5-21-111111111-222222222-333333333-1003");

        var setA = new[] { sid1, sid2, sid3 };
        var setB = new[] { sid3, sid1, sid2 };

        var (idA, groupsA) = UsageAttribution.FilterAndComputeGroupSet(setA);
        var (idB, groupsB) = UsageAttribution.FilterAndComputeGroupSet(setB);

        idA.ShouldBe(idB);
        idA.Length.ShouldBe(32);
        groupsA.Count.ShouldBe(3);
        groupsB.Count.ShouldBe(3);
        groupsA[0].Value.ShouldBe(groupsB[0].Value);
        groupsA[1].Value.ShouldBe(groupsB[1].Value);
        groupsA[2].Value.ShouldBe(groupsB[2].Value);
    }

    [Fact]
    public void Empty_group_set_is_none()
    {
        var (idNull, groupsNull) = UsageAttribution.FilterAndComputeGroupSet(null);
        idNull.ShouldBe(UsageAttribution.NoneGroupSetId);
        groupsNull.ShouldBeEmpty();

        var (idEmpty, groupsEmpty) = UsageAttribution.FilterAndComputeGroupSet(Array.Empty<Sid>());
        idEmpty.ShouldBe(UsageAttribution.NoneGroupSetId);
        groupsEmpty.ShouldBeEmpty();
    }

    [Fact]
    public void Well_known_sids_are_excluded()
    {
        var sids = new[]
        {
            new Sid("S-1-1-0"), // Everyone
            new Sid("S-1-2-0"), // Local
            new Sid("S-1-5-32-544"), // Built-in Administrators
            new Sid("S-1-5-11"), // Authenticated Users
            new Sid("S-1-5-2"), // Network
            new Sid("S-1-5-4"), // Interactive
            new Sid("S-1-5-15"), // This Organization
            new Sid("S-1-5-64-10"), // NTLM Authentication
            new Sid("S-1-16-8192"), // Medium Mandatory Level
            new Sid("S-1-18-1"), // Authentication Authority Asserted Identity
            new Sid("S-1-5-21-111111111-222222222-333333333-1001") // Custom domain group
        };

        var (groupSetId, filtered) = UsageAttribution.FilterAndComputeGroupSet(sids);

        filtered.Count.ShouldBe(1);
        filtered[0].Value.ShouldBe("S-1-5-21-111111111-222222222-333333333-1001");
        groupSetId.ShouldNotBe(UsageAttribution.NoneGroupSetId);
        groupSetId.Length.ShouldBe(32);
    }

    [Fact]
    public void Included_prefixes_filter_groups()
    {
        var sids = new[]
        {
            new Sid("S-1-5-21-111111111-222222222-333333333-1001"),
            new Sid("S-1-9-5-1002")
        };

        var included = new[] { "S-1-5-21-" };
        var (groupSetId, filtered) = UsageAttribution.FilterAndComputeGroupSet(sids, includedPrefixes: included);

        filtered.Count.ShouldBe(1);
        filtered[0].Value.ShouldBe("S-1-5-21-111111111-222222222-333333333-1001");
    }

    [Fact]
    public void Groups_truncated_at_max_and_counted()
    {
        var sids = new List<Sid>();
        for (int i = 0; i < 300; i++)
        {
            sids.Add(new Sid($"S-1-5-21-999999999-888888888-777777777-{i + 1000}"));
        }

        var (groupSetId, filtered) = UsageAttribution.FilterAndComputeGroupSet(sids, maxGroupsPerUser: 256);

        filtered.Count.ShouldBe(256);
        groupSetId.Length.ShouldBe(32);
    }

    [Fact]
    public void Billing_group_is_first_configured_match()
    {
        var tenant = new TenantId("tenant-alpha");
        var userGroups = new[]
        {
            new Sid("S-1-5-21-999999999-888888888-777777777-2002"),
            new Sid("S-1-5-21-999999999-888888888-777777777-2001")
        };

        var config = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["tenant-alpha"] =
            [
                "S-1-5-21-999999999-888888888-777777777-2001",
                "S-1-5-21-999999999-888888888-777777777-2002"
            ]
        };

        var billingGroup = UsageAttribution.ResolveBillingGroup(tenant, userGroups, config);
        billingGroup.ShouldBe("S-1-5-21-999999999-888888888-777777777-2001");
    }

    [Fact]
    public void Billing_group_unassigned_when_no_match()
    {
        var tenant = new TenantId("tenant-alpha");
        var userGroups = new[]
        {
            new Sid("S-1-5-21-999999999-888888888-777777777-3001")
        };

        var config = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["tenant-alpha"] =
            [
                "S-1-5-21-999999999-888888888-777777777-2001"
            ]
        };

        var billingGroup = UsageAttribution.ResolveBillingGroup(tenant, userGroups, config);
        billingGroup.ShouldBe("unassigned");
    }

    [Theory]
    [InlineData(200, UsageOutcome.Allow)]
    [InlineData(302, UsageOutcome.Allow)]
    [InlineData(403, UsageOutcome.DenyAccess)]
    [InlineData(429, UsageOutcome.DenyRateLimit)]
    [InlineData(400, UsageOutcome.DenyOther)]
    [InlineData(500, UsageOutcome.Error)]
    public void Outcome_from_status_code_when_no_fact(int statusCode, UsageOutcome expected)
    {
        var outcome = UsageAttribution.MapStatusCodeToOutcome(statusCode);
        outcome.ShouldBe(expected);
    }

    [Fact]
    public void Outcome_429_with_quota_exceeded_maps_to_deny_quota()
    {
        var outcome = UsageAttribution.MapStatusCodeToOutcome(429, "USAGE_QUOTA_EXCEEDED");
        outcome.ShouldBe(UsageOutcome.DenyQuota);
    }

    [Fact]
    public void ComputeUserKey_without_pseudonymization_returns_raw_sid()
    {
        var sid = new Sid("S-1-5-21-12345");
        UsageAttribution.ComputeUserKey(sid, pseudonymizeUsers: false).ShouldBe("S-1-5-21-12345");
    }

    [Fact]
    public void ComputeUserKey_with_pseudonymization_hashes_correctly()
    {
        var sid = new Sid("S-1-5-21-12345");
        var key = Encoding.UTF8.GetBytes("secret-test-key-32-bytes-long!!");

        string userKey = UsageAttribution.ComputeUserKey(sid, pseudonymizeUsers: true, key);
        userKey.ShouldStartWith("h:");
        userKey.Length.ShouldBe(34); // "h:" + 32 hex chars

        using var hmac = new HMACSHA256(key);
        string expectedHex = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(sid.Value))).ToLowerInvariant()[..32];
        userKey.ShouldBe("h:" + expectedHex);
    }

    [Fact]
    public void ComputeUserKey_with_pseudonymization_without_key_throws()
    {
        var sid = new Sid("S-1-5-21-12345");
        Should.Throw<InvalidOperationException>(() => UsageAttribution.ComputeUserKey(sid, pseudonymizeUsers: true, null));
    }
}
