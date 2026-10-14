namespace Autheris.Tests.Unit.Governance;

using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Audit;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Governance;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>WP-ARC-B-07: role and domain authorization and the success audit of the OpenAPI ingestion live in the use case.</summary>
public sealed class OpenApiIngestionUseCaseTests
{
    private static readonly TenantId Alpha = new("tenant-alpha");

    private static ClaimsPrincipal User(string role) =>
        new(new ClaimsIdentity([new Claim("user_sid", "S-1-5-21-PUB"), new Claim(ClaimTypes.Role, role)], "Test", ClaimTypes.Name, ClaimTypes.Role));

    private static OpenApiIngestionResult Ok() => new(true, "Svc", 2, 5, ["a", "b"], []);

    private static (OpenApiIngestionUseCase Sut, IOpenApiIngestionService Ingestion, IAuditWriter Audit) Create(OpenApiIngestionResult? result = null)
    {
        var ingestion = Substitute.For<IOpenApiIngestionService>();
        ingestion.IngestOpenApiJsonAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(result ?? Ok());
        var audit = Substitute.For<IAuditWriter>();
        return (new OpenApiIngestionUseCase(ingestion, audit), ingestion, audit);
    }

    [Fact]
    public async Task NonAdminCaller_IsDenied_AndDenialIsAudited()
    {
        var (sut, ingestion, audit) = Create();

        var outcome = await sut.IngestAsync(User("StandardUser"), Alpha, false, null, null, "{}", "t", CancellationToken.None);

        outcome.Kind.ShouldBe(OpenApiIngestionOutcomeKind.Forbidden);
        await ingestion.DidNotReceiveWithAnyArgs().IngestOpenApiJsonAsync(default!, default!, default, default);
        await audit.DidNotReceiveWithAnyArgs().RecordRequiredAsync(default!, default);
    }

    [Fact]
    public async Task ForeignDomain_ForNonClusterAdmin_IsForbidden()
    {
        var (sut, ingestion, _) = Create();

        var outcome = await sut.IngestAsync(User("SchemaPublisher"), Alpha, false, "tenant-bravo", null, "{}", "t", CancellationToken.None);

        outcome.Kind.ShouldBe(OpenApiIngestionOutcomeKind.Forbidden);
        await ingestion.DidNotReceiveWithAnyArgs().IngestOpenApiJsonAsync(default!, default!, default, default);
    }

    [Fact]
    public async Task ClusterAdmin_DefaultsToExternalDomain_AndOwnTenantOtherwise()
    {
        var (sut, ingestion, _) = Create();

        await sut.IngestAsync(User("GovernanceAdmin"), Alpha, true, null, null, "{}", "t", CancellationToken.None);
        await sut.IngestAsync(User("SchemaPublisher"), Alpha, false, null, null, "{}", "t", CancellationToken.None);

        await ingestion.Received(1).IngestOpenApiJsonAsync("{}", "external", null, Arg.Any<CancellationToken>());
        await ingestion.Received(1).IngestOpenApiJsonAsync("{}", "tenant-alpha", null, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Success_AuditsAfterCommit_WithoutTheRequestToken()
    {
        var (sut, _, audit) = Create();
        using var cts = new CancellationTokenSource();

        var outcome = await sut.IngestAsync(User("SchemaPublisher"), Alpha, false, "tenant-alpha", "https://x", "{}", "trace-1", cts.Token);

        outcome.Kind.ShouldBe(OpenApiIngestionOutcomeKind.Ingested);
        await audit.Received(1).RecordRequiredAsync(
            Arg.Is<AuditLogEntry>(e => e.EventType == "OPENAPI_CATALOG_INGESTED" && e.TraceId == "trace-1" && e.TargetTable == "tenant-alpha" && e.Decision == "ALLOW"),
            Arg.Is<CancellationToken>(t => !t.CanBeCanceled));
    }

    [Fact]
    public async Task FailedIngestion_IsRejected_WithoutAudit()
    {
        var (sut, _, audit) = Create(new OpenApiIngestionResult(false, "", 0, 0, [], [], "bad"));

        var outcome = await sut.IngestAsync(User("SchemaPublisher"), Alpha, false, null, null, "{}", "t", CancellationToken.None);

        outcome.Kind.ShouldBe(OpenApiIngestionOutcomeKind.Rejected);
        outcome.Result!.ErrorMessage.ShouldBe("bad");
        await audit.DidNotReceiveWithAnyArgs().RecordRequiredAsync(default!, default);
    }
}
