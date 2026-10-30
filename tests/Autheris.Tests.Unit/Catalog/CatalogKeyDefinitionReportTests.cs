namespace Autheris.Tests.Unit.Catalog;

using Autheris.Tests.Common;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Services;
using Autheris.Application.DataCatalog.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.OData.Services;
using Autheris.Application.Security.Rebac.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #94 (SD-4): discovery and OpenAPI report the declared definition only as a whole, over listed columns.</summary>
[Trait("Category", "Security")]
public sealed class CatalogKeyDefinitionReportTests
{
    private static readonly TableIdentifier Id = new("crm", "dbo", "orders");

    private static TableMetadata Table() => new()
    {
        Identifier = Id,
        Table = new Table { SchemaName = "dbo", TableName = "orders", DataSourceType = DataSourceType.HttpDeclarative, IsActive = true },
        Columns =
        [
            new TableColumn { ColumnName = "a", DataType = "int" },
            new TableColumn { ColumnName = "b", DataType = "int" }
        ],
        PrimaryKeyColumns = ["a", "ghost"],
        Indexes =
        [
            new TableIndexDefinition("ux_ok", ["a", "b"], true),
            new TableIndexDefinition("ix_ghost", ["b", "ghost"], false)
        ]
    };

    [Fact]
    public async Task DiscoveryDetail_ReportsIndexesWholeOnly_AndNoPartialPrimaryKey()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetTableMetadataAsync(Id, Arg.Any<CancellationToken>()).Returns(Table());
        var consents = Substitute.For<IConsentRepository>();
        var options = Wp0.Options(Autheris.Tests.Common.TenancyShape.Single);
        var service = new CatalogDiscoveryService(
            repo,
            Substitute.For<IAuditLogRepository>(),
            Microsoft.Extensions.Options.Options.Create(options),
            Autheris.Tests.Unit.TestSupport.CatalogVisibilityTestFactory.Create(repo, consents, options, Substitute.For<IRebacEvaluator>()),
            Substitute.For<IKeyVaultSecretProvider>(),
            Substitute.For<IOpenApiIngestionService>(),
            Substitute.For<ICatalogSearchEngine>(),
            NullLogger<CatalogDiscoveryService>.Instance);

        var detail = await service.GetDatasetDetailAsync(Id, new RequestContext(new TenantId("t"), new Sid("user:x"), User: Wp0.User("user:x", null, ["GovernanceAdmin"])));

        detail.ShouldNotBeNull();
        detail.PrimaryKeys.ShouldBeEmpty();
        detail.Columns.ShouldAllBe(c => !c.IsPrimaryKey);
        detail.Indexes.ShouldNotBeNull().Select(i => string.Join(",", i.Columns)).ShouldBe(new[] { "a,b" });
    }

    [Fact]
    public async Task OpenApi_ReportsNoIndexes_AndNoPartialRequiredKey()
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<TableMetadata>>([Table()]));
        var generator = new DynamicOpenApiGenerator(repo, NullLogger<DynamicOpenApiGenerator>.Instance);

        var json = await generator.GenerateOpenApiJsonAsync(null);

        json.ShouldNotContain("ghost");
        using var doc = JsonDocument.Parse(json);
        json.ShouldNotContain("x-autheris-indexes");
        json.ShouldNotContain("ux_ok");
        var entity = doc.RootElement.GetProperty("components").GetProperty("schemas").EnumerateObject()
            .Select(p => p.Value).First(v => v.TryGetProperty("properties", out var props) && props.TryGetProperty("a", out _));
        entity.TryGetProperty("required", out _).ShouldBeFalse();
    }
}
