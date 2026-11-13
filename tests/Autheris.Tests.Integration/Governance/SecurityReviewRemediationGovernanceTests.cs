using Autheris.Tests.Common;
using System.Data;
using System.Data.Common;
using System.IO;
using System.Security;
using System.Security.Claims;
using System.Text;
using Autheris.Api.Endpoints;
using Autheris.Api.Middleware;
using Autheris.Application.Catalog.Services;
using Autheris.Application.FinOps.Interfaces;
using Autheris.Application.Governance;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Extensions.Dbt;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using TrinoSqlEngine;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

public sealed class SecurityReviewRemediationGovernanceTests
{
    [Fact]
    public async Task POL_3_ConsentResolutionService_AddedColumnsAfterConsentActivation_AreDenyNotClear()
    {
        using var repo = GovernanceTestDatabase.CreateRepository(new FakeEpochValidationService());
        var tableId = new TableIdentifier("sales", "crm", "pol3_customers_" + Guid.NewGuid().ToString("N")[..8]);
        var table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = tableId.Domain,
            SchemaName = tableId.Schema,
            TableName = tableId.TableName,
            IsActive = true
        };

        // Table initially has columns: id, name
        var meta = await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = tableId,
            Table = table,
            Columns =
            [
                new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "INTEGER" },
                new TableColumn { TableId = table.Id, ColumnName = "name", DataType = "VARCHAR" }
            ]
        });

        var userSid = new Sid("S-1-5-21-POL3-USER");
        var request = await repo.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = userSid,
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = userSid.Value,
            BusinessJustification = "POL-3 Unit Test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7),
            Status = "PENDING",
            TenantId = new TenantId("tenant-pol3")
        });

        // Activate the consent: freezes column snapshot into CONSENT_COLUMN_RULES
        await repo.ActivateConsentForAutoApproveAsync(request.Id);

        // Retrieve active consents
        var activeConsents = await repo.GetActiveConsentsForSubjectsAsync([userSid], tableId, DateTimeOffset.UtcNow);
        activeConsents.Count.ShouldBe(1);
        activeConsents[0].ColumnRules.Count.ShouldBe(2); // "id" and "name"
        activeConsents[0].ColumnRules.ShouldContain(r => r.ColumnName.Equals("id", StringComparison.OrdinalIgnoreCase));
        activeConsents[0].ColumnRules.ShouldContain(r => r.ColumnName.Equals("name", StringComparison.OrdinalIgnoreCase));

        // Resolve access via ConsentResolutionService
        var resolutionService = new ConsentResolutionService();
        var decision = resolutionService.ResolveAccess(
            userSid,
            new HashSet<Sid>(),
            new HashSet<string>(),
            tableId,
            activeConsents);

        decision.IsAllowed.ShouldBeTrue();
        decision.HasUnconstrainedColumnAllow.ShouldBeFalse();
        decision.GetColumnAccess("id").ShouldBe(ColumnAccessLevel.Clear);
        decision.GetColumnAccess("name").ShouldBe(ColumnAccessLevel.Clear);

        // A column added to the schema LATER was NOT part of the consent activation snapshot and must NOT resolve to Clear
        decision.GetColumnAccess("secret_credit_card").ShouldBe(ColumnAccessLevel.Deny);
        decision.GetEffectiveColumnAccess("secret_credit_card", metadata: null).ShouldBe(ColumnAccessLevel.Deny);
    }
}
