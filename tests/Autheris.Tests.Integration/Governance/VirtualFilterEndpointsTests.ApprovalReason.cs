namespace Autheris.Tests.Integration.Governance;

using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Options;
using Autheris.Tests.Unit.Catalog;
using Autheris.Tests.Unit.VirtualFilters;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed partial class VirtualFilterEndpointsTests
{
    [Fact]
    public async Task PutSqlFilter_RequireApprovalFalse_ReturnsPendingWithReason()
    {
        var catalog = Substitute.For<ITableMetadataRepository>();
        catalog.StubTables([
            VirtualFilterTestData.Table("conf", "client", "client_id", "crane_serial_number"),
            VirtualFilterTestData.Table("md", "crane", "serial_number", "is_delivered")
        ]);
        var options = Options.Create(new GatewayOptions { VirtualFilters = new VirtualFilterOptions { RequireApproval = false } });
        var service = new VirtualFilterAdministrationService(_repository, Substitute.For<IAuditLogRepository>(), options, catalog: catalog);

        var sqlBody = new
        {
            source = "lwetem_prod",
            sql = "from conf.client client join md.crane crane on crane.serial_number = client.crane_serial_number where crane.is_delivered is null and target.client_id = client.client_id"
        };
        var putContext = Context(Tenant, sqlBody, "GovernanceAdmin");
        var putStatus = await StatusAsync(await VirtualFilterEndpoints.PutFilterAsync("active_cranes_sql", putContext, service), putContext);

        putStatus.ShouldBe(StatusCodes.Status200OK);
        putContext.Response.Body.Position = 0;
        using (var putDoc = await JsonDocument.ParseAsync(putContext.Response.Body))
        {
            putDoc.RootElement.GetProperty("status").GetString().ShouldBe("PendingApproval");
            putDoc.RootElement.GetProperty("approval_reason").GetString().ShouldBe("sql_definition_needs_owner_approval");
        }

        var listContext = Context(Tenant, null, "GovernanceAdmin");
        var listStatus = await StatusAsync(await VirtualFilterEndpoints.ListAsync(listContext, service), listContext);
        listStatus.ShouldBe(StatusCodes.Status200OK);
        listContext.Response.Body.Position = 0;
        using var listDoc = await JsonDocument.ParseAsync(listContext.Response.Body);
        var listed = listDoc.RootElement.GetProperty("filters").EnumerateArray().Single(f => f.GetProperty("name").GetString() == "active_cranes_sql");
        listed.GetProperty("status").GetString().ShouldBe("PendingApproval");
        listed.GetProperty("approval_reason").GetString().ShouldBe("sql_definition_needs_owner_approval");
    }
}
