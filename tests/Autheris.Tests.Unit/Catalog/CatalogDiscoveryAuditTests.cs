namespace Autheris.Tests.Unit.Catalog;

using Autheris.Application.Audit;
using Autheris.Application.Catalog.Interfaces;
using Autheris.Application.Catalog.Services;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>SEC-36-SP2-07: the catalog datasource registration is refused, before ingestion and secret storage, when its required audit write fails.</summary>
[Trait("Category", "Security")]
public sealed class CatalogDiscoveryAuditTests
{
    [Fact]
    public async Task Register_datasource_is_refused_when_audit_write_fails()
    {
        var writer = Substitute.For<IAuditWriter>();
        writer.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns(Task.FromException(new AuditUnavailableException("down")));
        var ingestion = Substitute.For<IOpenApiIngestionService>();
        var service = new CatalogDiscoveryService(
            Substitute.For<ITableMetadataRepository>(), writer, Options.Create(new GatewayOptions()), openApiIngestionService: ingestion);
        var request = new DatasourceRegistrationRequest("crane", "telemetry", SpecContent: "{}", BaseUrl: "https://api.example.test");

        await Should.ThrowAsync<AuditUnavailableException>(() => service.RegisterDatasourceAsync(request, new RequestContext(new TenantId("tenant-a"), new Sid("user:admin"), Roles: ["GovernanceAdmin"])));

        await ingestion.DidNotReceiveWithAnyArgs().IngestOpenApiJsonAsync(default!, default!, default, default, default, default);
    }
}
