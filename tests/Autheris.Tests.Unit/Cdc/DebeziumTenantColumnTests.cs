namespace Autheris.Tests.Unit.Cdc;

using System;
using System.Collections.Generic;
using Autheris.Api.Endpoints;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Extensions.Cdc;
using Microsoft.AspNetCore.Http;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DebeziumTenantColumnTests
{
    private const string Source = """{ "name": "salesdb", "schema": "crm", "table": "accounts" }""";

    private static string Payload(string? before, string? after)
        => "{ \"op\": \"u\", \"source\": " + Source
           + (before == null ? string.Empty : ", \"before\": " + before)
           + (after == null ? string.Empty : ", \"after\": " + after) + " }";

    [Fact]
    public void TenantId_PascalCase_IsExtracted()
    {
        var evt = DebeziumCdcParser.Parse(Payload(null, """{ "id": 1, "TenantId": "tenant-a" }"""));

        evt.TenantId.ShouldBe("tenant-a");
    }

    [Fact]
    public void TenantColumn_Configured_IsExtracted()
    {
        var evt = DebeziumCdcParser.Parse(Payload(null, """{ "id": 1, "org": "tenant-a" }"""), null, "org");

        evt.TenantId.ShouldBe("tenant-a");
    }

    [Fact]
    public void ConflictingBeforeAfterTenant_Rejected()
    {
        Should.Throw<InvalidOperationException>(() => DebeziumCdcParser.Parse(
            Payload("""{ "id": 1, "tenant_id": "tenant-a" }""", """{ "id": 1, "tenant_id": "tenant-b" }""")));
    }

    private static CdcEvent Event(string? tenant) => new(
        "e1", new TableIdentifier("salesdb", "crm", "accounts"), CdcOperation.Insert, tenant, null,
        new Dictionary<string, object?> { ["id"] = 1 }, DateTimeOffset.UtcNow);

    [Fact]
    public void NonAdmin_RowWithoutTenantValue_ForTenantTable_Rejected()
    {
        var rejection = StreamingCdcEndpoints.ApplyIngestTenantPolicy(Event(null), false, "tenant-a", true, false, out var resolved);

        rejection.ShouldNotBeNull();
        rejection.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        resolved.TenantId.ShouldBeNull();
    }

    [Fact]
    public void NonAdmin_RowWithoutTenantValue_UntenantedTable_IsStamped_UnlessRequired()
    {
        StreamingCdcEndpoints.ApplyIngestTenantPolicy(Event(null), false, "tenant-a", false, false, out var stamped).ShouldBeNull();
        stamped.TenantId.ShouldBe("tenant-a");

        StreamingCdcEndpoints.ApplyIngestTenantPolicy(Event(null), false, "tenant-a", false, true, out _).ShouldNotBeNull();
    }

    [Fact]
    public void NonAdmin_ForeignTenantValue_Forbidden()
    {
        var rejection = StreamingCdcEndpoints.ApplyIngestTenantPolicy(Event("tenant-b"), false, "tenant-a", true, false, out _);

        rejection.ShouldNotBeNull();
        rejection.ShouldBeAssignableTo<IStatusCodeHttpResult>()!.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
    }
}
