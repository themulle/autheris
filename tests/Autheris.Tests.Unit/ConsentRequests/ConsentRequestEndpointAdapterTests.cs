namespace Autheris.Tests.Unit.ConsentRequests;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading.Tasks;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.ClassificationClear;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Unit.Catalog;
using Autheris.Tests.Unit.TestSupport;
using Shouldly;
using Xunit;

/// <summary>The GraphQL, MCP and discovery entry points submit through this adapter, which only maps onto the engine.</summary>
public sealed class ConsentRequestEndpointAdapterTests
{
    private static readonly TenantId Tenant = new("tenant-a");

    private static ConsentRequestSubmission Command(
        ClaimsPrincipal? principal = null,
        IReadOnlyList<WriteRule>? writeRules = null,
        bool? writeOnly = null,
        IReadOnlyList<RequestedClearItem>? clear = null,
        DateTimeOffset? validTo = null) => new(
        "sales", "dbo", "orders", "Quarterly audit of order totals", 7, null, new Sid("S-1"),
        principal ?? Wp0.User("S-1", "tenant-a"), Tenant,
        RequestedWriteRules: writeRules, RequestWriteOnly: writeOnly, RequestedValidTo: validTo, RequestedClear: clear);

    [Fact]
    public async Task Data_request_is_one_engine_submission_for_the_caller()
    {
        var engine = new RecordingPermissionRequestEngine();
        var result = await new ConsentRequestEndpointAdapter(engine).SubmitAsync(Command());

        var s = engine.Submissions.ShouldHaveSingleItem();
        s.RequestKind.ShouldBe(ConsentRequestKind.Data);
        s.Target.Table.ShouldBe(new TableIdentifier("sales", "dbo", "orders"));
        s.GranteeType.ShouldBe(GranteeType.User);
        s.GranteeRef.ShouldBe("S-1");
        s.DurationDays.ShouldBe(7);
        s.IncludeReadAccess.ShouldBeTrue();
        result.Status.ShouldBe("PENDING");
        result.RequestId.ShouldNotBe(Guid.Empty);
    }

    [Fact]
    public async Task Explicit_valid_to_suppresses_the_duration()
    {
        var engine = new RecordingPermissionRequestEngine();
        var until = DateTimeOffset.UtcNow.AddDays(3);
        await new ConsentRequestEndpointAdapter(engine).SubmitAsync(Command(validTo: until));

        var s = engine.Submissions.ShouldHaveSingleItem();
        s.ValidTo.ShouldBe(until);
        s.DurationDays.ShouldBeNull();
    }

    [Fact]
    public async Task Clear_columns_become_one_unmask_request_over_the_union()
    {
        var engine = new RecordingPermissionRequestEngine();
        await new ConsentRequestEndpointAdapter(engine).SubmitAsync(Command(clear:
        [
            new RequestedClearItem("pii", ["email", "phone"]),
            new RequestedClearItem("finance", ["phone", "iban"])
        ]));

        var s = engine.Submissions.ShouldHaveSingleItem();
        s.RequestKind.ShouldBe(ConsentRequestKind.Unmask);
        s.Target.Columns.ShouldBe(["email", "phone", "iban"]);
        s.IncludeReadAccess.ShouldBeFalse();
    }

    [Fact]
    public async Task Write_only_without_rules_and_clear_with_rules_are_refused_before_the_engine()
    {
        var engine = new RecordingPermissionRequestEngine();
        var adapter = new ConsentRequestEndpointAdapter(engine);

        (await Should.ThrowAsync<ConsentRequestException>(() => adapter.SubmitAsync(Command(writeOnly: true)))).Code.ShouldBe("VALIDATION_ERROR");
        (await Should.ThrowAsync<ConsentRequestException>(() => adapter.SubmitAsync(Command(
            writeRules: [new WriteRule(Guid.NewGuid(), WriteOperation.Insert, ["id"], WriteRowScope.OwnRows)], clear: [new RequestedClearItem("pii", ["email"])])))).Code.ShouldBe("INVALID_COLUMNS");
        engine.Submissions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Machine_principal_is_refused_before_the_engine()
    {
        var engine = new RecordingPermissionRequestEngine();
        var app = Wp0.User("S-APP", "tenant-a", extra: new Claim("idtyp", "app"));

        var ex = await Should.ThrowAsync<ConsentRequestException>(() => new ConsentRequestEndpointAdapter(engine).SubmitAsync(Command(principal: app)));

        ex.Code.ShouldBe("FORBIDDEN");
        engine.Submissions.ShouldBeEmpty();
    }

    [Fact]
    public async Task Malformed_table_is_a_validation_error()
    {
        var engine = new RecordingPermissionRequestEngine();
        var bad = Command() with { TableName = "a.b" };

        (await Should.ThrowAsync<ConsentRequestException>(() => new ConsentRequestEndpointAdapter(engine).SubmitAsync(bad))).Code.ShouldBe("VALIDATION_ERROR");
    }

    [Fact]
    public async Task Withdraw_is_delegated_to_the_engine()
    {
        var withdrawn = await new ConsentRequestEndpointAdapter(new RecordingPermissionRequestEngine())
            .WithdrawAsync(Guid.NewGuid(), Wp0.User("S-1", "tenant-a"));

        withdrawn.ShouldBeTrue();
    }
}
