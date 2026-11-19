namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Catalog.External;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Tenancy;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

/// <summary>Issue #858: <c>callerState.rowScopeDescription</c> is the caller's own consent description, nothing else.</summary>
[Trait("Category", "Security")]
public sealed class ExternalCatalogRowScopeDescriptionTests
{
    private const string UserSid = "S-1-5-21-111-222-333-1001";
    private const string OtherSid = "S-1-5-21-111-222-333-1002";
    private const string GroupSid = "S-1-5-21-111-222-333-2001";
    private static readonly TenantId TenantA = new("tenant-a");

    private static ExternalCatalogTestHarness Create(out TableMetadata table, bool allowAll = true)
    {
        var harness = ExternalCatalogTestHarness.Create();
        table = harness.AddTable("pg", "orders", ownerTenantId: "tenant-a");
        harness.Pdp.Decide = r => allowAll
            ? TableAccessDecision.Allowed(r.Object.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true)
            : TableAccessDecision.Denied(r.Object.Identifier, "Denied");
        return harness;
    }

    private static Consent Grant(
        TableMetadata table,
        string? description,
        GranteeType type = GranteeType.User,
        string sid = UserSid,
        TimeSpan? validFor = null,
        bool revoked = false,
        TimeSpan? validFromOffset = null,
        TenantId? tenant = null,
        Guid? id = null)
    {
        var now = ExternalCatalogTestHarness.FixedNow;
        return new Consent
        {
            Id = id ?? Guid.NewGuid(),
            TableIdentifier = table.Identifier,
            Effect = ConsentEffect.Allow,
            GranteeType = type,
            GranteeSid = new Sid(sid),
            TenantId = tenant ?? TenantA,
            ValidFrom = now + (validFromOffset ?? TimeSpan.FromDays(-30)),
            ValidTo = now + (validFor ?? TimeSpan.FromDays(30)),
            IsRevoked = revoked,
            Description = description
        };
    }

    private static ClaimsPrincipal User(bool inGroup = false)
    {
        var principal = ExternalCatalogTestHarness.BuildDelegatedUserPrincipal("tenant-a", UserSid, scopes: ExternalApiPermissions.CatalogRead);
        if (inGroup)
        {
            ((ClaimsIdentity)principal.Identity!).AddClaim(IdentityClaimIssuers.CreateGroupSidClaim(GroupSid));
        }

        return principal;
    }

    private static async Task<JsonElement> GetCallerStateAsync(ExternalCatalogTestHarness harness, TableMetadata table, ClaimsPrincipal caller)
    {
        var resp = await harness.GetObjectByIdAsync(caller, table.Identifier.ToString());
        resp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        return resp.ReadJson().GetProperty("callerState");
    }

