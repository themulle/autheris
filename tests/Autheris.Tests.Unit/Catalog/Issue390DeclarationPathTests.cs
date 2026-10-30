namespace Autheris.Tests.Unit.Catalog;

using System.Security.Claims;
using System.Text;
using Autheris.Api.Endpoints;
using Autheris.Application.Audit;
using Autheris.Application.DataCatalog.Models;
using Autheris.Application.DataCatalog.Services;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Dbt;
using Autheris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #390 (WP-390-3): the declaration paths of key and index column directions (PUT keys, dbt, OpenAPI, catalog sync).</summary>
public sealed class Issue390DeclarationPathTests : IDisposable
{
    private const KeyColumnDirection Asc = KeyColumnDirection.Ascending;
    private const KeyColumnDirection Desc = KeyColumnDirection.Descending;
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    private readonly SqliteGovernanceRepository _repo;
    private readonly AuditWriter _audit;

    public Issue390DeclarationPathTests()
    {
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=i390decl_{Guid.NewGuid():N};Mode=Memory;Cache=Shared", JournalMode = "DELETE" }
        });
        _repo = Autheris.Tests.Unit.GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), options);
        _audit = new AuditWriter(_repo, NullLogger<AuditWriter>.Instance);
    }

    public void Dispose() => _repo.Dispose();

    // ---------------------------------------------------------------- KeyColumnSpec (I390-SC-05)

    [Theory]
    [InlineData("ts", "ts", Asc)]
    [InlineData("  ts  ", "ts", Asc)]
    [InlineData("ts ASC", "ts", Asc)]
    [InlineData("ts asc", "ts", Asc)]
    [InlineData("ts DESC", "ts", Desc)]
    [InlineData("ts desc", "ts", Desc)]
    [InlineData("  ts   DeSc ", "ts", Desc)]
    [InlineData("ts\tDESC", "ts", Desc)]
    public void KeyColumnSpec_TryParse_Theory_AcceptsColumnWithOptionalDirection(string token, string column, KeyColumnDirection direction)
    {
        KeyColumnSpec.TryParse(token, out var parsedColumn, out var parsedDirection).ShouldBeTrue();
        parsedColumn.ShouldBe(column);
        parsedDirection.ShouldBe(direction);
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("ts DESC; DROP")]
    [InlineData("ts DESC DESC")]
    [InlineData("ts ASC DESC")]
    [InlineData("ts NULLS FIRST")]
    [InlineData("ts DESC NULLS LAST")]
    [InlineData("\"ts\" DESC")]
    [InlineData("[ts] DESC")]
    [InlineData("`ts` DESC")]
    [InlineData("t.s DESC")]
    [InlineData("a,b DESC")]
    [InlineData("ts() DESC")]
    [InlineData("ts COLLATE x")]
    [InlineData("ts COLLATE x DESC")]
    [InlineData("ts descending")]
    [InlineData("ts DESC;")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void I390_T_03_KeyColumnSpec_TryParse_RejectsInjectionAndExtraTokens(string? token)
    {
        KeyColumnSpec.TryParse(token, out var column, out var direction).ShouldBeFalse();
        column.ShouldBeEmpty("no partial result");
        direction.ShouldBe(Asc, "no direction leaks from a refused token");
    }

    [Trait("Category", "Security")]
    [Fact]
    public void I390_T_03_KeyColumnSpec_TryParse_RejectsOverLongToken_BeforeParsing()
    {
        KeyColumnSpec.TryParse(new string('a', KeyColumnSpec.MaxTokenLength) + " DESC", out _, out _).ShouldBeFalse("over the bound, even with a valid suffix");
        KeyColumnSpec.TryParse(new string('a', KeyColumnSpec.MaxTokenLength + 1), out _, out _).ShouldBeFalse();
        KeyColumnSpec.TryParse(new string('a', 250) + " DESC", out var column, out var direction).ShouldBeTrue();
        column.Length.ShouldBe(250);
        direction.ShouldBe(Desc);
    }

    // ---------------------------------------------------------------- dbt

    private static string Manifest(string modelConstraints) => $$"""
    {
      "nodes": {
        "model.proj.orders": {
          "name": "orders", "database": "db", "schema": "dbo", "description": "orders",
          "config": { "materialized": "table" },
          "constraints": {{modelConstraints}},
          "columns": {
            "device_id": { "name": "device_id", "data_type": "int", "meta": {}, "tags": [] },
            "ts": { "name": "ts", "data_type": "datetime", "meta": {}, "tags": [] },
            "email": { "name": "email", "data_type": "varchar", "meta": {}, "tags": [] }
          },
          "depends_on": { "nodes": [] }
        }
      }
    }
    """;

    private static async Task<DbtModelDefinition> ParseAsync(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        return (await DbtArtifactStreamingParser.ParseManifestStreamAsync(stream)).Single(m => m.Name == "orders");
    }

    [Fact]
    public async Task DbtConstraint_DescSuffix_ImportsDescending()
    {
        var model = await ParseAsync(Manifest(
            """[{"type":"primary_key","columns":["device_id","ts DESC"]},{"type":"unique","columns":["email desc","ts"]}]"""));

        model.DeclaredKeys!.Invalid.ShouldBeFalse();
        model.DeclaredKeys.PrimaryKey.ShouldBe(["device_id", "ts"]);
        model.DeclaredKeys.PrimaryKeyDirections.ShouldBe([Asc, Desc]);
        model.DeclaredKeys.UniqueConstraints.Single().ShouldBe(["email", "ts"]);
        model.DeclaredKeys.UniqueDirections!.Single().ShouldBe([Desc, Asc]);
    }

    [Fact]
    public async Task DbtConstraint_WithoutSuffix_StaysAscending_WithNoDirectionList()
    {
        var model = await ParseAsync(Manifest("""[{"type":"primary_key","columns":["device_id","ts ASC"]}]"""));

        model.DeclaredKeys!.PrimaryKey.ShouldBe(["device_id", "ts"]);
        model.DeclaredKeys.PrimaryKeyDirections.ShouldBeNull("all ascending is the canonical (absent) form");
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("""[{"type":"primary_key","columns":["device_id","ts DESC; DROP TABLE x"]}]""")]
    [InlineData("""[{"type":"primary_key","columns":["device_id","ts NULLS FIRST"]}]""")]
    [InlineData("""[{"type":"primary_key","columns":["device_id","\"ts\" DESC"]}]""")]
    [InlineData("""[{"type":"primary_key","columns":["device_id","ts DESC DESC"]}]""")]
    [InlineData("""[{"type":"unique","columns":["email","ts COLLATE x"]}]""")]
    public async Task I390_T_04_DbtConstraint_InvalidToken_DropsWholeDefinition_NoPartialKey(string constraints)
    {
        var model = await ParseAsync(Manifest(constraints));

        model.DeclaredKeys.ShouldNotBeNull();
        model.DeclaredKeys!.Invalid.ShouldBeTrue("the whole declaration is dropped, never a partial or ascending-fallback key");
        model.DeclaredKeys.PrimaryKey.ShouldBeNull();
        model.DeclaredKeys.UniqueConstraints.ShouldBeEmpty();
    }

    [Fact]
    public void DbtConstraint_Import_AppliesDirectionsAndDropsMismatch()
    {
        var logger = NullLogger.Instance;
        var meta = new TableMetadata
        {
            Identifier = Orders,
            Columns = [new TableColumn { ColumnName = "device_id" }, new TableColumn { ColumnName = "ts" }]
        };

        var collector = new CatalogKeyDefinitionImport.Collector();
        collector.AddPrimaryKey(["device_id", "ts"], [Asc, Desc]);
        var applied = CatalogKeyDefinitionImport.Apply(meta, collector, logger, "dbt");
        applied.ReplaceKeyDefinition.ShouldBeTrue();
        applied.PrimaryKeyDirections.ShouldBe([Asc, Desc]);

        var mismatch = new CatalogKeyDefinitionImport.Collector();
        mismatch.AddPrimaryKey(["device_id", "ts"], [Desc]);
        mismatch.Invalid.ShouldBeTrue();
        CatalogKeyDefinitionImport.Apply(meta, mismatch, logger, "dbt").ReplaceKeyDefinition.ShouldBeFalse();
    }

    // ---------------------------------------------------------------- OpenAPI

    private static string Spec(string schemaExtras) => $$"""
    {
      "openapi": "3.0.0",
      "info": { "title": "Svc", "version": "1" },
      "servers": [ { "url": "https://svc.example.com" } ],
      "paths": { "/orders": { "get": { "summary": "list" } } },
      "components": { "schemas": { "Order": {
        "type": "object",
        {{schemaExtras}}
        "properties": { "device_id": { "type": "integer" }, "ts": { "type": "string", "format": "date-time" }, "sku": { "type": "string" } }
      } } }
    }
    """;

    private static async Task<TableMetadata> IngestAsync(string json)
    {
        var repo = Substitute.For<ITableMetadataRepository>();
        var upserts = new List<TableMetadata>();
        repo.UpsertTableMetadataAsync(Arg.Do<TableMetadata>(upserts.Add), Arg.Any<CancellationToken>())
            .Returns(c => Task.FromResult(c.Arg<TableMetadata>()));
        var service = new OpenApiIngestionService(repo, NullLogger<OpenApiIngestionService>.Instance);
        (await service.IngestOpenApiJsonAsync(json, domain: "svc")).Success.ShouldBeTrue();
        return upserts.Single();
    }

    [Fact]
    public async Task OpenApi_DirectionExtensions_Imported()
    {
        var meta = await IngestAsync(Spec("""
            "x-autheris-primary-key": ["device_id", "ts"],
            "x-autheris-primary-key-directions": ["asc", "DESC"],
            "x-autheris-indexes": [ { "name": "ux_sku_ts", "columns": ["sku", "ts"], "unique": true, "directions": ["ASC", "DESC"] }, { "name": "ix_sku", "columns": ["sku"], "directions": ["ASC"] } ],
            """));

        meta.ReplaceKeyDefinition.ShouldBeTrue();
        meta.PrimaryKeyColumns.ShouldBe(["device_id", "ts"]);
        meta.PrimaryKeyDirections.ShouldBe([Asc, Desc]);
        meta.Indexes.Single(i => i.Name == "ux_sku_ts").Directions.ShouldBe([Asc, Desc]);
        meta.Indexes.Single(i => i.Name == "ix_sku").Directions.ShouldBeNull();
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("\"x-autheris-primary-key\": [\"device_id\", \"ts\"], \"x-autheris-primary-key-directions\": [\"ASC\"],")]
    [InlineData("\"x-autheris-primary-key\": [\"device_id\", \"ts\"], \"x-autheris-primary-key-directions\": [\"ASC\", \"DESC\", \"DESC\"],")]
    [InlineData("\"x-autheris-primary-key\": [\"device_id\", \"ts\"], \"x-autheris-primary-key-directions\": [\"ASC\", \"DESC NULLS FIRST\"],")]
    [InlineData("\"x-autheris-primary-key\": [\"device_id\", \"ts\"], \"x-autheris-primary-key-directions\": [\"ASC\", 1],")]
    [InlineData("\"x-autheris-primary-key\": [\"device_id\", \"ts\"], \"x-autheris-primary-key-directions\": \"DESC\",")]
    [InlineData("\"x-autheris-primary-key-directions\": [\"DESC\"],")]
    [InlineData("\"x-autheris-indexes\": [ { \"name\": \"ix\", \"columns\": [\"sku\", \"ts\"], \"directions\": [\"DESC\"] } ],")]
    [InlineData("\"x-autheris-indexes\": [ { \"name\": \"ix\", \"columns\": [\"sku\"], \"directions\": [\"sideways\"] } ],")]
    public async Task OpenApi_InvalidDirections_DropWholeDefinition(string extras)
    {
        var meta = await IngestAsync(Spec(extras));

        meta.ReplaceKeyDefinition.ShouldBeFalse();
        meta.PrimaryKeyColumns.ShouldBeEmpty();
        meta.PrimaryKeyDirections.ShouldBeEmpty();
        meta.Indexes.ShouldBeEmpty();
    }

    // ---------------------------------------------------------------- catalog sync

    [Fact]
    public void DataCatalogSync_AssetDirections_Imported()
    {
        var meta = new TableMetadata
        {
            Identifier = Orders,
            Columns = [new TableColumn { ColumnName = "device_id" }, new TableColumn { ColumnName = "ts" }]
        };
        var asset = new CatalogTableAsset
        {
            Identifier = Orders,
            PrimaryKeyColumns = ["device_id", "ts"],
            PrimaryKeyDirections = [Asc, Desc],
            Indexes = [new TableIndexDefinition("ix_ts", ["ts"], false, [Desc])]
        };

        var applied = CatalogKeyDefinitionImport.Apply(
            meta, asset.PrimaryKeyColumns, asset.Indexes, NullLogger.Instance, "DataCatalog",
            asset.PrimaryKeyColumns.Count > 0 ? asset.PrimaryKeyDirections : null);

        applied.PrimaryKeyDirections.ShouldBe([Asc, Desc]);
        applied.Indexes.Single().Directions.ShouldBe([Desc]);
    }

    // ---------------------------------------------------------------- PUT keys

    private static TableMetadata Meta(TableIdentifier id) => new()
    {
        Identifier = id,
        Table = new Table { SourceType = "sql", SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, DisplayName = id.TableName, IsActive = true },
        Columns = [new TableColumn { ColumnName = "a", DataType = "integer" }, new TableColumn { ColumnName = "b", DataType = "integer" }, new TableColumn { ColumnName = "ssn", DataType = "varchar" }],
        ReplaceKeyDefinition = true
    };

    private static HttpContext Context()
    {
        var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Response.Body = new MemoryStream();
        ctx.TraceIdentifier = "trace-1";
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("tenant_id", "tenant-a"), new Claim(ClaimTypes.Role, "GovernanceAdmin"), new Claim(ClaimTypes.NameIdentifier, "S-1-5-21-ADMIN")], "TestAuth"));
        return ctx;
    }

    private static async Task<(int Status, string Body)> Run(IResult result)
    {
        var ctx = Context();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray()));
    }

    private Task<IResult> Put(SetDatasetKeysDto dto, ITableMetadataRepository? repo = null) =>
        CatalogApiEndpoints.SetDatasetKeysHandler("sales.dbo.orders", dto, repo ?? _repo, _audit, Context(), CancellationToken.None);

    private string? AuditDetails() => Convert.ToString(Scalar(
        "SELECT details_json FROM AUDIT_LOG_ENTRIES WHERE event_type = 'CATALOG_KEY_DEFINITION_CHANGED' ORDER BY seq DESC LIMIT 1"));

    private object? Scalar(string sql)
    {
        using var cmd = _repo.RawConnection().CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    [Fact]
    public async Task SetDatasetKeys_WithDirections_PersistsAndAudits()
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders));

        var (status, body) = await Run(await Put(new SetDatasetKeysDto(
            PrimaryKey: ["a", "b"], PrimaryKeyDirections: ["asc", "DESC"],
            Indexes: [new DatasetIndexDto("ix_b", ["b", "a"], false, Directions: ["DESC", "ASC"])])));

        status.ShouldBe(200);
        body.ShouldContain("\"primaryKeyDirections\":[\"ASC\",\"DESC\"]");
        body.ShouldContain("\"directions\":[\"DESC\",\"ASC\"]");
        _repo.InvalidateMetadataCacheForTesting();
        var read = (await _repo.GetTableMetadataAsync(Orders))!;
        read.PrimaryKeyDirections.ShouldBe([Asc, Desc]);
        read.Indexes.Single().Directions.ShouldBe([Desc, Asc]);
        var details = AuditDetails()!;
        details.ShouldContain("\"primaryKeyDirections\":[\"ASC\",\"DESC\"]", Case.Sensitive, "the after view carries the closed strings");
        details.ShouldContain("\"after\"");
    }

    [Fact]
    public async Task SetDatasetKeys_DirectionOnlyChange_IsAuditedWithBeforeAndAfter()
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders));
        await Run(await Put(new SetDatasetKeysDto(PrimaryKey: ["a", "b"])));

        await Run(await Put(new SetDatasetKeysDto(PrimaryKey: ["a", "b"], PrimaryKeyDirections: ["ASC", "DESC"])));

        var details = AuditDetails()!;
        using var doc = System.Text.Json.JsonDocument.Parse(details);
        doc.RootElement.GetProperty("before").TryGetProperty("primaryKeyDirections", out _).ShouldBeFalse("ascending before: canonical form, no directions");
        doc.RootElement.GetProperty("after").GetProperty("primaryKeyDirections").EnumerateArray().Select(e => e.GetString()).ShouldBe(["ASC", "DESC"]);
    }

    [Fact]
    public async Task SetDatasetKeys_WithoutDirections_BehavesAsToday()
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders));

        var (status, body) = await Run(await Put(new SetDatasetKeysDto(PrimaryKey: ["a", "b"], Indexes: [new DatasetIndexDto("ux_b", ["b"], true)])));

        status.ShouldBe(200);
        body.ShouldNotContain("irections");
        AuditDetails()!.ShouldNotContain("irections");
        (await _repo.GetTableMetadataAsync(Orders))!.PrimaryKeyDirections.ShouldBeEmpty();
    }

    [Trait("Category", "Security")]
    [Theory]
    [InlineData("sideways")]
    [InlineData("DESC; DROP TABLE x")]
    [InlineData("")]
    [InlineData(null)]
    public async Task I390_T_05_SetDatasetKeys_InvalidDirection_Returns400_NoEchoNoAuditLeak(string? invalid)
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders));
        var auditBefore = Convert.ToInt64(Scalar("SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE event_type = 'CATALOG_KEY_DEFINITION_CHANGED'"));

        var (status, body) = await Run(await Put(new SetDatasetKeysDto(PrimaryKey: ["a", "b"], PrimaryKeyDirections: ["ASC", invalid])));

        status.ShouldBe(400);
        using (var problem = System.Text.Json.JsonDocument.Parse(body))
        {
            problem.RootElement.GetProperty("code").GetString().ShouldBe("BAD_REQUEST");
            problem.RootElement.GetProperty("detail").GetString().ShouldBe("The table key or index definition is invalid.");
        }

        if (!string.IsNullOrEmpty(invalid))
        {
            body.ShouldNotContain(invalid);
        }

        Convert.ToInt64(Scalar("SELECT COUNT(*) FROM AUDIT_LOG_ENTRIES WHERE event_type = 'CATALOG_KEY_DEFINITION_CHANGED'")).ShouldBe(auditBefore, "nothing is audited for a refused request");
        _repo.InvalidateMetadataCacheForTesting();
        (await _repo.GetTableMetadataAsync(Orders))!.PrimaryKeyColumns.ShouldBeEmpty("nothing was persisted");
    }

    [Trait("Category", "Security")]
    [Fact]
    public async Task I390_T_05_SetDatasetKeys_DirectionCountMismatch_Returns400_BeforePersistence()
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders));
        var spy = Substitute.For<ITableMetadataRepository>();
        spy.GetActiveTableIdentifierAsync(Orders, Arg.Any<CancellationToken>()).Returns(Orders);

        var (status, body) = await Run(await Put(new SetDatasetKeysDto(PrimaryKey: ["a", "b"], PrimaryKeyDirections: ["DESC"]), spy));
        var (indexStatus, _) = await Run(await Put(new SetDatasetKeysDto(PrimaryKey: ["a"], Indexes: [new DatasetIndexDto("ix", ["a", "b"], false, Directions: ["DESC"])]), spy));
        var tooMany = Enumerable.Repeat("ASC", TableKeyDefinitionValidator.MaxColumnsPerKey + 1).ToArray();
        var (cappedStatus, _) = await Run(await Put(new SetDatasetKeysDto(PrimaryKey: ["a"], PrimaryKeyDirections: tooMany), spy));

        status.ShouldBe(400);
        indexStatus.ShouldBe(400);
        cappedStatus.ShouldBe(400);
        body.ShouldNotContain("DESC");
        await spy.DidNotReceiveWithAnyArgs().ReplaceTableKeyDefinitionAsync(default!, default!, default!, default, default);
    }
}
