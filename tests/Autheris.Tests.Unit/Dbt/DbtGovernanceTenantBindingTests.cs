namespace Autheris.Tests.Unit.Dbt;

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Dbt.Interfaces;
using Autheris.Application.Interfaces;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Extensions.Dbt;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public class DbtGovernanceTenantBindingTests
{
    private const string MultiTenantManifestJson = """
    {
      "metadata": {
        "dbt_version": "1.8.0",
        "project_name": "multi_tenant_dw"
      },
      "nodes": {
        "model.multi_tenant_dw.tenant_a_orders": {
          "name": "tenant_a_orders",
          "database": "tenant_a",
          "schema": "sales",
          "description": "Tenant A Orders",
          "config": { "materialized": "table" },
          "tags": [],
          "meta": {},
          "columns": {
            "order_id": { "name": "order_id", "data_type": "integer", "tags": [], "meta": {} }
          }
        },
        "model.multi_tenant_dw.tenant_b_orders": {
          "name": "tenant_b_orders",
          "database": "tenant_b",
          "schema": "sales",
          "description": "Tenant B Orders",
          "config": { "materialized": "table" },
          "tags": [],
          "meta": {},
          "columns": {
            "order_id": { "name": "order_id", "data_type": "integer", "tags": [], "meta": {} }
          }
        }
      }
    }
    """;

    [Fact]
    public async Task IngestManifest_NonAdmin_RejectsForeignTenantModel()
    {
        var proposalRepo = Substitute.For<IDbtProposalRepository>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var graphStore = Substitute.For<ILineageGraphStore>();
        var testTargetMap = Substitute.For<IDbtTestTargetMap>();
        var service = new DbtMetadataIngestionService(proposalRepo, metadataRepo, graphStore, NullLogger<DbtMetadataIngestionService>.Instance, testTargetMap: testTargetMap);

        var caller = new DbtGovernanceImportCaller(
            new VirtualFilterActor(new Sid("S-1-USER-A"), IsSync: false),
            new TenantId("tenant_a"),
            IsClusterAdmin: false);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(MultiTenantManifestJson));
        var result = await service.IngestManifestStreamAsync(stream, caller);

        result.Success.ShouldBeTrue();
        result.ParsedModelsCount.ShouldBe(1);
        result.RejectedModels.ShouldNotBeNull();
        result.RejectedModels.Count.ShouldBe(1);
        result.RejectedModels[0].ShouldContain("tenant_b");
        result.Warnings.ShouldContain(w => w.Contains("tenant_b") && w.Contains("not the caller's tenant"));
    }

    [Fact]
    public async Task IngestManifest_ClusterAdmin_AdmitsAllTenantModels()
    {
        var proposalRepo = Substitute.For<IDbtProposalRepository>();
        var metadataRepo = Substitute.For<ITableMetadataRepository>();
        var graphStore = Substitute.For<ILineageGraphStore>();
        var testTargetMap = Substitute.For<IDbtTestTargetMap>();
        var service = new DbtMetadataIngestionService(proposalRepo, metadataRepo, graphStore, NullLogger<DbtMetadataIngestionService>.Instance, testTargetMap: testTargetMap);

        var adminCaller = new DbtGovernanceImportCaller(
            new VirtualFilterActor(new Sid("S-1-ADMIN"), IsSync: false),
            new TenantId("tenant_a"),
            IsClusterAdmin: true);

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(MultiTenantManifestJson));
        var result = await service.IngestManifestStreamAsync(stream, adminCaller);

        result.Success.ShouldBeTrue();
        result.ParsedModelsCount.ShouldBe(2);
        result.RejectedModels.ShouldBeEmpty();
    }
}
