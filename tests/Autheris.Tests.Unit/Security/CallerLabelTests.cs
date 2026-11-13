namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Policy.Pdp;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CallerLabelTests
{
    private static DefaultHttpContext Request(string? tokenClientId, params (string Header, string Value)[] headers)
    {
        var claims = new List<Claim> { new(ClaimTypes.PrimarySid, "S-1-5-21-1-2-3-1001") };
        if (tokenClientId is not null)
        {
            claims.Add(new Claim("azp", tokenClientId));
        }

        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "Bearer")) };
        foreach (var (header, value) in headers)
        {
            context.Request.Headers[header] = value;
        }

        return context;
    }

    [Theory]
    [InlineData(SecurityContextFactory.CallerAppHeaderName)]
    [InlineData(SecurityContextFactory.CallerAppHeaderNameAlt)]
    [InlineData(SecurityContextFactory.CallerVersionHeaderName)]
    [InlineData(SecurityContextFactory.CallerQueryHeaderName)]
    public void Header_labels_are_marked_client_asserted(string header)
    {
        var caller = SecurityContextFactory.CreateFromHttpContext(Request(null, (header, "reporting"))).Caller;

        caller.ShouldNotBeNull();
        caller.IsClientAsserted.ShouldBeTrue();
        caller.ClientId.ShouldBe("unknown");
    }

    [Fact]
    public void Token_client_id_is_not_asserted()
    {
        var caller = SecurityContextFactory.CreateFromHttpContext(Request("22222222-2222-2222-2222-222222222222")).Caller;

        caller.ShouldNotBeNull();
        caller.IsClientAsserted.ShouldBeFalse();
        caller.ClientId.ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void A_header_label_never_becomes_the_client_id()
    {
        var caller = SecurityContextFactory.CreateFromHttpContext(
            Request("22222222-2222-2222-2222-222222222222", (SecurityContextFactory.CallerAppHeaderName, "11111111-1111-1111-1111-111111111111"))).Caller;

        caller.ShouldNotBeNull();
        caller.IsClientAsserted.ShouldBeTrue();
        caller.ClientId!.ShouldNotContain("11111111");
    }

    [Fact]
    public void Audit_details_carry_asserted_label_and_client_id_separately()
    {
        var entry = new AuditLogEntry
        {
            EventType = "test",
            Caller = new CallerContext("reporting", "1.0", "daily", "22222222-2222-2222-2222-222222222222") { IsClientAsserted = true }
        };

        using var json = JsonDocument.Parse(entry.DetailsJson);
        var caller = json.RootElement.GetProperty("caller");
        caller.GetProperty("asserted").GetBoolean().ShouldBeTrue();
        caller.GetProperty("clientId").GetString().ShouldBe("22222222-2222-2222-2222-222222222222");
        caller.GetProperty("app").GetString().ShouldBe("reporting");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_labels_do_not_change_authorization_outcome(bool bypass)
    {
        var options = new GatewayOptions { Insecure = new InsecureGettingStartedOptions { danger_bypass_consent_checks = bypass } };
        var pdp = new AccessDecisionPoint(Options.Create(options));
        var meta = new TableMetadata
        {
            Table = new Table { SourceName = "default", SchemaName = "dbo", TableName = "orders", IsActive = true },
            Identifier = new TableIdentifier("default", "dbo", "orders")
        };
        SecurityPrincipalContext Subject(CallerContext? caller) => new()
        {
            UserSid = new Sid("S-1-5-21-1-2-3-1001"),
            TenantId = new TenantId("tenant-a"),
            GroupSids = new HashSet<Sid>(),
            TenantRoles = new HashSet<string>(),
            ClusterRoles = new HashSet<string>(),
            AuthenticationScheme = "test",
            Caller = caller
        };

        var plain = await pdp.DecideAsync(new AccessRequest(Subject(null), meta, AccessOperation.Read, AccessTransport.Rest));
        var labelled = await pdp.DecideAsync(new AccessRequest(
            Subject(new CallerContext("admin-console", "9.9", "everything", "99999999-9999-9999-9999-999999999999") { IsClientAsserted = true }),
            meta, AccessOperation.Read, AccessTransport.Rest));

        labelled.IsAllowed.ShouldBe(plain.IsAllowed);
        labelled.Reasons.ShouldBe(plain.Reasons);
    }
}
