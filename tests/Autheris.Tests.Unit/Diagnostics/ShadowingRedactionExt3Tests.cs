namespace Autheris.Tests.Unit.Diagnostics;

using System;
using System.Collections.Generic;
using Autheris.Application.Diagnostics.Shadowing;
using Shouldly;
using Xunit;

/// <summary>
/// EXT-3 / API-12: shadowed requests must not carry identity or tenant headers and must not leak literal values from
/// the query string or body to the shadow target.
/// </summary>
public sealed class ShadowingRedactionExt3Tests
{
    [Fact]
    public void IdentityAndTenantHeaders_AreNotForwarded()
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"] = "Negotiate abc",
            ["X-Forwarded-User"] = "CORP\\alice",
            ["X-Forwarded-Tenant"] = "tenant-a",
            ["X-Tenant-ID"] = "tenant-a",
            ["X-Test-User-Sid"] = "S-1-5-21-ALICE",
            ["X-Forwarded-For"] = "10.0.0.5",
            ["Content-Type"] = "application/json",
            ["traceparent"] = "00-abc-def-01"
        };

        var sanitized = PiiShadowingRedactor.RedactHeaders(headers, stripPiiHeaders: false);

        sanitized.Keys.ShouldBe(["Authorization", "Content-Type", "traceparent"], ignoreOrder: true);
        sanitized["Authorization"].ShouldBe("Bearer staging-shadow-synthetic-token");
    }

    [Fact]
    public void QueryStringLiterals_AreRedacted_PathIsKept()
    {
        var redacted = PiiShadowingRedactor.RedactPathAndQuery("/odata/hr/employees?$filter=lastname%20eq%20%27M%C3%BCller%27&$top=5");

        Uri.UnescapeDataString(redacted).ShouldNotContain("Müller");
        Uri.UnescapeDataString(redacted).ShouldContain("lastname eq '***'");
        redacted.ShouldStartWith("/odata/hr/employees?");
        redacted.ShouldContain("$top=5");
    }

    [Fact]
    public void SqlAndGraphQlStringLiterals_InBody_AreRedacted()
    {
        var sql = PiiShadowingRedactor.RedactBody("{\"sql\":\"SELECT id FROM employees WHERE lastname = 'O''Brien'\"}");
        var gql = PiiShadowingRedactor.RedactBody("{\"query\":\"{ employees(where: {lastname: {eq: \\\"Müller\\\"}}) { id } }\"}");

        sql.ShouldBe("{\"sql\":\"SELECT id FROM employees WHERE lastname = '***'\"}");
        gql.ShouldNotBeNull();
        gql.ShouldNotContain("Müller");
        gql.ShouldContain("{eq: \\\"***\\\"}");
    }
}
