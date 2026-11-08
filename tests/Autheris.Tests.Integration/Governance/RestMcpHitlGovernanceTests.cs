using Autheris.Domain.Security;
using Autheris.Tests.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

public sealed class RestMcpHitlGovernanceTests
{
    [Fact]
    public async Task H06_PostgreSqlRepository_TicketLookupIsTenantBound_AndActivationOnlyFromPending()
    {
        using var repo = GovernanceTestDatabase.CreateRepository(new FakeEpochValidationService());
        var tableId = new TableIdentifier("sales", "crm", "h06_contacts");
        var table = new Table { Id = Guid.NewGuid(), SourceName = tableId.Domain, SchemaName = tableId.Schema, TableName = tableId.TableName, IsActive = true };
        var meta = await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = tableId,
            Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "email", DataType = "VARCHAR" }]
        });

        var ticketId = $"H06-{Guid.NewGuid():N}";
        var created = await repo.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-H06"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-H06",
            BusinessJustification = "H-06 tenant binding",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(1),
            Status = "PENDING_EXTERNAL_APPROVAL",
            TenantId = new TenantId("tenant-a"),
            ItsmTicketId = ticketId
        });

        (await repo.GetConsentRequestByTicketIdAsync(ticketId, new TenantId("tenant-b"))).ShouldBeNull();
        (await repo.GetConsentRequestByTicketIdAsync(ticketId, new TenantId("tenant-a"))).ShouldNotBeNull();

        await repo.ActivateConsentForAutoApproveAsync(created.Id);
        await repo.ActivateConsentForAutoApproveAsync(created.Id);

        var active = await repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-H06")], tableId, DateTimeOffset.UtcNow);
        active.Count.ShouldBe(1);
        (await repo.GetConsentRequestAsync(created.Id))!.Status.ShouldBe("APPROVED");
    }
}
