namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.ConsentRequests;
using Autheris.Application.ConsentRequests.Routing;
using Autheris.Application.Governance.Analysis;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

public sealed partial class AdvisoryRegressionTests
{
    [Fact]
    [Trait("Category", "Security")]
    public async Task Ghsa_5fqm_mpc4_regression()
    {
        // Route caps, justification, self-service, rate limits and validity are enforced by the permission request engine
        // (PermissionRequestEngineSubmitTests); the bundle service keeps the person-subject gate and hands every item to the engine.
        var ordersId = new TableIdentifier("sales", "dbo", "orders");
        var orders = new TableMetadata
        {
            Identifier = ordersId,
            Table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "dbo", TableName = "orders", IsActive = true },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };
        var engine = new Autheris.Tests.Unit.TestSupport.RecordingPermissionRequestEngine();
        var svc = new BundledConsentRequestService(
            new InMemoryTableMetadataRepository(orders),
            new InMemoryConsentRepository(),
            engine: engine);

        var personUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER"),
            new Claim(ClaimTypes.Email, "user@example.com"),
            new Claim("tenant_id", "tenant-a")
        ], "Bearer"));

        var ok = await svc.SubmitBundledRequestAsync(
            personUser,
            new BundledConsentRequest(
                [new RequestableConsentItem("sales.dbo.orders", null, "READ", "Analytics query need")],
                Purpose: "Analytics query need",
                ConsumerReference: "PR-1"));
        ok.Tickets.ShouldHaveSingleItem();
        engine.Bundles.ShouldHaveSingleItem();

        var appOnly = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-APP"),
            new Claim("idtyp", "app"),
            new Claim("tenant_id", "tenant-a")
        ], "Bearer"));
        (await Should.ThrowAsync<ConsentRequestException>(() => svc.SubmitBundledRequestAsync(
            appOnly,
            new BundledConsentRequest(
                [new RequestableConsentItem("sales.dbo.orders", null, "READ", "Analytics query need")],
                Purpose: "Analytics query need"))))
            .Code.ShouldBe("FORBIDDEN");
        engine.Bundles.ShouldHaveSingleItem();
    }
}
