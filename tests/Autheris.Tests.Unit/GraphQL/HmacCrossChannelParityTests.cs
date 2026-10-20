namespace Autheris.Tests.Unit.GraphQL;

using System;
using System.Text;
using System.Text.RegularExpressions;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.GraphQL.Catalog;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class HmacCrossChannelParityTests
{
    private static readonly Regex HmacHexRegex = new("^[a-fA-F0-9]{64}$", RegexOptions.Compiled);

    [Fact]
    public void TenantScopedHmac_ProducesIdenticalHash_AcrossCallsForSameTenant()
    {
        var secretProvider = Substitute.For<IKeyVaultSecretProvider>();
        secretProvider.GetSecretBytes(Arg.Any<string>()).Returns(Encoding.UTF8.GetBytes("master-secret-key-for-hmac-tests"));

        var maskingProvider = new ColumnMaskingProvider();

        var baseRule = new MaskingRule { RuleType = "HMAC" };
        var tenant = new TenantId("tenant-alpha");

        var scopedRule1 = MaskingRule.CreateTenantScopedHmacRule(baseRule, tenant, "default");
        var scopedRule2 = MaskingRule.CreateTenantScopedHmacRule(baseRule, tenant, "default");

        var hash1 = maskingProvider.MaskValue("ssn", 123456789, scopedRule1)?.ToString();
        var hash2 = maskingProvider.MaskValue("ssn", 123456789, scopedRule2)?.ToString();

        hash1.ShouldNotBeNull();
        hash2.ShouldNotBeNull();
        hash1.Length.ShouldBe(64);
        HmacHexRegex.IsMatch(hash1).ShouldBeTrue();
        hash1.ShouldBe(hash2);
    }

    [Fact]
    public void TenantScopedHmac_IsIsolated_BetweenDifferentTenants()
    {
        var secretProvider = Substitute.For<IKeyVaultSecretProvider>();
        secretProvider.GetSecretBytes(Arg.Any<string>()).Returns(Encoding.UTF8.GetBytes("master-secret-key-for-hmac-tests"));

        var maskingProvider = new ColumnMaskingProvider();

        var baseRule = new MaskingRule { RuleType = "HMAC" };
        var tenantA = new TenantId("tenant-alpha");
        var tenantB = new TenantId("tenant-beta");

        var scopedRuleA = MaskingRule.CreateTenantScopedHmacRule(baseRule, tenantA, "default");
        var scopedRuleB = MaskingRule.CreateTenantScopedHmacRule(baseRule, tenantB, "default");

        var hashA = maskingProvider.MaskValue("ssn", 123456789, scopedRuleA)?.ToString();
        var hashB = maskingProvider.MaskValue("ssn", 123456789, scopedRuleB)?.ToString();

        hashA.ShouldNotBeNull();
        hashB.ShouldNotBeNull();
        hashA.ShouldNotBe(hashB);
    }

    [Theory]
    [InlineData("a3c5d6e7f80123456789abcdef0123456789abcdef0123456789abcdef012345", true)]
    [InlineData("A3C5D6E7F80123456789ABCDEF0123456789ABCDEF0123456789ABCDEF012345", true)]
    [InlineData("12345", false)]
    [InlineData("not-a-hash", false)]
    [InlineData("a3c5d6e7f80123456789abcdef0123456789abcdef0123456789abcdef01234z", false)]
    public void IsHmacHexString_ValidatesFormat(string candidate, bool expected)
    {
        // Testing CatalogGraphQlTypeModule.IsHmacHexString logic
        var result = CatalogGraphQlTypeModule.IsHmacHexString(candidate);
        result.ShouldBe(expected);
    }
}