    private static string? Description(JsonElement callerState) =>
        callerState.TryGetProperty("rowScopeDescription", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;

    [Fact]
    public async Task CallerState_UserGrantDescription_IsReturned()
    {
        await using var harness = Create(out var table);
        harness.Consents.Consents.Add(Grant(table, "Only EU orders of your region."));

        var state = await GetCallerStateAsync(harness, table, User());

        state.GetProperty("access").GetString().ShouldBe("granted");
        Description(state).ShouldBe("Only EU orders of your region.");
    }

    [Fact]
    public async Task CallerState_GroupGrantDescription_IsReturned_ForGroupMember()
    {
        await using var harness = Create(out var table);
        harness.Consents.Consents.Add(Grant(table, "Group scope text.", GranteeType.Group, GroupSid));

        Description(await GetCallerStateAsync(harness, table, User(inGroup: true))).ShouldBe("Group scope text.");
        Description(await GetCallerStateAsync(harness, table, User(inGroup: false))).ShouldBeNull();
    }

    [Fact]
    public async Task CallerState_OtherCallersConsentDescription_NeverReturned()
    {
        await using var harness = Create(out var table);
        harness.Consents.Consents.Add(Grant(table, "Foreign user text.", sid: OtherSid));
        harness.Consents.Consents.Add(Grant(table, "Foreign group text.", GranteeType.Group, "S-1-5-21-111-222-333-2999"));
        harness.Consents.Consents.Add(Grant(table, "Foreign tenant text.", tenant: new TenantId("tenant-b")));

        var state = await GetCallerStateAsync(harness, table, User(inGroup: true));

        Description(state).ShouldBeNull();
    }

    [Fact]
    public async Task CallerState_SeveralConsents_UserBeatsGroupBeatsRole_EarliestExpiryThenId()
    {
        await using var harness = Create(out var table);
        var lowId = new Guid("00000000-0000-0000-0000-000000000001");
        var highId = new Guid("00000000-0000-0000-0000-000000000002");
        harness.Consents.Consents.Add(Grant(table, "group early", GranteeType.Group, GroupSid, TimeSpan.FromDays(1)));
        harness.Consents.Consents.Add(Grant(table, "user late", validFor: TimeSpan.FromDays(20), id: highId));
        harness.Consents.Consents.Add(Grant(table, "user late low id", validFor: TimeSpan.FromDays(20), id: lowId));
        harness.Consents.Consents.Add(Grant(table, "user earliest", validFor: TimeSpan.FromDays(10)));
        harness.Consents.Consents.Add(new Consent
        {
            TableIdentifier = table.Identifier,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.Role,
            RoleName = "Analyst",
            TenantId = TenantA,
            ValidFrom = ExternalCatalogTestHarness.FixedNow.AddDays(-1),
            ValidTo = ExternalCatalogTestHarness.FixedNow.AddDays(2),
            Description = "role text"
        });

        Description(await GetCallerStateAsync(harness, table, User(inGroup: true))).ShouldBe("user earliest");

        harness.Consents.Consents.RemoveAll(c => c.Description == "user earliest");
        Description(await GetCallerStateAsync(harness, table, User(inGroup: true))).ShouldBe("user late low id");

        harness.Consents.Consents.RemoveAll(c => c.GranteeType == GranteeType.User);
        Description(await GetCallerStateAsync(harness, table, User(inGroup: true))).ShouldBe("group early");
    }

    [Fact]
    public async Task CallerState_SeveralConsents_ReturnsOneString_NoConcatenation()
    {
        await using var harness = Create(out var table);
        harness.Consents.Consents.Add(Grant(table, "first text"));
        harness.Consents.Consents.Add(Grant(table, "second text", GranteeType.Group, GroupSid));

        var description = Description(await GetCallerStateAsync(harness, table, User(inGroup: true)));

        description.ShouldBe("first text");
        description!.ShouldNotContain("second text");
    }

    [Fact]
    public async Task CallerState_OverCapOrControlCharDescription_ReturnsNull()
    {
        await using var harness = Create(out var table);
        harness.Consents.Consents.Add(Grant(table, new string('x', 501)));
        Description(await GetCallerStateAsync(harness, table, User())).ShouldBeNull();

        harness.Consents.Consents.Clear();
        harness.Consents.Consents.Add(Grant(table, "bad\u0007text"));
        Description(await GetCallerStateAsync(harness, table, User())).ShouldBeNull();

        // A failed check is not repaired by falling back to a truncated text.
        harness.Consents.Consents.Clear();
        harness.Consents.Consents.Add(Grant(table, "line one\nline two"));
        Description(await GetCallerStateAsync(harness, table, User())).ShouldBeNull();
    }

    [Fact]
    public async Task CallerState_ExpiredPendingRevokedConsent_ReturnsNull()
    {
        await using var harness = Create(out var table);
        harness.Consents.Consents.Add(Grant(table, "expired", validFor: TimeSpan.FromDays(-1)));
        harness.Consents.Consents.Add(Grant(table, "not yet valid", validFromOffset: TimeSpan.FromDays(5), validFor: TimeSpan.FromDays(60)));
        harness.Consents.Consents.Add(Grant(table, "revoked", revoked: true));

        var state = await GetCallerStateAsync(harness, table, User());

        Description(state).ShouldBeNull();
    }

    [Fact]
    public async Task CallerState_NoAccess_ReturnsNull()
    {
        await using var harness = Create(out var table, allowAll: false);
        harness.Consents.Consents.Add(Grant(table, "must not leak"));

        var state = await GetCallerStateAsync(harness, table, User());

        state.GetProperty("access").GetString().ShouldNotBe("granted");
        Description(state).ShouldBeNull();
    }

    [Fact]
    public async Task CallerState_AdminProjection_HasNoCallerState()
    {
        await using var harness = Create(out var table);
        harness.RegisterExternalSystem("tenant-a", "admin-sync", enabled: true);
        harness.Consents.Consents.Add(Grant(table, "owner text"));
        var admin = ExternalCatalogTestHarness.BuildAppPrincipal("tenant-a", "admin-sync", ExternalApiPermissions.CatalogReadAdmin);

        var resp = await harness.GetObjectByIdAsync(admin, table.Identifier.ToString());

        resp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        resp.ReadJson().GetProperty("callerState").ValueKind.ShouldBe(JsonValueKind.Null);
        resp.Body.ShouldNotContain("owner text");
    }

    [Fact]
    public async Task CallerState_Json_NeverContainsActiveRowFiltersSqlOrValueType()
    {
        typeof(ExternalCatalogCallerStateDto)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static p => p.Name)
            .OrderBy(static n => n, StringComparer.Ordinal)
            .ShouldBe(["Access", "RowScope", "RowScopeDescription", "Write"]);

        await using var harness = Create(out var table);
        harness.Consents.Consents.Add(Grant(table, "text"));
        var state = await GetCallerStateAsync(harness, table, User());

        state.EnumerateObject().Select(static p => p.Name).OrderBy(static n => n, StringComparer.Ordinal)
            .ShouldBe(["access", "rowScope", "rowScopeDescription", "write"]);
        var raw = state.GetRawText();
        raw.ShouldNotContain("activeRowFilters", Case.Insensitive);
        raw.ShouldNotContain("sql", Case.Insensitive);
        raw.ShouldNotContain("valueType", Case.Insensitive);
    }

