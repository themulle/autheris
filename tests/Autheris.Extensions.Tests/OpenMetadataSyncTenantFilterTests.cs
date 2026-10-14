namespace Autheris.Extensions.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.OpenMetadata.Interfaces;
using Autheris.Application.OpenMetadata.Models;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.OpenMetadata;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// SR-L-23: a tenant-restricted OpenMetadata sync only touches tables mapped to the caller's tenant and only reconciles
/// that tenant's sync consents; the sanitised warnings contain stable codes and counts only.
/// </summary>
[Trait("Category", "Security")]
public sealed class OpenMetadataSyncTenantFilterTests
{
    private static readonly Guid PolicyId = new("6c1f2b0e-3a4d-4e5f-8a9b-0c1d2e3f4a5b");
    private static readonly TenantId TenantA = new("tenant-a");
    private static readonly TenantId TenantB = new("tenant-b");

    private static OpenMetadataTable OmTable(string service, string name) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        FullyQualifiedName = $"{service}.db.dbo.{name}",
        Service = new OpenMetadataEntityReference { Name = service },
        Database = new OpenMetadataEntityReference { Name = "db" },
        DatabaseSchema = new OpenMetadataEntityReference { Name = "dbo" },
        Tags = [],
        Columns = [new OpenMetadataColumn { Name = "id", DataType = "INT" }]
    };

    private sealed class Fixture
    {
        public required OpenMetadataSyncService Service { get; init; }
        public List<TableMetadata> Upserted { get; } = [];
        public List<Consent> Created { get; } = [];
        public List<Guid> Revoked { get; } = [];
    }

    private static Fixture Create(IReadOnlyList<Consent>? existing = null)
    {
        var client = Substitute.For<IOpenMetadataClient>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var consentRepo = Substitute.For<IConsentRepository>();
        var epochRepo = Substitute.For<IPolicyEpochRepository>();

        IReadOnlyList<OpenMetadataTable> tables = [OmTable("svc_a", "orders"), OmTable("svc_b", "invoices")];
        client.GetTablesAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(tables));
        client.GetPoliciesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataPolicy>>([
            new OpenMetadataPolicy
            {
                Id = PolicyId,
                Name = "P",
                Enabled = true,
                Rules = [new OpenMetadataRule { Name = "ReadTables", Effect = "allow", Resources = ["table"], Operations = ["ViewAll"] }]
            }
        ]));
        client.GetRolesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataRole>>([
            new OpenMetadataRole { Name = "Reader", Policies = [new OpenMetadataEntityReference { Id = PolicyId, Name = "P" }] }
        ]));
        client.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataTeam>>([]));
        client.GetUsersAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<OpenMetadataUser>>([]));

        metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<TableMetadata?>(null));
        consentRepo.GetActiveConsentsByConsentRequestIdAsync(Arg.Any<Guid>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Consent>>(existing ?? []));

        var options = Options.Create(new GatewayOptions
        {
            OpenMetadata = new OpenMetadataOptions
            {
                Enabled = true,
                AutoCreateConsents = true,
                SyncIntervalMinutes = 30,
                ActivateNewTables = true,
                RoleToGatewayRoleMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Reader"] = "ReaderRole" },
                ServiceDatabaseToTenantMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["svc_a"] = TenantA.Value,
                    ["svc_b"] = TenantB.Value
                }
            }
        });

        var fixture = new Fixture
        {
            Service = new OpenMetadataSyncService(client, metadataRepo, consentRepo, epochRepo, options, NullLogger<OpenMetadataSyncService>.Instance)
        };

        metadataRepo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(ci => { var m = ci.Arg<TableMetadata>(); fixture.Upserted.Add(m); return Task.FromResult(m); });
        consentRepo.CreateConsentAsync(Arg.Any<Consent>(), Arg.Any<CancellationToken>())
            .Returns(ci => { var c = ci.Arg<Consent>(); fixture.Created.Add(c); return Task.FromResult(c); });
        consentRepo.RevokeSystemConsentAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Sid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => { fixture.Revoked.Add(ci.ArgAt<Guid>(0)); return Task.FromResult(true); });
        return fixture;
    }

    [Fact]
    public async Task RestrictedSync_OnlyUpsertsTablesOfTheCallerTenant()
    {
        var f = Create();

        var result = await f.Service.SyncPermissionsAsync(dryRun: false, restrictToTenant: TenantA);

        f.Upserted.Select(m => m.Identifier.TableName).ShouldBe(["orders"]);
        result.SyncedTables.ShouldBe(1);
        result.InvalidationTables.ShouldAllBe(t => t.TableName == "orders");
        f.Created.ShouldAllBe(c => c.TenantId == TenantA);
    }

    [Fact]
    public async Task UnrestrictedSync_TouchesEveryTenant()
    {
        var f = Create();

        var result = await f.Service.SyncPermissionsAsync(dryRun: false);

        result.SyncedTables.ShouldBe(2);
        f.Upserted.Select(m => m.Identifier.TableName).ShouldBe(["orders", "invoices"], ignoreOrder: true);
    }

    [Fact]
    public async Task RestrictedSync_DoesNotRevokeOtherTenantsSyncConsents()
    {
        var foreign = new Consent
        {
            Id = Guid.NewGuid(),
            TableIdentifier = new TableIdentifier("svc_b", "dbo", "invoices"),
            GranteeType = GranteeType.Role,
            RoleName = "ReaderRole",
            Effect = ConsentEffect.Allow,
            TenantId = TenantB,
            ConsentRequestId = OpenMetadataSyncService.OpenMetadataSyncConsentMarker,
            ValidFrom = DateTimeOffset.UtcNow.AddDays(-1),
            ValidTo = DateTimeOffset.UtcNow.AddDays(1)
        };
        var f = Create([foreign]);

        await f.Service.SyncPermissionsAsync(dryRun: false, restrictToTenant: TenantA);

        f.Revoked.ShouldNotContain(foreign.Id);
    }

    [Fact]
    public void SanitizedWarnings_AreCodesWithCounts_WithoutTableNamesOrExceptionText()
    {
        var result = new OpenMetadataSyncResult(0, 0, 0, [], [
            "Failed to map or upsert table svc_b.db.dbo.secret: connection string Server=x;Password=y",
            "Failed to map or upsert table svc_b.db.dbo.other: boom",
            "Failed to map or upsert table svc_b.db.dbo.third: boom",
            "Skipped OpenMetadata table svc_c.db.dbo.t: service is outside ServiceFilter."
        ], false);

        result.SanitizedWarnings.ShouldBe(["OM_TABLE_MAP_FAILED: 3", "OM_TABLE_SKIPPED: 1"]);
        string.Join(' ', result.SanitizedWarnings).ShouldNotContain("secret");
        string.Join(' ', result.SanitizedWarnings).ShouldNotContain("Password");
    }
}
