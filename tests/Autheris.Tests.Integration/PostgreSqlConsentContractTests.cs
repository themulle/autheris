namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

/// <summary>Review PG-9/PG-12: contract tests for the PostgreSQL consent workflow. Need Docker; without it they return early.</summary>
public sealed class PostgreSqlConsentContractTests : IAsyncLifetime
{
    private PostgreSqlContainer? _container;
    private bool _available;

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder("postgres:16-alpine").Build();
            await _container.StartAsync();
            _available = true;
        }
        catch (Exception)
        {
            _available = false;
        }
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
        }
    }

    private PostgreSqlGovernanceRepository NewRepository()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "PostgreSql",
                ConnectionString = _container!.GetConnectionString(),
                EnableOutboxProcessor = false,
                SeedDemoData = false
            }
        });
        return new PostgreSqlGovernanceRepository(Substitute.For<IEpochValidationService>(), options, env);
    }

    [Fact]
    public async Task RevokeConsent_UnknownConsent_ThrowsKeyNotFound()
    {
        if (!_available) return;

        await using var repo = NewRepository();

        await Should.ThrowAsync<KeyNotFoundException>(() =>
            repo.RevokeConsentAsync(Guid.NewGuid(), new Sid("S-1-5-21-ADMIN"), "test"));
    }

    [Fact]
    public async Task Schema_HasUniqueTicketIndexPerTenant()
    {
        if (!_available) return;

        await using var repo = NewRepository();
        await using var conn = new NpgsqlConnection(_container!.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(
            "SELECT indexdef FROM pg_indexes WHERE indexname = 'ux_consent_requests_ticket'", conn);

        var def = (string?)await cmd.ExecuteScalarAsync();

        def.ShouldNotBeNull();
        def.ShouldContain("UNIQUE");
    }

    [Fact]
    public async Task ApproveConsentRequest_HighSensitivityTable_EnforcesFourEyesWhenRequiresFourEyesIsFalse()
    {
        if (!_available) return;

        await using var repo = NewRepository();
        var tableId = new TableIdentifier("sales", "crm", "pg_fe_" + Guid.NewGuid().ToString("N")[..8]);
        var table = new Table
        {
            Id = Guid.NewGuid(),
            SourceName = tableId.Domain,
            SchemaName = tableId.Schema,
            TableName = tableId.TableName,
            Sensitivity = "HIGH",
            RequiresFourEyes = false,
            IsActive = true
        };

        var meta = await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Identifier = tableId,
            Table = table,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "email", DataType = "VARCHAR" }]
        });

        var request = await repo.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = meta.Table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-REQ"),
            RequestedGranteeType = GranteeType.User,
            RequestedGranteeRef = "S-1-5-21-REQ",
            BusinessJustification = "Support",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(1),
            Status = "PENDING",
            TenantId = new TenantId("tenant-pg")
        });

        var approver1 = new Sid("S-1-5-21-APP1");
        var approver2 = new Sid("S-1-5-21-APP2");

        var step1 = await repo.ApproveConsentRequestStepAsync(request.Id, approver1);
        step1.Status.ShouldBe("PENDING_SECOND_APPROVAL");

        await Should.ThrowAsync<InvalidOperationException>(() =>
            repo.ApproveConsentRequestStepAsync(request.Id, approver1));

        var step2 = await repo.ApproveConsentRequestStepAsync(request.Id, approver2);
        step2.Status.ShouldBe("APPROVED");
    }
}

