namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>Security review 2026-10-05 (recheck 3): E-4, E-8.</summary>
public sealed class SecurityReviewRecheck3Tests
{
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
