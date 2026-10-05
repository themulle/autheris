namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Security.Rebac.Services;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>Security review 2026-10-05 (recheck 3): E-3, E-4, E-8.</summary>
public sealed class SecurityReviewRecheck3Tests
{
    // ---------- E-3: ReBAC batch-check must not evaluate foreign tenants ----------

    [Fact]
    public async Task E3_BatchCheck_ItemOfForeignTenant_IsDenied()
    {
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        await store.AddTupleAsync(new RebacTuple("tenant-b", "user:alice", "viewer", "doc:1"));
        var evaluator = new ZanzibarRebacEvaluator(
            store,
            Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true, MaxTraversalDepth = 5, CacheTtlSeconds = 10 } }),
            NullLogger<ZanzibarRebacEvaluator>.Instance);

        var foreign = new RebacCheckRequest("tenant-b", "user:alice", "viewer", "doc:1");
        var result = await evaluator.BatchCheckAsync(new RebacBatchCheckRequest("tenant-a", [foreign]));

        result.Decisions[foreign].ShouldBeFalse();
        (await evaluator.CheckAsync(foreign)).Allowed.ShouldBeTrue(); // the tuple exists, only the batch tenant differs
    }

    [Fact]
    public async Task E3_DecisionCache_IsBounded()
    {
        var store = new InMemoryRebacStore(NullLogger<InMemoryRebacStore>.Instance);
        var evaluator = new ZanzibarRebacEvaluator(
            store,
            Options.Create(new GatewayOptions { Rebac = new RebacOptions { Enabled = true, MaxTraversalDepth = 2, CacheTtlSeconds = 600 } }),
            NullLogger<ZanzibarRebacEvaluator>.Instance);

        for (int i = 0; i < ZanzibarRebacEvaluator.MaxCachedDecisionsPerTenant + 50; i++)
        {
            await evaluator.CheckAsync(new RebacCheckRequest("tenant-a", $"user:{i}", "viewer", "doc:x"));
        }

        var cacheField = typeof(ZanzibarRebacEvaluator).GetField("_cache", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var cache = (System.Collections.IDictionary)cacheField.GetValue(evaluator)!;
        var tenantCache = (System.Collections.ICollection)cache["tenant-a"]!;
        tenantCache.Count.ShouldBeLessThanOrEqualTo(ZanzibarRebacEvaluator.MaxCachedDecisionsPerTenant);
    }

    // ---------- E-4: REGEX masking must not return clear text when the pattern does not match ----------

    [Theory]
    [InlineData("123 45 6789")]  // other separator
    [InlineData("ab-cd-efgh")]   // other characters
    public void E4_RegexMask_NoMatch_IsRedacted(string value)
    {
        var rule = new MaskingRule { RuleType = "REGEX", PatternOrFormat = @"^\d{3}-\d{2}-(\d{4})$", Replacement = "***-**-$1" };

        new ColumnMaskingProvider().MaskValue("ssn", value, rule).ShouldBe("REDACTED");
    }

    [Fact]
    public void E4_RegexMask_Match_IsMasked()
    {
        var rule = new MaskingRule { RuleType = "REGEX", PatternOrFormat = @"^\d{3}-\d{2}-(\d{4})$", Replacement = "***-**-$1" };

        new ColumnMaskingProvider().MaskValue("ssn", "123-45-6789", rule).ShouldBe("***-**-6789");
    }

    // ---------- E-8: tenant-scoped token revocation ----------

    private static ClaimsPrincipal Principal(string sid, string tenant, params string[] roles)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, sid), new("sub", sid), new("tenant_id", tenant) };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
    }

    [Fact]
    public void E8_TenantScopedKey_OnlyMatchesTokensOfThatTenant()
    {
        string key = TokenRevocationKeys.TenantScoped(new TenantId("tenant-a"), "S-1-5-21-VICTIM");

        TokenRevocationKeys.GetLookupKeys(Principal("S-1-5-21-VICTIM", "tenant-a")).ShouldContain(key);
        TokenRevocationKeys.GetLookupKeys(Principal("S-1-5-21-VICTIM", "tenant-b")).ShouldNotContain(key);
    }
}
