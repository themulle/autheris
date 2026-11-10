namespace Autheris.Tests.Unit.Catalog;

using System.Data.Common;
using System.Security.Claims;
using Autheris.Application.Audit;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>SEC-36-SP2-01, SEC-36-SP2-02, SEC-36-SP2-03: the datasource probe is audited with required writes around the outbound connection.</summary>
[Trait("Category", "Security")]
public sealed class DatasourceTestAuditTests
{
    private const string Source = "gateway_lwe";

    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly ISqlConnectionFactory _factory = Substitute.For<ISqlConnectionFactory>();
    private readonly IAuditWriter _writer = Substitute.For<IAuditWriter>();
    private readonly List<string> _events = [];
    private readonly List<AuditLogEntry> _entries = [];

    public DatasourceTestAuditTests()
    {
        _writer.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            var entry = call.Arg<AuditLogEntry>();
            _events.Add(entry.EventType);
            _entries.Add(entry);
            return Task.CompletedTask;
        });
        _factory.CreateOpenConnectionAsync(Arg.Any<DataSourceConnectionOptions>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _events.Add("CONNECT");
            return Task.FromResult<DbConnection>(new global::Autheris.Tests.Unit.FakeDbConnection(null));
        });
    }

    private static ClaimsPrincipal User(string? tenant)
    {
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, "admin") };
        if (tenant is not null)
        {
            claims.Add(new Claim("tenant_id", tenant));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, "Test"));
    }

    private DatasourceTestingService CreateService(string? defaultTenant = null, string? owner = null)
    {
        var table = new Table { SourceName = Source, SchemaName = "dbo", TableName = "orders", DataSourceType = DataSourceType.Sql };
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(new[]
        {
            new TableMetadata { Identifier = new TableIdentifier(Source, "dbo", "orders"), Table = table, Columns = [], PrimaryKeyColumns = [], OwnerTenantId = owner }
        });
        var gateway = new GatewayOptions { DataSources = new SqlDataSourceOptions(), Tenancy = new TenancyOptions { DefaultTenantId = defaultTenant } };
        gateway.DataSources.Connections[Source] = new DataSourceConnectionOptions { Provider = "SqlServer", ConnectionString = "Server=db.example.test,1433;Database=x;" };
        return new DatasourceTestingService(
            _metadataRepo, Substitute.For<IHttpClientFactory>(), _writer, null, null, null,
            _factory, Options.Create(gateway), new SqlSourceDescriber());
    }

    [Fact]
    public async Task Probe_is_refused_when_audit_write_fails()
    {
        _writer.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new AuditUnavailableException("down")));

        await Should.ThrowAsync<AuditUnavailableException>(() => CreateService().TestDatasourceAsync(Source, new DatasourceTestRequest(), User("t1")));

        await _factory.DidNotReceiveWithAnyArgs().CreateOpenConnectionAsync(default!, default);
    }

    [Fact]
    public async Task DatasourceTest_AuditUnavailable_RefusesProbe()
    {
        _writer.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new AuditUnavailableException("down")));

        await Should.ThrowAsync<AuditUnavailableException>(() => CreateService().TestDatasourceAsync(Source, new DatasourceTestRequest(), User("t1")));

        await _factory.DidNotReceiveWithAnyArgs().CreateOpenConnectionAsync(default!, default);
    }

    [Fact]
    public async Task Probe_intent_audit_precedes_outbound_connection()
    {
        await CreateService().TestDatasourceAsync(Source, new DatasourceTestRequest(), User("t1"));

        _events.ShouldBe([AuditEventTypes.DatasourceTestRequested, "CONNECT", AuditEventTypes.DatasourceTested]);
    }

    [Fact]
    public async Task Probe_result_is_withheld_when_outcome_audit_fails()
    {
        _writer.RecordRequiredAsync(Arg.Is<AuditLogEntry>(e => e.EventType == AuditEventTypes.DatasourceTested), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new AuditUnavailableException("down")));

        await Should.ThrowAsync<AuditUnavailableException>(() => CreateService().TestDatasourceAsync(Source, new DatasourceTestRequest(), User("t1")));
    }

    [Fact]
    public async Task Probe_without_valid_tenant_claim_is_refused_in_multi_tenant_mode()
    {
        await Should.ThrowAsync<TenantUnresolvedException>(() => CreateService().TestDatasourceAsync(Source, new DatasourceTestRequest(), User(null)));

        _events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Probe_with_invalid_tenant_claim_is_refused_even_with_a_default_tenant()
    {
        await Should.ThrowAsync<TenantClaimInvalidException>(() => CreateService("default").TestDatasourceAsync(Source, new DatasourceTestRequest(), User("  ")));

        _events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Probe_without_tenant_claim_is_audited_under_default_tenant()
    {
        await CreateService("default").TestDatasourceAsync(Source, new DatasourceTestRequest(), User(null));

        _entries.ShouldNotBeEmpty();
        _entries.ShouldAllBe(e => e.TenantId.Value == "default");
    }

    [Fact]
    public async Task Probe_unauthorized_datasource_writes_no_audit_row()
    {
        await Should.ThrowAsync<KeyNotFoundException>(() =>
            CreateService(owner: "tenant-b").TestDatasourceAsync(Source, new DatasourceTestRequest(), User("t1"), new TenantId("t1")));

        _events.ShouldBeEmpty();
    }
}
