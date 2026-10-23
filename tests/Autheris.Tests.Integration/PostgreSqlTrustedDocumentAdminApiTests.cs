namespace Autheris.Tests.Integration;

using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.Governance.TrustedDocuments;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #281 WP3: admin service over the real PostgreSql store; a replace and a retire write the required audit rows (SEC-281-9) and a new set is visible to a registry without restart. Needs Docker; without it each test returns early.</summary>
public sealed class PostgreSqlTrustedDocumentAdminApiTests : IAsyncLifetime
{
    private static readonly string H1 = new('a', 64);
    private static readonly string[] Origins = ["https://app.example.com"];
    private PostgreSqlTestDatabase? _db;
    private bool _available;

    public async Task InitializeAsync()
    {
        try
        {
            _db = await PostgreSqlTestDatabase.CreateAsync();
            _available = _db.IsAvailable;
        }
        catch (Exception ex)
        {
            _available = false;
            ContainerStartup.FailIfRequired(ex, "The PostgreSQL test container");
        }
    }

    public async Task DisposeAsync()
    {
        if (_db != null)
        {
            await _db.DisposeAsync();
        }
    }

    private PostgreSqlGovernanceRepository NewRepository()
    {
        var env = Substitute.For<IHostEnvironment>();
        env.EnvironmentName.Returns("Development");
        return GovernanceStoreFactory.CreatePostgreSql(Substitute.For<IEpochValidationService>(), Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { Provider = "PostgreSql", ConnectionString = _db!.ConnectionString, EnableOutboxProcessor = false, SeedDemoData = false }
        }), env);
    }

    [Fact]
    public async Task Put_Retire_WriteAuditRows_AndRegistrySeesNewSetWithoutRestart()
    {
        if (!_available) return;
        await using var repository = NewRepository();
        var options = Options.Create(new GatewayOptions());
        using var registry = new TrustedDocumentAppRegistry(repository, options);
        var service = new TrustedDocumentAdministrationService(repository, registry, new AuditWriter(repository, NullLogger<AuditWriter>.Instance), options);
        var actor = new TrustedDocumentActor("S-1-5-21-281", "trusted-documents:write");
        var app = new AppIdentity("shop", "1.0");

        (await registry.GetAsync(default)).IsDocumentTrusted(app, H1).ShouldBeFalse();
        var put = await service.PutAsync("shop", "1.0", [H1], Origins, actor);
        put.Added.ShouldBe(1);
        (await registry.GetAsync(default)).IsDocumentTrusted(app, H1).ShouldBeTrue();

        (await service.RetireAsync("shop", "1.0", actor)).ShouldBe(TrustedDocumentRetireOutcome.Retired);
        (await registry.GetAsync(default)).IsDocumentTrusted(app, H1).ShouldBeFalse();

        var page = await repository.QueryAuditLogPageAsync(new Autheris.Application.Governance.Paging.AuditLogPageQuery(null, PageSize: 50));
        var replaced = page.Items.Single(e => e.EventType == AuditEventTypes.TrustedDocumentsReplaced && e.TargetTable == "trusted-documents:shop/1.0");
        replaced.ActorSid.Value.ShouldBe("S-1-5-21-281");
        replaced.DetailsJson.ShouldNotContain("query");
        page.Items.ShouldContain(e => e.EventType == AuditEventTypes.TrustedDocumentsRetired && e.TargetTable == "trusted-documents:shop/1.0");
    }
}
