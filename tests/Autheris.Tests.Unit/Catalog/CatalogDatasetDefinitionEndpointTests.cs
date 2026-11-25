namespace Autheris.Tests.Unit.Catalog;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Api.Endpoints;
using Autheris.Api.Errors;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Domain.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>
/// WP-SIMPL-44 / SEC-SIMPL-143: PUT /api/v1/catalog/datasets/{id}/definition uploads declared catalog metadata.
/// </summary>
public sealed class CatalogDatasetDefinitionEndpointTests
{
    private static IOptions<GatewayOptions> CreateOptions(
        string dataSource = "sales",
        string provider = "PostgreSql",
        IReadOnlyDictionary<string, string>? mappings = null)
    {
        var webSql = mappings == null
            ? new WebSqlOptions()
            : new WebSqlOptions { DataSourceMappings = new Dictionary<string, string>(mappings, StringComparer.OrdinalIgnoreCase) };
        return Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>(StringComparer.OrdinalIgnoreCase)
                {
                    [dataSource] = new DataSourceConnectionOptions { Provider = provider }
                }
            },
            WebSql = webSql
        });
    }

    private static DefaultHttpContext CreateContext()
    {
        var context = new DefaultHttpContext();
        context.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "admin"), new Claim("tenant_id", "tenant-a"), new Claim(ClaimTypes.Role, "GovernanceAdmin")], "TestAuth"));
        return context;
    }

    private sealed class PermissiveProvider(IServiceProvider inner) : IServiceProvider, IServiceProviderIsService
    {
        public object? GetService(Type serviceType) =>
            serviceType == typeof(IServiceProviderIsService) ? this : inner.GetService(serviceType);

        public bool IsService(Type serviceType) => serviceType.IsInterface || inner.GetService(serviceType) != null;

        public bool IsKeyedService(Type serviceType, object? serviceKey) => false;
    }

    private sealed class NullAuditWriter : IAuditWriter
    {
        public int WriteCalls { get; private set; }
        public Task RecordRequiredAsync(AuditLogEntry entry, CancellationToken ct = default) { WriteCalls++; return Task.CompletedTask; }
        public Task RecordBestEffortAsync(AuditLogEntry entry, CancellationToken ct = default) { WriteCalls++; return Task.CompletedTask; }
        public Task RecordAsync(AuditLogEntry entry, CancellationToken ct = default) { WriteCalls++; return Task.CompletedTask; }
    }

    private sealed class FakeTableMetadataRepository : ITableMetadataRepository
    {
        public TableMetadata? Stored { get; set; }
        public int UpsertCalls { get; private set; }

        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default)
            => Task.FromResult(Stored);

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<TableMetadata>>(Stored != null ? [Stored] : []);

        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default)
        {
            UpsertCalls++;
            Stored = metadata;
            return Task.FromResult(metadata);
        }

        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(
            TableIdentifier table,
            IReadOnlyList<string> primaryKey,
            IReadOnlyList<TableIndexDefinition> indexes,
            IReadOnlyList<KeyColumnDirection>? primaryKeyDirections = null,
            CancellationToken ct = default)
            => Task.FromResult<TableKeyDefinitionChange?>(null);
    }

    private sealed class TestRouteBuilder(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public ICollection<EndpointDataSource> DataSources { get; } = [];
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }

    [Fact]
    [Trait("Category", "Security")]
    public void Put_RequiresGovernanceAdmin()
    {
        var services = new ServiceCollection();
        services.AddRouting();
        services.AddAuthorization();
        var routes = new TestRouteBuilder(new PermissiveProvider(services.BuildServiceProvider()));
        routes.MapCatalogEndpoints();

        var endpoints = routes.DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText != null &&
                        e.RoutePattern.RawText.EndsWith("/datasets/{id}/definition", StringComparison.Ordinal) &&
                        e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("PUT"))
            .ToList();

        endpoints.ShouldNotBeEmpty();
        foreach (var endpoint in endpoints)
        {
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(a => a.Policy)
                .ShouldContain(GatewayPolicies.GovernanceAdmin);
        }
    }

    [Fact]
    public async Task Put_CreatesThenReplaces_KeepsMaskingAndConsent()
    {
        var repo = new FakeTableMetadataRepository();
        var audit = new NullAuditWriter();

        var tableId = "sales.public.orders";
        var createRequest = new CatalogDatasetDefinitionRequest(
            SchemaVersion: 1,
            DataSource: "sales",
            Kind: "table",
            Columns: [
                new CatalogDatasetDefinitionColumn("id", "int"),
                new CatalogDatasetDefinitionColumn("amount", "decimal(10,2)"),
                new CatalogDatasetDefinitionColumn("tenant_id", "nvarchar(64)")
            ],
            TenantColumn: "tenant_id",
            PrimaryKey: ["id DESC"]);

        var createResult = await CatalogApiEndpoints.PutDatasetDefinitionHandler(
            tableId, createRequest, repo, audit, CreateOptions(), CreateContext(), CancellationToken.None);

        var createStatus = ((IStatusCodeHttpResult)createResult).StatusCode ?? StatusCodes.Status200OK;
        createStatus.ShouldBe(StatusCodes.Status200OK);
        var stored = repo.Stored;
        stored.ShouldNotBeNull();
        stored.Table.SourceType.ShouldBe("PostgreSql");
        stored.Table.Dialect.ShouldBe(DatabaseDialect.PostgreSql);
        stored.Columns.Count.ShouldBe(3);
        stored.TenantColumnName.ShouldBe("tenant_id");
        stored.PrimaryKeyColumns.ShouldBe(new[] { "id" });
        stored.PrimaryKeyDirections.ShouldBe(new[] { KeyColumnDirection.Descending });

        // Add a masking rule to existing stored metadata
        var maskingRule = new MaskingRule { TableColumnId = stored.Columns[0].Id, RuleType = "hash" };
        repo.Stored = stored with { ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["id"] = maskingRule } };

        // Second call: replace columns (remove amount, add status)
        var replaceRequest = new CatalogDatasetDefinitionRequest(
            SchemaVersion: 1,
            DataSource: "sales",
            Kind: "table",
            Columns: [
                new CatalogDatasetDefinitionColumn("id", "int"),
                new CatalogDatasetDefinitionColumn("status", "varchar(20)"),
                new CatalogDatasetDefinitionColumn("tenant_id", "nvarchar(64)")
            ],
            TenantColumn: "tenant_id",
            PrimaryKey: ["id"]);

        var replaceResult = await CatalogApiEndpoints.PutDatasetDefinitionHandler(
            tableId, replaceRequest, repo, audit, CreateOptions(), CreateContext(), CancellationToken.None);

        var replaceStatus = ((IStatusCodeHttpResult)replaceResult).StatusCode ?? StatusCodes.Status200OK;
        replaceStatus.ShouldBe(StatusCodes.Status200OK);
        stored = repo.Stored;
        stored.ShouldNotBeNull();
        stored.Table.SourceType.ShouldBe("PostgreSql");
        stored.Table.Dialect.ShouldBe(DatabaseDialect.PostgreSql);
        stored.Columns.Select(c => c.ColumnName).ShouldBe(new[] { "id", "status", "tenant_id" });
        stored.ColumnMaskingRules.ShouldContainKey("id");
        stored.ReplaceColumns.ShouldBeTrue();
        stored.ReplaceKeyDefinition.ShouldBeTrue();
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("sales", "sys", "orders")]
    [InlineData("sales", "information_schema", "orders")]
    [InlineData("sales", "pg_catalog", "orders")]
    [InlineData("sales", "SYS", "orders")]
    [InlineData("system", "public", "orders")]
    [InlineData("sales", "public", "ALL_TABLES")]
    public async Task Reserved_source_names_rejected(string domain, string schema, string table)
    {
        var repo = new FakeTableMetadataRepository();
        var audit = new NullAuditWriter();
        var request = new CatalogDatasetDefinitionRequest(
            SchemaVersion: 1,
            DataSource: domain,
            Kind: "table",
            Columns: [new CatalogDatasetDefinitionColumn("id", "int")]);

        var result = await CatalogApiEndpoints.PutDatasetDefinitionHandler(
            $"{domain}.{schema}.{table}", request, repo, audit, CreateOptions(domain), CreateContext(), CancellationToken.None);

        var problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        problem.ProblemDetails.Extensions["code"]?.ToString().ShouldBe(ApiErrorCodes.CatalogReservedSourceName);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Put_LimitsEnforced()
    {
        var repo = new FakeTableMetadataRepository();
        var audit = new NullAuditWriter();

        // Over 1024 columns rejected
        var tooManyColumns = Enumerable.Range(1, 1025)
            .Select(i => new CatalogDatasetDefinitionColumn($"col_{i}", "int"))
            .ToList();
        var reqColumns = new CatalogDatasetDefinitionRequest(1, "sales", "table", tooManyColumns);
        var resColumns = await CatalogApiEndpoints.PutDatasetDefinitionHandler(
            "sales.public.orders", reqColumns, repo, audit, CreateOptions(), CreateContext(), CancellationToken.None);
        resColumns.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(StatusCodes.Status400BadRequest);

        // Over 32 indexes rejected
        var validColumns = new[] { new CatalogDatasetDefinitionColumn("id", "int") };
        var tooManyIndexes = Enumerable.Range(1, 33)
            .Select(i => new CatalogDatasetDefinitionIndex($"idx_{i}", ["id"]))
            .ToList();
        var reqIndexes = new CatalogDatasetDefinitionRequest(1, "sales", "table", validColumns, Indexes: tooManyIndexes);
        var resIndexes = await CatalogApiEndpoints.PutDatasetDefinitionHandler(
            "sales.public.orders", reqIndexes, repo, audit, CreateOptions(), CreateContext(), CancellationToken.None);
        resIndexes.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Theory]
    [InlineData("123bad")]
    [InlineData("int; DROP TABLE t;")]
    [InlineData("nvarchar(10,2,3)")]
    [InlineData("varchar(max,1)")]
    [InlineData("bad!type")]
    public async Task Put_InvalidNativeType_Rejected(string invalidType)
    {
        var repo = new FakeTableMetadataRepository();
        var audit = new NullAuditWriter();
        var request = new CatalogDatasetDefinitionRequest(
            1, "sales", "table", [new CatalogDatasetDefinitionColumn("id", invalidType)]);

        var result = await CatalogApiEndpoints.PutDatasetDefinitionHandler(
            "sales.public.orders", request, repo, audit, CreateOptions(), CreateContext(), CancellationToken.None);

        result.ShouldBeOfType<ProblemHttpResult>().StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Put_NeverOpensSourceConnection()
    {
        // SEC-SIMPL-142: Definition endpoint interacts with metadataRepository and audit only, never ISqlConnectionFactory
        var repo = new FakeTableMetadataRepository();
        var audit = new NullAuditWriter();
        var request = new CatalogDatasetDefinitionRequest(
            1, "sales", "table", [new CatalogDatasetDefinitionColumn("id", "int")]);

        var result = await CatalogApiEndpoints.PutDatasetDefinitionHandler(
            "sales.public.orders", request, repo, audit, CreateOptions(), CreateContext(), CancellationToken.None);

        var status = ((IStatusCodeHttpResult)result).StatusCode ?? StatusCodes.Status200OK;
        status.ShouldBe(StatusCodes.Status200OK);
        repo.UpsertCalls.ShouldBe(1);
    }

    [Theory]
    [InlineData("kostentransparenz_dbx", "Databricks", "Databricks", DatabaseDialect.Databricks)]
    [InlineData("lake", "sparksql", "Databricks", DatabaseDialect.Databricks)]
    [InlineData("erp", "pgsql", "PostgreSql", DatabaseDialect.PostgreSql)]
    [InlineData("crm", "mssql", "SqlServer", DatabaseDialect.SqlServer)]
    [InlineData("fin", "oracledb", "Oracle", DatabaseDialect.Oracle)]
    public async Task Put_ResolvesConfiguredProviderToCanonicalDialect(
        string dataSource,
        string configuredProvider,
        string expectedSourceType,
        DatabaseDialect expectedDialect)
    {
        var repo = new FakeTableMetadataRepository();
        var audit = new NullAuditWriter();
        var request = new CatalogDatasetDefinitionRequest(
            SchemaVersion: 1,
            DataSource: dataSource,
            Kind: "table",
            Columns: [new CatalogDatasetDefinitionColumn("date_key", "int")]);

        var result = await CatalogApiEndpoints.PutDatasetDefinitionHandler(
            $"{dataSource}.controlling.dim_date",
            request,
            repo,
            audit,
            CreateOptions(dataSource.ToUpperInvariant(), configuredProvider),
            CreateContext(),
            CancellationToken.None);

        var status = ((IStatusCodeHttpResult)result).StatusCode ?? StatusCodes.Status200OK;
        status.ShouldBe(StatusCodes.Status200OK);
        repo.Stored.ShouldNotBeNull();
        repo.Stored.Table.SourceName.ShouldBe(dataSource);
        repo.Stored.Table.SourceType.ShouldBe(expectedSourceType);
        repo.Stored.Table.Dialect.ShouldBe(expectedDialect);
    }

    [Fact]
    public async Task Put_ResolvesLogicalDataSourceViaWebSqlMappings()
    {
        var repo = new FakeTableMetadataRepository();
        var audit = new NullAuditWriter();
        var request = new CatalogDatasetDefinitionRequest(
            SchemaVersion: 1,
            DataSource: "kostentransparenz_dbx",
            Kind: "table",
            Columns: [new CatalogDatasetDefinitionColumn("date_key", "int")]);
        var options = CreateOptions(
            dataSource: "phys_dbx",
            provider: "Databricks",
            mappings: new Dictionary<string, string> { ["kostentransparenz_dbx"] = "phys_dbx" });

        var result = await CatalogApiEndpoints.PutDatasetDefinitionHandler(
            "kostentransparenz_dbx.controlling.dim_date",
            request,
            repo,
            audit,
            options,
            CreateContext(),
            CancellationToken.None);

        var status = ((IStatusCodeHttpResult)result).StatusCode ?? StatusCodes.Status200OK;
        status.ShouldBe(StatusCodes.Status200OK);
        repo.Stored.ShouldNotBeNull();
        repo.Stored.Table.SourceName.ShouldBe("kostentransparenz_dbx");
        repo.Stored.Table.SourceType.ShouldBe("Databricks");
        repo.Stored.Table.Dialect.ShouldBe(DatabaseDialect.Databricks);
    }

    [Fact]
    public async Task Put_ReplacesPoisonedExistingSourceType_AndPreservesGovernance()
    {
        var tableId = new TableIdentifier("kostentransparenz_dbx", "controlling", "dim_date");
        var existingGuid = Guid.NewGuid();
        var existingColGuid = Guid.NewGuid();
        var repo = new FakeTableMetadataRepository
        {
            Stored = new TableMetadata
            {
                Identifier = tableId,
                Table = new Table
                {
                    Id = existingGuid,
                    SourceName = "kostentransparenz_dbx",
                    SchemaName = "controlling",
                    TableName = "dim_date",
                    SourceType = "kostentransparenz_dbx",
                    IsActive = true,
                    DataSourceType = DataSourceType.Sql
                },
                Columns = [new TableColumn { Id = existingColGuid, TableId = existingGuid, ColumnName = "date_key", DataType = "int" }],
                ColumnMaskingRules = new Dictionary<string, MaskingRule>
                {
                    ["date_key"] = new MaskingRule { TableColumnId = existingColGuid, RuleType = "REDACT", Replacement = "***" }
                }
            }
        };
        var audit = new NullAuditWriter();
        var request = new CatalogDatasetDefinitionRequest(
            SchemaVersion: 1,
            DataSource: "kostentransparenz_dbx",
            Kind: "table",
            Columns: [new CatalogDatasetDefinitionColumn("date_key", "int")]);

        var result = await CatalogApiEndpoints.PutDatasetDefinitionHandler(
            "kostentransparenz_dbx.controlling.dim_date",
            request,
            repo,
            audit,
            CreateOptions("kostentransparenz_dbx", "Databricks"),
            CreateContext(),
            CancellationToken.None);

        var status = ((IStatusCodeHttpResult)result).StatusCode ?? StatusCodes.Status200OK;
        status.ShouldBe(StatusCodes.Status200OK);
        repo.Stored.Table.Id.ShouldBe(existingGuid);
        repo.Stored.Table.SourceType.ShouldBe("Databricks");
        repo.Stored.Table.Dialect.ShouldBe(DatabaseDialect.Databricks);
        repo.Stored.ColumnMaskingRules.ShouldContainKey("date_key");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Put_UnconfiguredDataSource_Returns400WithoutInputEchoOrSideEffects()
    {
        var repo = new FakeTableMetadataRepository();
        var audit = new NullAuditWriter();
        var request = new CatalogDatasetDefinitionRequest(
            1, "unknown_conn_secret", "table", [new CatalogDatasetDefinitionColumn("id", "int")]);

        var result = await CatalogApiEndpoints.PutDatasetDefinitionHandler(
            "unknown_conn_secret.public.orders", request, repo, audit, CreateOptions("sales", "PostgreSql"), CreateContext(), CancellationToken.None);

        var problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        problem.ProblemDetails.Extensions["code"]?.ToString().ShouldBe(ApiErrorCodes.BadRequest);
        (problem.ProblemDetails.Detail ?? string.Empty).ShouldNotContain("unknown_conn_secret");
        repo.UpsertCalls.ShouldBe(0);
        audit.WriteCalls.ShouldBe(0);
    }

    [Theory]
    [Trait("Category", "Security")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("secret_custom_provider")]
    [InlineData("99")]
    public async Task Put_UnsupportedOrEmptyProvider_Returns400WithoutEchoOrSideEffects(string invalidProvider)
    {
        var repo = new FakeTableMetadataRepository();
        var audit = new NullAuditWriter();
        var request = new CatalogDatasetDefinitionRequest(
            1, "sales", "table", [new CatalogDatasetDefinitionColumn("id", "int")]);

        var result = await CatalogApiEndpoints.PutDatasetDefinitionHandler(
            "sales.public.orders", request, repo, audit, CreateOptions("sales", invalidProvider), CreateContext(), CancellationToken.None);

        var problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        problem.ProblemDetails.Extensions["code"]?.ToString().ShouldBe(ApiErrorCodes.BadRequest);
        if (!string.IsNullOrWhiteSpace(invalidProvider))
        {
            (problem.ProblemDetails.Detail ?? string.Empty).ShouldNotContain(invalidProvider);
        }
        repo.UpsertCalls.ShouldBe(0);
        audit.WriteCalls.ShouldBe(0);
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task Put_ExistingNonSqlDataset_Returns400WithoutRerouting()
    {
        var tableId = new TableIdentifier("restsrc", "v1", "customers");
        var repo = new FakeTableMetadataRepository
        {
            Stored = new TableMetadata
            {
                Identifier = tableId,
                Table = new Table
                {
                    Id = Guid.NewGuid(),
                    SourceName = "restsrc",
                    SchemaName = "v1",
                    TableName = "customers",
                    SourceType = "rest",
                    IsActive = true,
                    DataSourceType = DataSourceType.HttpDeclarative
                },
                Columns = [new TableColumn { Id = Guid.NewGuid(), ColumnName = "id", DataType = "string" }]
            }
        };
        var audit = new NullAuditWriter();
        var request = new CatalogDatasetDefinitionRequest(
            1, "restsrc", "table", [new CatalogDatasetDefinitionColumn("id", "string")]);

        var result = await CatalogApiEndpoints.PutDatasetDefinitionHandler(
            "restsrc.v1.customers", request, repo, audit, CreateOptions("restsrc", "Databricks"), CreateContext(), CancellationToken.None);

        var problem = result.ShouldBeOfType<ProblemHttpResult>();
        problem.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        repo.UpsertCalls.ShouldBe(0);
        audit.WriteCalls.ShouldBe(0);
    }
}
