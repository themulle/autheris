namespace Autheris.Tests.Integration;

using System;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

/// <summary>
/// Virtual filters, phase 2: the storage contract on real PostgreSQL (same cases as the SQLite repository tests).
/// Needs Docker; without it each test returns early.
/// </summary>
public sealed class PostgreSqlVirtualFilterContractTests : IAsyncLifetime
{
    private static readonly TenantId Tenant = new("tenant_lwe");
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
        return new PostgreSqlGovernanceRepository(Substitute.For<IEpochValidationService>(), Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions
            {
                Provider = "PostgreSql",
                ConnectionString = _container!.GetConnectionString(),
                EnableOutboxProcessor = false,
                SeedDemoData = false
            }
        }), env);
    }

    private static VirtualFilter Filter(string name = "nicht_ausgelieferte_krane") => new()
    {
        TenantId = Tenant,
        Name = name,
        Source = "lwetem_prod",
        KeyColumns = ["client.client_id"],
        ValidToColumn = "crane.date_of_delivery",
        Structured = new StructuredFilterDefinition
        {
            From = new TableIdentifier("lwetem_prod", "conf", "client"),
            FromAlias = "client",
            Joins = [new FilterJoin(new TableIdentifier("lwetem_prod", "md", "crane"), "crane", "crane.serial_number", "client.crane_serial_number")],
            Where = [new FilterCondition("crane.is_delivered", FilterConditionOperator.IsNull)]
        },
        ManagedBy = new ManagedBy("governance/access", "c1")
    };

    private static VirtualFilterAccessProfile Profile() => new()
    {
        TenantId = Tenant,
        Name = "david",
        GranteeSid = new Sid("S-1-5-21-LWE-DAVID"),
        Scope = "lwetem_prod.*.*",
        Uncovered = UncoveredPolicy.Deny,
        Bindings = [new FilterBinding { FilterName = "nicht_ausgelieferte_krane", TargetPattern = "lwetem_prod.*.*.client_id", TimeColumn = "ts" }]
    };

    [Fact]
    public async Task FiltersAndProfiles_RoundTrip_AndEachChangeSetIncrementsTheGenerationOnce()
    {
        if (!_available) return;
        await using var repository = NewRepository();
        IVirtualFilterRepository store = repository;
        var start = await store.GetGenerationAsync();

        await store.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [Filter()], SaveProfiles = [Profile()] });
        await store.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [Filter() with { Id = Guid.NewGuid(), ValidToColumn = null }] });   // replaced by name

        var snapshot = await store.LoadSnapshotAsync();
        snapshot.Generation.ShouldBe(start + 2);
        var filter = snapshot.Filters.ShouldHaveSingleItem();
        filter.ValidToColumn.ShouldBeNull();
        filter.ManagedBy.ShouldBe(new ManagedBy("governance/access", "c1"));
        filter.StoredDefinitionHash.ShouldBe(filter.ComputeDefinitionHash());
        snapshot.Profiles.ShouldHaveSingleItem().ComputeDefinitionHash().ShouldBe(Profile().ComputeDefinitionHash());

        await store.ApplyAsync(new VirtualFilterChangeSet { DeleteProfiles = [(Tenant, "david")], DeleteFilters = [(Tenant, "nicht_ausgelieferte_krane")] });
        var after = await store.LoadSnapshotAsync();
        after.Filters.ShouldBeEmpty();
        after.Profiles.ShouldBeEmpty();
        after.Generation.ShouldBe(start + 3);
    }

    [Fact]
    public async Task AFailingChangeSet_WritesNothing()
    {
        if (!_available) return;
        await using var repository = NewRepository();
        IVirtualFilterRepository store = repository;
        var start = await store.GetGenerationAsync();
        var duplicate = Filter("zweiter_filter");

        await Should.ThrowAsync<Exception>(() => store.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [duplicate, duplicate with { Name = "dritter_filter" }] }));

        (await store.LoadSnapshotAsync()).Filters.ShouldBeEmpty();
        (await store.GetGenerationAsync()).ShouldBe(start);
    }

    [Fact]
    public async Task SameFilterName_InTwoTenants_IsAllowed()
    {
        if (!_available) return;
        await using var repository = NewRepository();
        IVirtualFilterRepository store = repository;

        await store.ApplyAsync(new VirtualFilterChangeSet { SaveFilters = [Filter(), Filter() with { Id = Guid.NewGuid(), TenantId = new TenantId("other") }] });

        (await store.LoadSnapshotAsync()).Filters.Count.ShouldBe(2);
    }

    [Fact]
    public async Task POL_15_ActivateConsent_ForGroupAndServicePrincipal_PersistsGranteeSid()
    {
        if (!_available) return;
        await using var repo = NewRepository();
        var tableId = new TableIdentifier("sales", "public", "orders");
        var table = new Table { Id = Guid.NewGuid(), SourceName = "sales", SchemaName = "public", TableName = "orders" };
        await repo.UpsertTableMetadataAsync(new TableMetadata
        {
            Table = table,
            Identifier = tableId,
            Columns = [new TableColumn { TableId = table.Id, ColumnName = "id", DataType = "int" }]
        });

        var groupSid = new Sid("S-1-5-21-GROUP-ANALYTICS");
        var reqGroup = await repo.CreateConsentRequestAsync(new ConsentRequest
        {
            TableId = table.Id,
            TableIdentifier = tableId,
            RequesterSid = new Sid("S-1-5-21-REQ"),
            RequestedGranteeType = GranteeType.Group,
            RequestedGranteeRef = groupSid.Value,
            BusinessJustification = "POL-15 Group Test",
            RequestedValidTo = DateTimeOffset.UtcNow.AddDays(7),
            Status = "PENDING",
            TenantId = Tenant
        });

        await repo.ActivateConsentForAutoApproveAsync(reqGroup.Id);

        var activeConsents = await repo.GetActiveConsentsForSubjectsAsync(
            [groupSid], tableId, DateTimeOffset.UtcNow, Tenant);

        var groupConsent = activeConsents.ShouldHaveSingleItem();
        groupConsent.GranteeType.ShouldBe(GranteeType.Group);
        groupConsent.GranteeSid.ShouldBe(groupSid);
    }
}
