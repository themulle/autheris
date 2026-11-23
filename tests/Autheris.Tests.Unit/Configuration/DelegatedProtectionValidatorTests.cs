namespace Autheris.Tests.Unit.Configuration;

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Extensions.DependencyInjection;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Domains;
using Autheris.Application.Governance.Protection;
using Autheris.Application.Governance.Services;
using Autheris.Application.Policy;
using Autheris.Application.Security;
using Autheris.Domain.Common;
using Autheris.Domain.Identity;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Autheris.Tests.Common;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.Governance;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class DelegatedProtectionValidatorTests
{
    private static GatewayOptions BuildOptions(EnforcementMode mode) =>
        new()
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["uc-prod"] = new()
                    {
                        Provider = "Databricks",
                        ConnectionString = "Host=example.invalid;HttpPath=/sql/1.0/warehouses/x",
                        EnforcementMode = mode
                    }
                }
            }
        };

    private static EffectiveProtection Protection(ProtectionClass tableClass, string? maskCategory = null) =>
        new(
            Container: new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            OwnerGroupSid: null,
            TableClass: tableClass,
            DeclaredTableClass: tableClass,
            TableMaskCategory: maskCategory,
            Columns: new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase),
            Visibility: CatalogVisibilityMode.OnRequest);

    [Fact]
    public void Confidential_on_delegated_refused()
    {
        var options = BuildOptions(EnforcementMode.Delegated);

        var ex = Should.Throw<ValidationException>(() =>
            GatewayStartupValidator.ValidateDelegatedProtection(
                options,
                [("uc-prod", Protection(ProtectionClass.Confidential))]));

        ex.Message.ShouldContain(DelegatedProtectionValidator.ErrorCode);

        Should.Throw<ValidationException>(() =>
            DelegatedProtectionValidator.Validate(
                EnforcementMode.Delegated,
                "uc-prod",
                ProtectionClass.Secret,
                maskCategory: null));
    }

    [Fact]
    public void Pii_on_delegated_refused()
    {
        var options = BuildOptions(EnforcementMode.Delegated);

        var columnMaskProtection = new EffectiveProtection(
            Container: new ContainerRef(ContainerKind.Domain, Guid.NewGuid()),
            OwnerGroupSid: null,
            TableClass: ProtectionClass.Internal,
            DeclaredTableClass: ProtectionClass.Internal,
            TableMaskCategory: null,
            Columns: new Dictionary<string, ColumnProtection>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = new(ProtectionClass.Internal, "email")
            },
            Visibility: CatalogVisibilityMode.OnRequest);

        var ex = Should.Throw<ValidationException>(() =>
            GatewayStartupValidator.ValidateDelegatedProtection(options, [("uc-prod", columnMaskProtection)]));

        ex.Message.ShouldContain(DelegatedProtectionValidator.ErrorCode);
    }

    [Fact]
    public void Defence_in_depth_allows()
    {
        var options = BuildOptions(EnforcementMode.DelegatedDefenceInDepth);

        var ex = Record.Exception(() =>
            GatewayStartupValidator.ValidateDelegatedProtection(
                options,
                [("uc-prod", Protection(ProtectionClass.Secret, maskCategory: "full"))]));

        ex.ShouldBeNull();
    }

    [Fact]
    public void Startup_validation_fails_closed_for_delegated_without_defence_in_depth_and_allows_defence_in_depth()
    {
        var delegatedOptions = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["uc-prod"] = new()
                    {
                        Provider = "PostgreSql",
                        ConnectionString = "Host=h;Database=d",
                        DefaultSchema = "public",
                        EnforcementMode = EnforcementMode.Delegated
                    }
                }
            }
        };

        var ex = Should.Throw<ValidationException>(() =>
            GatewayStartupValidator.ValidateStartup(
                delegatedOptions,
                new TestHostEnvironment("Development"),
                _ => null,
                NullLogger.Instance));

        ex.Message.ShouldContain(DelegatedProtectionValidator.ErrorCode);

        var defenceInDepthOptions = new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["uc-prod"] = new()
                    {
                        Provider = "PostgreSql",
                        ConnectionString = "Host=h;Database=d",
                        DefaultSchema = "public",
                        EnforcementMode = EnforcementMode.DelegatedDefenceInDepth
                    }
                }
            }
        };

        Should.NotThrow(() =>
            GatewayStartupValidator.ValidateStartup(
                defenceInDepthOptions,
                new TestHostEnvironment("Development"),
                _ => null,
                NullLogger.Instance));
    }

    [Fact]
    public async Task CatalogDiscovery_register_datasource_uses_wired_protection_provider_and_refuses_confidential_delegated()
    {
        var tenant = new TenantId("tenant-a");
        var currentClass = ProtectionClass.Confidential;
        var snapshotProvider = new RecordingProtectionProvider((_, _) => Protection(currentClass));

        var options = new GatewayOptions
        {
            Tenancy = TenancyShape.Multi.Options(),
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    ["uc-prod"] = new()
                    {
                        Provider = "PostgreSql",
                        ConnectionString = "Host=h;Database=d",
                        DefaultSchema = "public",
                        EnforcementMode = EnforcementMode.Delegated
                    }
                }
            }
        };

        var ingestion = Substitute.For<IOpenApiIngestionService>();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton(Options.Create(options));
        services.AddSingleton<ITableMetadataRepository>(new InMemoryTableMetadataRepository());
        services.AddSingleton<IConsentRepository>(new FakeConsentRepository());
        services.AddSingleton<IAuditWriter>(new InMemoryAuditWriter());
        services.AddSingleton(ingestion);
        services.AddSingleton<IProtectionSnapshotProvider>(snapshotProvider);
        services.AddAutherisCatalog();

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var discoveryService = scope.ServiceProvider.GetRequiredService<ICatalogDiscoveryService>();

        var request = new DatasourceRegistrationRequest("orders", "uc-prod", SpecContent: """{"openapi":"3.0.0"}""");
        var ctx = new RequestContext(tenant, new Sid("S-1-5-21-ADM"), Roles: ["GovernanceAdmin"]);

        var ex = await Should.ThrowAsync<ValidationException>(() => discoveryService.RegisterDatasourceAsync(request, ctx));
        ex.Message.ShouldContain(DelegatedProtectionValidator.ErrorCode);
        snapshotProvider.RequestedTables.ShouldContain(new TableIdentifier("uc-prod", "public", "orders"));

        currentClass = ProtectionClass.Internal;
        ingestion.IngestOpenApiJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<DatasourceAuthDto?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(new OpenApiIngestionResult(true, "orders", 1, 1, ["orders"], []));

        var ok = await discoveryService.RegisterDatasourceAsync(request, ctx);
        ok.Success.ShouldBeTrue();
    }

    [Fact]
    public async Task AccessPlanning_register_datasource_refuses_delegated_without_defence_in_depth_and_allows_defence_in_depth()
    {
        var delegatedSvc = new AccessPlanningService(
            new InMemoryAuditWriter(),
            options: Options.Create(BuildOptions(EnforcementMode.Delegated)));

        var ex = await Should.ThrowAsync<ValidationException>(() =>
            delegatedSvc.RegisterDatasourceAsync(
                new AdminRegisterDatasourceRequest("orders", "uc-prod"),
                "S-1-5-21-ADM",
                "tenant-a",
                isClusterAdmin: true));
        ex.Message.ShouldContain(DelegatedProtectionValidator.ErrorCode);

        var defenceInDepthSvc = new AccessPlanningService(
            new InMemoryAuditWriter(),
            options: Options.Create(BuildOptions(EnforcementMode.DelegatedDefenceInDepth)));

        var ok = await defenceInDepthSvc.RegisterDatasourceAsync(
            new AdminRegisterDatasourceRequest("orders", "uc-prod"),
            "S-1-5-21-ADM",
            "tenant-a",
            isClusterAdmin: true);
        ok.DatasourceId.ShouldBe("uc-prod.orders");
    }

    [Fact]
    public async Task GovernanceDomain_update_and_assign_tables_enforce_delegated_protection()
    {
        var tenant = new TenantId("tenant-a");
        var domainId = Guid.NewGuid();
        var tableId = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);

        var domains = Substitute.For<IGovernanceDomainRepository>();
        var tables = new InMemoryTableMetadataRepository();
        var consents = new FakeConsentRepository();
        var audit = new InMemoryAuditWriter();
        var caps = new GovernanceCapabilities(domainRepository: domains);

        var delegatedTable = new TableMetadata
        {
            Identifier = new TableIdentifier("uc-prod", "dbo", "orders"),
            OwnerTenantId = tenant.Value,
            Table = new Table
            {
                Id = tableId,
                SourceName = "uc-prod",
                SchemaName = "dbo",
                TableName = "orders",
                EnforcementMode = EnforcementMode.AutherisEnforced,
                IsActive = true
            },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };
        await tables.UpsertTableMetadataAsync(delegatedTable);

        var service = new GovernanceDomainService(
            domains,
            tables,
            consents,
            audit,
            caps,
            timeProvider: new FakeTimeProvider(now),
            gatewayOptions: Options.Create(BuildOptions(EnforcementMode.Delegated)));

        var govCaller = SecurityPrincipalContext.FromPrincipal(
            new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-GOV"), new Claim("tenant_id", tenant.Value), new Claim(ClaimTypes.Role, "GovernanceAdmin")],
                "Bearer")),
            tenant) with
        { GroupResolutionStatus = GroupResolutionStatus.Complete };

        var confidentialDomain = new GovernanceDomain(domainId, tenant, "ConfDomain", null, null, null, ProtectionClass.Confidential, CatalogVisibilityMode.OnRequest, false, 1, now, now);
        domains.GetAsync(tenant, domainId, Arg.Any<CancellationToken>()).Returns(confidentialDomain);
        domains.GetProtectionAsync(tenant, tableId, Arg.Any<CancellationToken>())
            .Returns(((ObjectProtection?)null, (IReadOnlyDictionary<string, ObjectProtection>)new Dictionary<string, ObjectProtection>()));

        var assignDenied = await service.AssignTablesAsync(govCaller, tenant, domainId, [tableId]);
        assignDenied.Status.ShouldBe(DomainServiceStatus.Invalid);
        assignDenied.ErrorCode.ShouldBe(DelegatedProtectionValidator.ErrorCode);
        await domains.DidNotReceiveWithAnyArgs().AssignTablesAsync(default, default, default!, default, default);

        var internalDomain = confidentialDomain with { DefaultClass = ProtectionClass.Internal };
        domains.GetAsync(tenant, domainId, Arg.Any<CancellationToken>()).Returns(internalDomain);
        var assignAllowed = await service.AssignTablesAsync(govCaller, tenant, domainId, [tableId]);
        assignAllowed.Status.ShouldBe(DomainServiceStatus.Ok);

        domains.GetContainerForTableAsync(tenant, tableId, Arg.Any<CancellationToken>())
            .Returns(new ContainerDefaults(new ContainerRef(ContainerKind.Domain, domainId), null, ProtectionClass.Internal, CatalogVisibilityMode.OnRequest, IsValid: true));

        var updateDenied = await service.UpdateDomainAsync(
            govCaller,
            tenant,
            domainId,
            expectedVersion: 1,
            new UpdateGovernanceDomainInput("ConfDomain", DefaultClass: ProtectionClass.Confidential));
        updateDenied.Status.ShouldBe(DomainServiceStatus.Invalid);
        updateDenied.ErrorCode.ShouldBe(DelegatedProtectionValidator.ErrorCode);
        await domains.DidNotReceiveWithAnyArgs().UpsertAsync(default!, default, default, default);
    }

    private sealed class RecordingProtectionProvider(Func<TenantId, TableIdentifier, EffectiveProtection> factory) : IProtectionSnapshotProvider
    {
        public List<TableIdentifier> RequestedTables { get; } = [];

        public ValueTask<EffectiveProtection> GetAsync(TenantId tenant, TableIdentifier table, CancellationToken ct = default)
        {
            RequestedTables.Add(table);
            return ValueTask.FromResult(factory(tenant, table));
        }
    }

    private sealed class TestHostEnvironment(string environmentName) : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "Autheris.Tests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
