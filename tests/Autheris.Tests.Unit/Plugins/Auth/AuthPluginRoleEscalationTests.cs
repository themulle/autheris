namespace Autheris.Tests.Unit.Plugins.Auth;

using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.Plugins.Auth;
using Autheris.Domain.Common;
using Autheris.Domain.Options.Plugins;
using Autheris.Domain.Plugins.Auth;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;

public sealed class AuthPluginRoleEscalationTests
{
    private static PluginSecurityContext CreateTestContext() =>
        new()
        {
            TenantId = new TenantId("tenant-1"),
            UserSid = new Sid("user-1"),
            GroupSids = new HashSet<Sid>(),
            ExistingTenantRoles = new HashSet<string>(),
            Claims = [],
            AuthenticationScheme = "Bearer"
        };

    [Theory]
    [InlineData("ClusterAdmin")]
    [InlineData("clusteradmin")]
    [InlineData("CLUSTERADMIN")]
    [InlineData("GovernanceAdmin")]
    [InlineData("governanceadmin")]
    [InlineData("SecurityAdmin")]
    [InlineData("securityadmin")]
    public void Sanitize_WhenProhibitedRolesSupplied_StripsThemAndEmitsAlert(string prohibitedRole)
    {
        var options = new AuthPluginOptions();
        var guard = new AuthPluginRoleEscalationGuard(options, NullLogger<AuthPluginRoleEscalationGuard>.Instance);

        var rawResult = new PluginRoleEnrichmentResult(
            addedRoles: [prohibitedRole, "LegitimateRole"],
            addedGroupSids: [new Sid("S-1-5-21-1-2-3-1000")]);

        var sanitized = guard.Sanitize("test-plugin", rawResult, CreateTestContext());

        sanitized.AddedRoles.ShouldNotContain(prohibitedRole);
        sanitized.AddedRoles.ShouldContain("LegitimateRole");
        sanitized.AddedRoles.Count.ShouldBe(1);
    }

    [Fact]
    public void Sanitize_WhenConfiguredProhibitedRoles_StripsCustomProhibitedRole()
    {
        var options = new AuthPluginOptions
        {
            ProhibitedRoles = ["CustomSuperuser", "RootOperator"]
        };
        var guard = new AuthPluginRoleEscalationGuard(options, NullLogger<AuthPluginRoleEscalationGuard>.Instance);

        var rawResult = new PluginRoleEnrichmentResult(
            addedRoles: ["CustomSuperuser", "RootOperator", "RegularUser"]);

        var sanitized = guard.Sanitize("test-plugin", rawResult, CreateTestContext());

        sanitized.AddedRoles.ShouldNotContain("CustomSuperuser");
        sanitized.AddedRoles.ShouldNotContain("RootOperator");
        sanitized.AddedRoles.ShouldContain("RegularUser");
        sanitized.AddedRoles.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("tenant:admin")]
    [InlineData("role:with:colon")]
    [InlineData("role;with;semicolon")]
    [InlineData("role/with/slash")]
    [InlineData("role with space")]
    [InlineData("role\0withnull")]
    [InlineData("role\r\nwithnewline")]
    public void Sanitize_WhenRoleContainsInvalidCharacters_StripsRole(string invalidRole)
    {
        var options = new AuthPluginOptions();
        var guard = new AuthPluginRoleEscalationGuard(options, NullLogger<AuthPluginRoleEscalationGuard>.Instance);

        var rawResult = new PluginRoleEnrichmentResult(
            addedRoles: [invalidRole, "Valid-Role.1_A"]);

        var sanitized = guard.Sanitize("test-plugin", rawResult, CreateTestContext());

        sanitized.AddedRoles.ShouldNotContain(invalidRole);
        sanitized.AddedRoles.ShouldContain("Valid-Role.1_A");
        sanitized.AddedRoles.Count.ShouldBe(1);
    }

