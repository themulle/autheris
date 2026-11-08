using Autheris.Tests.Common;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Events.Services;
using Autheris.Application.Extensibility;
using Autheris.Application.Extensibility.Interceptors;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Audit;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Security;
using Autheris.Application.State;
using Autheris.Application.Streaming.Interfaces;
using Autheris.Application.Governance.Workflows;
using Autheris.Application.Webhooks;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Lakehouse.Interfaces;
using Autheris.Extensions.Lakehouse.Services;
using Autheris.GraphQL.Subscriptions;
using Autheris.Infrastructure.Persistence;
using HotChocolate;
using Path = System.IO.Path;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

public sealed class SecurityReviewG5GovernanceTests
{
    [Fact]
    public async Task Itsm_Reject_DoesNotTrustItsmSidPrefix()
    {
        var (repo, request) = await PendingExternalRequestAsync();
        using (repo)
        {
            await Should.ThrowAsync<UnauthorizedAccessException>(() =>
                repo.RejectConsentRequestAsync(request.Id, new Sid("ITSM_ATTACKER"), "no"));
            await Should.ThrowAsync<UnauthorizedAccessException>(() =>
                repo.RejectConsentRequestAsync(request.Id, new Sid("ITSM_ATTACKER"), "no", isExternalItsm: false));
        }
    }

    [Fact]
    public async Task Itsm_Reject_AuthorizedWhenWebhookFlagsExternalItsm()
    {
        var (repo, request) = await PendingExternalRequestAsync();
        using (repo)
        {
            var rejected = await repo.RejectConsentRequestAsync(request.Id, new Sid("ITSM_SERVICENOW:inst"), "no", isExternalItsm: true);

            rejected.Status.ShouldBe("REJECTED");
        }
    }

    [Fact]
    public async Task Consent_ActivateConsent_DoesNotActivatePendingRequests()
    {
        var (repo, request) = await PendingExternalRequestAsync();
        using (repo)
        {
            await repo.ActivateConsentAsync(request.Id, new Sid("ITSM_SERVICENOW:inst"));

            (await repo.GetConsentRequestAsync(request.Id))!.Status.ShouldBe("PENDING_EXTERNAL_APPROVAL");
            (await repo.GetActiveConsentsForSubjectsAsync([new Sid("S-1-5-21-G5")], request.TableIdentifier, DateTimeOffset.UtcNow))
                .ShouldBeEmpty();
        }
    }

    [Fact]
    public async Task Consent_AutoApprovePath_StillActivatesPendingRequests()
    {
        var (repo, request) = await PendingExternalRequestAsync("PENDING");
        using (repo)
        {
            await repo.ActivateConsentForAutoApproveAsync(request.Id);

            (await repo.GetConsentRequestAsync(request.Id))!.Status.ShouldBe("APPROVED");
        }
    }

    private static async Task<(PostgreSqlGovernanceRepository repo, ConsentRequest request)> PendingExternalRequestAsync(string status = "PENDING_EXTERNAL_APPROVAL")
    {
        var repo = GovernanceTestDatabase.CreateRepository(new FakeEpochValidationService());
        var tableId = new TableIdentifier("sales", "crm", "g5_" + Guid.NewGuid().ToString("N")[..8]);
        var table = new Table { Id = Guid.NewGuid(), SourceName = tableId.Domain, SchemaName = tableId.Schema, TableName = tableId.TableName, IsActive = true };
        var meta = await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = tableId,
            Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "email", DataType = "VARCHAR" }]
        });
        var created = await repo.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-G5"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-G5",
            BusinessJustification = "G5",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(1),
            Status = status,
            TenantId = new TenantId("tenant-a")
        });
        return (repo, created);
    }
}
