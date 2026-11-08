using Autheris.Tests.Common;
using Autheris.Domain.Security;
using System;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Security.Claims;
using System.Text;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Extensions;
using Autheris.Api.Middleware;
using Autheris.Api.Security;
using NSubstitute.ExceptionExtensions;
using Autheris.Application.Governance;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Audit;
using Autheris.Application.Security;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Security;
using Autheris.Application.Mcp.Interfaces;
using Autheris.Application.Mcp.Services;
using Autheris.Application.Streaming.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Integration.Governance;

public sealed class SecurityFindingsRemediationGovernanceTests
{
    [Fact]
    public async Task PostgreSqlGovernanceRepository_StrictTenantIsolation_DoesNotCrossLeakConsents()
    {
        // Finding B1 Verification:
        // Consents in tenant-alpha MUST NOT be visible when querying tenant-beta or vice-versa.
        var epochService = Substitute.For<IEpochValidationService>();
        using var repo = GovernanceTestDatabase.CreateRepository(epochService);

        var tableId = new TableIdentifier("sales", "dbo", "orders");
        var metadata = new TableMetadata
        {
            Identifier = tableId,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "sales",
                SchemaName = "dbo",
                TableName = "orders",
                Sensitivity = "CONFIDENTIAL",
                IsActive = true
            },
            Columns =
            [
                new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "int" },
                new TableColumn { Id = Guid.NewGuid(), ColumnName = "amount", DataType = "decimal" }
            ]
        };
        await repo.UpsertTableMetadataAsync(metadata);

        var userSid = new Sid("S-1-5-21-TENANT-USER");
        var consent = new Consent
        {
            Id = Guid.NewGuid(),
            TableId = metadata.Table.Id,
            TableIdentifier = tableId,
            Effect = ConsentEffect.Allow,
            GranteeType = GranteeType.User,
            GranteeSid = userSid,
            ValidFrom = DateTimeOffset.UtcNow.AddMinutes(-5),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1),
            TenantId = new TenantId("tenant-alpha")
        };
        await repo.CreateConsentAsync(consent);

        // Act & Assert 1: Querying with tenant-alpha returns the consent
        var alphaConsents = await repo.GetActiveConsentsForSubjectsAsync([userSid], tableId, DateTimeOffset.UtcNow, new TenantId("tenant-alpha"));
        alphaConsents.Count.ShouldBe(1);
        alphaConsents[0].TenantId.ShouldBe(new TenantId("tenant-alpha"));

        // Act & Assert 2: Querying with tenant-beta MUST NOT return the consent
        var betaConsents = await repo.GetActiveConsentsForSubjectsAsync([userSid], tableId, DateTimeOffset.UtcNow, new TenantId("tenant-beta"));
        betaConsents.Count.ShouldBe(0);

        // Act & Assert 3: Querying all active consents with tenant-beta MUST NOT return the consent
        var allBetaConsents = await repo.GetAllActiveConsentsForSubjectsAsync([userSid], null, DateTimeOffset.UtcNow, new TenantId("tenant-beta"));
        allBetaConsents.Count.ShouldBe(0);
    }
}
