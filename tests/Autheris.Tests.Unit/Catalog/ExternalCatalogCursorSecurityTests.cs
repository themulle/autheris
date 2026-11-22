namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Api.Security;
using Autheris.Application.Catalog.External;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ExternalCatalogCursorSecurityTests
{
    private const string PersistentSigningKey = "01234567890123456789012345678901-external-cursor";

    private static ClaimsPrincipal User(string sid = "S-1-5-21-SEC-USER", string tenant = "tenant-a", string? role = null)
    {
        var roles = role is null ? Array.Empty<string>() : [role];
        return ExternalCatalogTestHarness.BuildDelegatedUserPrincipal(tenant, sid, scopes: ExternalApiPermissions.CatalogRead, roles: roles);
    }

    private static string ReplaceSegment(string cursor, int index, string newValue)
    {
        PageCursor.TryDecode(cursor, ExternalCatalogCursor.MaxCursorLength, out var raw).ShouldBeTrue();
        var parts = raw.Split('|');
        parts.Length.ShouldBe(5);
        parts[index] = newValue;
        return PageCursor.Encode(string.Join('|', parts));
    }

    [Fact]
    public async Task Cursor_ForgedCount_Is400()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: PersistentSigningKey);
        harness.AddTable("pg", "t1");
        harness.AddTable("pg", "t2");
        var user = User();

        var p1 = (await harness.ListObjectsAsync(user, pageSize: 1)).ReadJson();
        var cursor = p1.GetProperty("nextCursor").GetString()!;
        var forged = ReplaceSegment(cursor, 2, "999999");

        var resp = await harness.ListObjectsAsync(user, pageSize: 1, cursor: forged);
        resp.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        resp.Body.ShouldContain("INVALID_CURSOR");
    }

    [Fact]
    public async Task Cursor_TamperedMac_Is400()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: PersistentSigningKey);
        harness.AddTable("pg", "t1");
        harness.AddTable("pg", "t2");
        var user = User();

        var p1 = (await harness.ListObjectsAsync(user, pageSize: 1)).ReadJson();
        var cursor = p1.GetProperty("nextCursor").GetString()!;
        PageCursor.TryDecode(cursor, ExternalCatalogCursor.MaxCursorLength, out var raw).ShouldBeTrue();
        var parts = raw.Split('|');
        var mac = parts[4];
        parts[4] = (mac[0] == 'A' ? 'B' : 'A') + mac[1..];
        var tampered = PageCursor.Encode(string.Join('|', parts));

        var resp = await harness.ListObjectsAsync(user, pageSize: 1, cursor: tampered);
        resp.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        resp.Body.ShouldContain("INVALID_CURSOR");
    }

    [Fact]
    public async Task Cursor_OtherTenant_Is400()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: PersistentSigningKey);
        harness.AddTable("pg", "t1", ownerTenantId: "tenant-a");
        harness.AddTable("pg", "t2", ownerTenantId: "tenant-a");

        var userA = User(tenant: "tenant-a");
        var userB = User(tenant: "tenant-b");

        var p1 = (await harness.ListObjectsAsync(userA, pageSize: 1)).ReadJson();
        var cursor = p1.GetProperty("nextCursor").GetString()!;

        var replay = await harness.ListObjectsAsync(userB, pageSize: 1, cursor: cursor);
        replay.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        replay.Body.ShouldContain("INVALID_CURSOR");
    }

    [Fact]
    public async Task Cursor_OtherCaller_Is400()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: PersistentSigningKey);
        harness.AddTable("pg", "t1");
        harness.AddTable("pg", "t2");

        var alice = User(sid: "S-1-5-21-ALICE");
        var bob = User(sid: "S-1-5-21-BOB");
        var aliceElevated = User(sid: "S-1-5-21-ALICE", role: "GovernanceAdmin");

        var p1 = (await harness.ListObjectsAsync(alice, pageSize: 1)).ReadJson();
        var cursor = p1.GetProperty("nextCursor").GetString()!;

        (await harness.ListObjectsAsync(bob, pageSize: 1, cursor: cursor)).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        (await harness.ListObjectsAsync(aliceElevated, pageSize: 1, cursor: cursor)).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    public async Task Cursor_EveryFilterChangeBreaksBinding()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: PersistentSigningKey);
        var user = User();
        var key = ExternalCatalogCursor.ResolvePersistentKey(
            new GatewayOptions { RestApi = new RestApiOptions { BulkStream = new BulkStreamOptions { SigningKey = PersistentSigningKey } } })!;

        var baseQuery = new ExternalCatalogListQuery(
            Kind: "table",
            Domain: "sales",
            Schema: "dbo",
            Owner: "Alice",
            Tag: "finance",
            ProtectionClass: "CONFIDENTIAL",
            Pii: true,
            ChangedSince: 5,
            AccessStatus: "granted",
            Q: "orders");

        var baseBinding = ExternalCatalogCursor.ComputeBinding(baseQuery, "table", DiscoveryAccessState.Granted, "orders", new TenantId("tenant-a"), user);
        var cursor = ExternalCatalogCursor.Encode("sales.dbo.orders", 10, baseBinding, key);

        var mutations = new (string Name, ExternalCatalogListQuery Query)[]
        {
            ("q", baseQuery with { Q = "other" }),
            ("accessStatus", baseQuery with { AccessStatus = "none" }),
            ("kind", baseQuery with { Kind = "view" }),
            ("domain", baseQuery with { Domain = "hr" }),
            ("schema", baseQuery with { Schema = "public" }),
            ("owner", baseQuery with { Owner = "Bob" }),
            ("tag", baseQuery with { Tag = "pii" }),
            ("protectionClass", baseQuery with { ProtectionClass = "INTERNAL" }),
            ("pii", baseQuery with { Pii = false }),
            ("changedSince", baseQuery with { ChangedSince = 6 })
        };

        foreach (var (name, mutated) in mutations)
        {
            var resp = await harness.GetReader().ListObjectsAsync(user, new TenantId("tenant-a"), mutated with { Cursor = cursor });
            resp.IsSuccess.ShouldBeFalse(name);
            resp.Error!.StatusCode.ShouldBe(400, name);
            resp.Error.Code.ShouldBe("INVALID_CURSOR", name);
        }
    }

    [Fact]
    public async Task Cursor_OffsetAndV1_Are400()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: PersistentSigningKey);
        harness.AddTable("pg", "t1");
        var user = User();

        var offsetCursor = PageCursor.Encode("1.abc");
        var v1Cursor = PageCursor.Encode("v1|pg.dbo.t1|1|abc|sig");
        var overLength = "c1." + new string('A', 510);

        foreach (var bad in new[] { offsetCursor, v1Cursor, overLength, "not-c1-prefix", string.Empty })
        {
            var resp = await harness.ListObjectsAsync(user, pageSize: 1, cursor: bad);
            resp.StatusCode.ShouldBe(StatusCodes.Status400BadRequest, bad);
            resp.Body.ShouldContain("INVALID_CURSOR", Case.Sensitive, bad);
        }
    }

    [Fact]
    public async Task Cursor_NegativeOrHugeCount_Is400()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: PersistentSigningKey);
        harness.AddTable("pg", "t1");
        harness.AddTable("pg", "t2");
        var user = User();

        var key = ExternalCatalogCursor.ResolvePersistentKey(
            new GatewayOptions { RestApi = new RestApiOptions { BulkStream = new BulkStreamOptions { SigningKey = PersistentSigningKey } } })!;
        var binding = ExternalCatalogCursor.ComputeBinding(new ExternalCatalogListQuery(), null, null, null, new TenantId("tenant-a"), user);

        // Even with a valid MAC over a negative/non-numeric/overflow count, decode rejects it.
        var negCountCursor = ExternalCatalogCursor.Encode("pg.dbo.t1", -5, binding, key);
        PageCursor.TryDecode(negCountCursor, ExternalCatalogCursor.MaxCursorLength, out var raw).ShouldBeTrue();
        var parts = raw.Split('|');
        parts[2].ShouldBeEmpty(); // Encode clamps/omits negative count; craft signed invalid counts directly:
        ExternalCatalogCursor.TryDecode(PageCursor.Encode($"v2|pg.dbo.t1|-1|{binding}|sig"), binding, key, out _, out _).ShouldBeFalse();
        ExternalCatalogCursor.TryDecode(PageCursor.Encode($"v2|pg.dbo.t1|99999999999999999999|{binding}|sig"), binding, key, out _, out _).ShouldBeFalse();
        ExternalCatalogCursor.TryDecode(PageCursor.Encode($"v2|pg.dbo.t1|abc|{binding}|sig"), binding, key, out _, out _).ShouldBeFalse();

        var p1 = (await harness.ListObjectsAsync(user, pageSize: 1)).ReadJson();
        var realCursor = p1.GetProperty("nextCursor").GetString()!;
        foreach (var badCount in new[] { "-1", "99999999999999999999", "notanumber" })
        {
            var mutated = ReplaceSegment(realCursor, 2, badCount);
            var resp = await harness.ListObjectsAsync(user, pageSize: 1, cursor: mutated);
            resp.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
            resp.Body.ShouldContain("INVALID_CURSOR");
        }
    }

    [Fact]
    public async Task Cursor_ErrorBodyDoesNotEchoCursor()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: PersistentSigningKey);
        harness.AddTable("pg", "t1");
        harness.AddTable("pg", "t2");
        var user = User();

        const string sentinel = "SENTINEL_CURSOR_VALUE_SHOULD_NEVER_BE_ECHOED";
        var bad = PageCursor.Encode(sentinel);
        var respBad = await harness.ListObjectsAsync(user, pageSize: 1, cursor: bad);
        respBad.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        respBad.Body.ShouldNotContain(sentinel);
        respBad.Body.ShouldNotContain(bad);

        var p1 = (await harness.ListObjectsAsync(user, pageSize: 1)).ReadJson();
        var valid = p1.GetProperty("nextCursor").GetString()!;
        var respMismatch = await harness.ListObjectsAsync(user, pageSize: 1, domain: "other", cursor: valid);
        respMismatch.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        respMismatch.Body.ShouldNotContain(valid);

        // Fixed error detail does not distinguish malformed vs mismatch.
        respBad.ReadJson().GetProperty("detail").GetString()
            .ShouldBe(respMismatch.ReadJson().GetProperty("detail").GetString());
    }

    [Fact]
    public void Cursor_BindingFieldsAreLengthPrefixed()
    {
        var user = User();
        var tenant = new TenantId("tenant-a");
        var q1 = new ExternalCatalogListQuery(Domain: "a|b", Schema: "c");
        var q2 = new ExternalCatalogListQuery(Domain: "a", Schema: "b|c");

        var m1 = ExternalCatalogCursor.FormatBindingMaterial(q1, null, null, null, tenant, "scope");
        var m2 = ExternalCatalogCursor.FormatBindingMaterial(q2, null, null, null, tenant, "scope");
        m1.ShouldNotBe(m2);

        var b1 = ExternalCatalogCursor.ComputeBinding(q1, null, null, null, tenant, user);
        var b2 = ExternalCatalogCursor.ComputeBinding(q2, null, null, null, tenant, user);
        b1.ShouldNotBe(b2);
    }

    [Fact]
    public async Task Cursor_WithoutPersistentKey_DoesNotCarryCount()
    {
        await using var harness = ExternalCatalogTestHarness.Create(cursorSigningKey: null);
        for (var i = 0; i < 4; i++)
        {
            harness.AddTable("pg", $"t_{i:00}");
        }

        var user = User();
        var p1 = await harness.ListObjectsAsync(user, pageSize: 2);
        p1.StatusCode.ShouldBe(StatusCodes.Status200OK);
        var p1Json = p1.ReadJson();
        var cursor = p1Json.GetProperty("nextCursor").GetString()!;
        cursor.ShouldNotBeNull();

        PageCursor.TryDecode(cursor, ExternalCatalogCursor.MaxCursorLength, out var raw).ShouldBeTrue();
        var parts = raw.Split('|');
        parts.Length.ShouldBe(5);
        parts[0].ShouldBe("v2");
        parts[2].ShouldBeEmpty();
        parts[4].ShouldBeEmpty();

        // Injecting a count into an unkeyed cursor is rejected as 400 INVALID_CURSOR.
        var injectedCount = ReplaceSegment(cursor, 2, "999");
        (await harness.ListObjectsAsync(user, pageSize: 2, cursor: injectedCount)).StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        // Using the valid count-less cursor on page 2 (fresh request scope) recomputes totalCount on the server.
        await using var harness2 = ExternalCatalogTestHarness.Create(cursorSigningKey: null);
        for (var i = 0; i < 5; i++)
        {
            harness2.AddTable("pg", $"t_{i:00}");
        }

        var p2 = await harness2.ListObjectsAsync(user, pageSize: 2, cursor: cursor);
        p2.StatusCode.ShouldBe(StatusCodes.Status200OK);
        p2.ReadJson().GetProperty("totalCount").GetInt32().ShouldBe(5);
    }
}