    [Fact]
    public void Sanitize_WhenRoleExceeds128Chars_StripsRole()
    {
        var options = new AuthPluginOptions();
        var guard = new AuthPluginRoleEscalationGuard(options, NullLogger<AuthPluginRoleEscalationGuard>.Instance);

        string longRole = new('a', 129);
        string okRole = new('b', 128);

        var rawResult = new PluginRoleEnrichmentResult(addedRoles: [longRole, okRole]);
        var sanitized = guard.Sanitize("test-plugin", rawResult, CreateTestContext());

        sanitized.AddedRoles.ShouldNotContain(longRole);
        sanitized.AddedRoles.ShouldContain(okRole);
        sanitized.AddedRoles.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("S-1-5-18")]
    [InlineData("s-1-5-18")]
    [InlineData("S-1-5-19")]
    [InlineData("S-1-5-20")]
    [InlineData("S-1-5-32-544")]
    [InlineData("s-1-5-32-544")]
    [InlineData("S-1-5-32-545")]
    public void Sanitize_WhenReservedSystemSidsSupplied_StripsThemAndEmitsAlert(string systemSid)
    {
        var options = new AuthPluginOptions();
        var guard = new AuthPluginRoleEscalationGuard(options, NullLogger<AuthPluginRoleEscalationGuard>.Instance);

        var validSid = new Sid("S-1-5-21-12345-67890-12345-1001");
        var rawResult = new PluginRoleEnrichmentResult(
            addedRoles: ["Reader"],
            addedGroupSids: [new Sid(systemSid), validSid]);

        var sanitized = guard.Sanitize("test-plugin", rawResult, CreateTestContext());

        sanitized.AddedGroupSids.ShouldNotContain(new Sid(systemSid));
        sanitized.AddedGroupSids.ShouldContain(validSid);
        sanitized.AddedGroupSids.Count.ShouldBe(1);
    }

    [Theory]
    [InlineData("S-1")]
    [InlineData("S-1-")]
    [InlineData("S-1-ABC")]
    [InlineData("not a sid or guid!")]
    public void Sanitize_WhenInvalidSidStructure_StripsSid(string malformedSid)
    {
        var options = new AuthPluginOptions();
        var guard = new AuthPluginRoleEscalationGuard(options, NullLogger<AuthPluginRoleEscalationGuard>.Instance);

        var validSid = new Sid("S-1-5-21-100-200-300-400");
        var rawResult = new PluginRoleEnrichmentResult(
            addedRoles: ["Reader"],
            addedGroupSids: [new Sid(malformedSid), validSid]);

        var sanitized = guard.Sanitize("test-plugin", rawResult, CreateTestContext());

        sanitized.AddedGroupSids.ShouldNotContain(new Sid(malformedSid));
        sanitized.AddedGroupSids.ShouldContain(validSid);
        sanitized.AddedGroupSids.Count.ShouldBe(1);
    }

    [Fact]
    public void Sanitize_WhenRolesExceedMaxQuota_TruncatesToMaxRoles()
    {
        var options = new AuthPluginOptions { MaxEnrichedRolesPerCaller = 5 };
        var guard = new AuthPluginRoleEscalationGuard(options, NullLogger<AuthPluginRoleEscalationGuard>.Instance);

        var roles = Enumerable.Range(1, 10).Select(i => $"Role_{i}").ToList();
        var rawResult = new PluginRoleEnrichmentResult(addedRoles: roles);

        var sanitized = guard.Sanitize("test-plugin", rawResult, CreateTestContext());

        sanitized.AddedRoles.Count.ShouldBe(5);
        sanitized.AddedRoles.ShouldBe(roles.Take(5));
    }

    [Fact]
    public void Sanitize_WhenGroupSidsExceedMaxQuota_TruncatesToMaxSids()
    {
        var options = new AuthPluginOptions { MaxEnrichedGroupSidsPerCaller = 4 };
        var guard = new AuthPluginRoleEscalationGuard(options, NullLogger<AuthPluginRoleEscalationGuard>.Instance);

        var sids = Enumerable.Range(1, 10).Select(i => new Sid($"S-1-5-21-1-2-3-{i}")).ToList();
        var rawResult = new PluginRoleEnrichmentResult(addedGroupSids: sids);

        var sanitized = guard.Sanitize("test-plugin", rawResult, CreateTestContext());

        sanitized.AddedGroupSids.Count.ShouldBe(4);
        sanitized.AddedGroupSids.ShouldBe(sids.Take(4));
    }

    [Fact]
    public void Sanitize_WhenValidRolesAndSids_PreservesThem()
    {
        var options = new AuthPluginOptions();
        var guard = new AuthPluginRoleEscalationGuard(options, NullLogger<AuthPluginRoleEscalationGuard>.Instance);

        var roles = new[] { "BillingViewer", "DataScientist", "support.tier-1" };
        var sids = new[] { new Sid("S-1-5-21-100-200-300-400"), new Sid("b3f68481-9b6d-4952-ba64-07d06eec4e1f") };

        var rawResult = new PluginRoleEnrichmentResult(roles, sids);
        var sanitized = guard.Sanitize("test-plugin", rawResult, CreateTestContext());

        sanitized.AddedRoles.Count.ShouldBe(3);
        sanitized.AddedGroupSids.Count.ShouldBe(2);
    }
}
