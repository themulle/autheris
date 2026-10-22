namespace Autheris.Tests.Unit.Data;

using Autheris.Application.Services;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>VF-12: read paths scope HMAC pseudonyms with the configured DataMasking:HmacKeyId, never the literal "default".</summary>
public sealed class HmacKeyIdScopingVf12Tests
{
    [Fact]
    public void MaskingProvider_ExposesTheConfiguredHmacKeyId()
    {
        IColumnMaskingProvider provider = new ColumnMaskingProvider(Options.Create(new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacKeyId = "key-rotated-7" }
        }));

        provider.DefaultHmacKeyId.ShouldBe("key-rotated-7");
    }

    [Fact]
    public void ScopeRuleForTenant_WithProviderKeyId_UsesItInsteadOfDefault()
    {
        var rule = new MaskingRule { RuleType = "HMAC_SHA256" };

        var scoped = GatewayExecutionService.ScopeRuleForTenant(rule, "tenant-a", "key-rotated-7");

        scoped.HmacKeyId.ShouldBe("key-rotated-7|tenant:tenant-a");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void ScopeRuleForTenant_WithoutKeyId_FallsBackToDefault(string? keyId)
    {
        var scoped = GatewayExecutionService.ScopeRuleForTenant(new MaskingRule { RuleType = "HMAC_SHA256" }, "tenant-a", keyId);

        scoped.HmacKeyId.ShouldBe("default|tenant:tenant-a");
    }
}