    [Fact]
    public async Task CallerState_DescriptionLoadFailure_ReturnsNullDescriptionAndKeepsObject()
    {
        await using var harness = Create(out var table);
        harness.Consents.Consents.Add(Grant(table, "never shown"));
        harness.Consents.FailDescriptionLoad = true;

        var state = await GetCallerStateAsync(harness, table, User());

        state.GetProperty("access").GetString().ShouldBe("granted");
        Description(state).ShouldBeNull();

        var list = await harness.ListObjectsAsync(User());
        list.StatusCode.ShouldBe(StatusCodes.Status200OK);
        list.ReadJson().GetProperty("items").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public async Task CallerState_ListRoute_LoadsDescriptionsOncePerPage()
    {
        await using var harness = Create(out var first);
        var second = harness.AddTable("pg", "payments", ownerTenantId: "tenant-a");
        var third = harness.AddTable("pg", "refunds", ownerTenantId: "tenant-a");
        harness.Consents.Consents.Add(Grant(first, "text one"));
        harness.Consents.Consents.Add(Grant(second, "text two"));
        harness.Consents.Consents.Add(Grant(third, "text three"));

        var resp = await harness.ListObjectsAsync(User());

        resp.StatusCode.ShouldBe(StatusCodes.Status200OK);
        harness.Consents.DescriptionLoadCalls.ShouldBe(1);
        resp.ReadJson().GetProperty("items").EnumerateArray()
            .Select(static i => Description(i.GetProperty("callerState")))
            .OrderBy(static t => t, StringComparer.Ordinal)
            .ShouldBe(["text one", "text three", "text two"]);
    }

    [Fact]
    public async Task CallerState_TopRankedBadDescription_FallsThroughToNextOwnConsent_NeverAForeignOne()
    {
        await using var harness = Create(out var table);
        harness.Consents.Consents.Add(Grant(table, "bad\u0007text"));
        harness.Consents.Consents.Add(Grant(table, "valid group text", GranteeType.Group, GroupSid));
        harness.Consents.Consents.Add(Grant(table, "foreign text", sid: OtherSid));

        Description(await GetCallerStateAsync(harness, table, User(inGroup: true))).ShouldBe("valid group text");
        Description(await GetCallerStateAsync(harness, table, User(inGroup: false))).ShouldBeNull();
    }

    [Fact]
    public async Task CallerState_AllowPlusDenyOnSameTable_IsNotGrantedAndHasNoDescription()
    {
        await using var harness = Create(out var table, allowAll: false);
        harness.Consents.Consents.Add(Grant(table, "allow text"));
        harness.Consents.Consents.Add(new Consent
        {
            TableIdentifier = table.Identifier,
            Effect = ConsentEffect.Deny,
            GranteeType = GranteeType.User,
            GranteeSid = new Sid(UserSid),
            TenantId = TenantA,
            ValidFrom = ExternalCatalogTestHarness.FixedNow.AddDays(-1),
            ValidTo = ExternalCatalogTestHarness.FixedNow.AddDays(30),
            Description = "deny text"
        });

        var state = await GetCallerStateAsync(harness, table, User());

        state.GetProperty("access").GetString().ShouldNotBe("granted");
        Description(state).ShouldBeNull();
    }
}
