namespace Autheris.Tests.Unit.Catalog;

using System.Globalization;
using System.Security.Claims;
using System.Text;
using Autheris.Api.Endpoints;
using Autheris.Api.Security;
using Autheris.Application.Audit;
using Autheris.Application.Interfaces;
using Autheris.Domain.Audit;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #94 (SD-5, SEC-94-05, -06, -15): PUT /api/v1/catalog/datasets/{id}/keys.</summary>
public sealed class CatalogKeysEndpointTests : IDisposable
{
    private static readonly TableIdentifier Orders = new("sales", "dbo", "orders");
    private readonly SqliteGovernanceRepository _repo;
    private readonly AuditWriter _audit;

    public CatalogKeysEndpointTests()
    {
        var options = Options.Create(new GatewayOptions
        {
            GovernanceDb = new GovernanceDbOptions { ConnectionString = $"Data Source=keysep_{Guid.NewGuid():N};Mode=Memory;Cache=Shared", JournalMode = "DELETE" }
        });
        _repo = Autheris.Tests.Unit.GovernanceStoreFactory.CreateSqlite(Substitute.For<IEpochValidationService>(), options);
        _audit = new AuditWriter(_repo, NullLogger<AuditWriter>.Instance);
    }

    public void Dispose() => _repo.Dispose();

    private static TableMetadata Meta(TableIdentifier id, IReadOnlyDictionary<string, MaskingRule>? masking = null, bool active = true) => new()
    {
        Identifier = id,
        Table = new Table { SourceType = "sql", SourceName = id.Domain, SchemaName = id.Schema, TableName = id.TableName, DisplayName = id.TableName, IsActive = active },
        Columns = [new TableColumn { ColumnName = "a", DataType = "integer" }, new TableColumn { ColumnName = "b", DataType = "integer" }, new TableColumn { ColumnName = "ssn", DataType = "varchar" }],
        ColumnMaskingRules = masking ?? new Dictionary<string, MaskingRule>(),
        ReplaceKeyDefinition = true
    };

    private static HttpContext Context()
    {
        var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var ctx = new DefaultHttpContext { RequestServices = services };
        ctx.Response.Body = new MemoryStream();
        ctx.TraceIdentifier = "trace-1";
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "S-1-5-21-ADMIN"), new Claim("tenant_id", "tenant-a"), new Claim(ClaimTypes.Role, "GovernanceAdmin")], "TestAuth"));
        return ctx;
    }

    private static async Task<(int Status, string Body)> Run(IResult result)
    {
        var ctx = Context();
        await result.ExecuteAsync(ctx);
        ctx.Response.Body.Position = 0;
        return (ctx.Response.StatusCode, Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray()));
    }

    private Task<IResult> Put(string id, SetDatasetKeysDto? dto, ITableMetadataRepository? repo = null) =>
        CatalogApiEndpoints.SetDatasetKeysHandler(id, dto, repo ?? _repo, _audit, Context(), CancellationToken.None);

    private object? Scalar(string sql)
    {
        using var cmd = _repo.RawConnection().CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    [Fact]
    public async Task Success_ReplacesDefinition_And_ReturnsIt()
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders));

        var (status, body) = await Run(await Put("sales.dbo.orders", new SetDatasetKeysDto(PrimaryKey: ["a", "b"],
            Indexes: [new DatasetIndexDto("ux_b", ["b"], true)])));

        status.ShouldBe(200);
        body.ShouldContain("\"a\"");
        body.ShouldContain("ux_b");
        _repo.InvalidateMetadataCacheForTesting();
        (await _repo.GetTableMetadataAsync(Orders))!.PrimaryKeyColumns.ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task Success_ByGuid_DoesNotLoadAllTables()
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders));
        var id = Guid.Parse((string)Scalar("SELECT id FROM TABLES WHERE table_name = 'orders'")!);
        var spy = Substitute.For<ITableMetadataRepository>();
        spy.GetActiveTableIdentifierByIdAsync(id, Arg.Any<CancellationToken>()).Returns(Orders);
        spy.GetActiveTableIdentifierAsync(Orders, Arg.Any<CancellationToken>()).Returns(Orders);
        spy.ReplaceTableKeyDefinitionAsync(Orders, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<TableIndexDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(new TableKeyDefinitionChange(new([], []), new(["a"], [])));

        var (status, _) = await Run(await Put(id.ToString("D"), new SetDatasetKeysDto(PrimaryKey: ["a"]), spy));

        status.ShouldBe(200);
        await spy.DidNotReceive().GetAllTablesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Success_UsesNarrowWrite_NeverFullUpsert()
    {
        var spy = Substitute.For<ITableMetadataRepository>();
        spy.GetActiveTableIdentifierAsync(Orders, Arg.Any<CancellationToken>()).Returns(Orders);
        spy.ReplaceTableKeyDefinitionAsync(Orders, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<TableIndexDefinition>>(), Arg.Any<CancellationToken>())
            .Returns(new TableKeyDefinitionChange(new([], []), new(["a"], [])));

        await Put("sales.dbo.orders", new SetDatasetKeysDto(PrimaryKey: ["a"]), spy);

        await spy.DidNotReceive().UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>());
        await spy.Received(1).ReplaceTableKeyDefinitionAsync(Orders, Arg.Any<IReadOnlyList<string>>(), Arg.Any<IReadOnlyList<TableIndexDefinition>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ConcurrentMaskingChange_IsPreserved()
    {
        var masking = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase) { ["ssn"] = new MaskingRule { RuleType = "FULL", Replacement = "***" } };
        await _repo.UpsertTableMetadataAsync(Meta(Orders));
        // A masking change committed by someone else after the endpoint resolved the dataset must survive the key write.
        var interleaving = new InterleavingRepository(_repo, async () => await _repo.UpsertTableMetadataAsync(Meta(Orders, masking)));

        var (status, _) = await Run(await Put("sales.dbo.orders", new SetDatasetKeysDto(PrimaryKey: ["a"]), interleaving));

        status.ShouldBe(200);
        Scalar("SELECT COUNT(*) FROM COLUMN_MASKING_RULES m JOIN TABLE_COLUMNS c ON c.id = m.table_column_id WHERE c.column_name = 'ssn'").ShouldBe(1L);
    }

    [Fact]
    public async Task Success_WritesHmacChainedAuditEntry_WithBeforeAfter()
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders) with { PrimaryKeyColumns = ["a"] });

        await Put("sales.dbo.orders", new SetDatasetKeysDto(PrimaryKey: ["b"], Indexes: [new DatasetIndexDto("ix_a", ["a"], false)]));

        using var cmd = _repo.RawConnection().CreateCommand();
        cmd.CommandText = "SELECT actor_sid, tenant_id, target_table, decision, details_json, prev_hash, entry_hash, seq FROM AUDIT_LOG_ENTRIES WHERE event_type = @t";
        cmd.Parameters.AddWithValue("@t", AuditEventTypes.CatalogKeyDefinitionChanged);
        using var reader = cmd.ExecuteReader();
        reader.Read().ShouldBeTrue();
        reader.GetString(0).ShouldBe("S-1-5-21-ADMIN");
        reader.GetString(1).ShouldBe("tenant-a");
        reader.GetString(2).ShouldBe("sales.dbo.orders");
        reader.GetString(3).ShouldBe("ALLOW");
        var details = reader.GetString(4);
        details.ShouldContain("\"before\"");
        details.ShouldContain("\"after\"");
        details.ShouldContain("ix_a");
        reader.GetString(5).ShouldNotBeNullOrEmpty();
        reader.GetString(6).ShouldNotBeNullOrEmpty();
        reader.GetInt64(7).ShouldBeGreaterThan(0);
        reader.Read().ShouldBeFalse("exactly one entry per change");
    }

    [Fact]
    public async Task AuditUnavailable_FailsClosed()
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders));
        var failing = Substitute.For<IAuditWriter>();
        failing.RecordRequiredAsync(Arg.Any<AuditLogEntry>(), Arg.Any<CancellationToken>()).Returns<Task>(_ => throw new AuditUnavailableException("down"));

        await Should.ThrowAsync<AuditUnavailableException>(() =>
            CatalogApiEndpoints.SetDatasetKeysHandler("sales.dbo.orders", new SetDatasetKeysDto(PrimaryKey: ["a"]), _repo, failing, Context(), CancellationToken.None));
    }

    [Fact]
    public async Task UnknownOrInactiveDataset_404_SameBody()
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders) with { PrimaryKeyColumns = ["a", "b"] });
        await _repo.UpsertTableMetadataAsync(Meta(new TableIdentifier("sales", "dbo", "inactive"), active: false));
        var inactiveId = (string)Scalar("SELECT id FROM TABLES WHERE table_name = 'inactive'")!;
        var dto = new SetDatasetKeysDto(PrimaryKey: ["a"]);

        var bodies = new List<(int, string)>();
        foreach (var id in new[] { "sales.dbo.nothere", Guid.NewGuid().ToString("D"), "sales.dbo.inactive", inactiveId, "not an id", "{" + Guid.NewGuid() + "}", new string('x', 5000) })
        {
            bodies.Add(await Run(await Put(id, dto)));
        }

        bodies.Select(b => b.Item1).ShouldAllBe(s => s == 404);
        bodies.Select(b => b.Item2).Distinct().Count().ShouldBe(1);
        bodies[0].Item2.ShouldNotContain("nothere");
        bodies[0].Item2.ShouldNotContain("inactive");
    }

    [Fact]
    public async Task CorruptStoredDefinition_CanBeRepaired_AndAuditRecordsBeforeAsUnavailable()
    {
        var broken = new TableIdentifier("sales", "dbo", "broken");
        await _repo.UpsertTableMetadataAsync(Meta(broken) with { PrimaryKeyColumns = ["a", "b"] });
        Exec("UPDATE TABLE_PRIMARY_KEY_COLUMNS SET key_ordinal = 7 WHERE key_ordinal = 2 AND table_id = (SELECT id FROM TABLES WHERE table_name = 'broken')");
        _repo.InvalidateMetadataCacheForTesting();
        await Should.ThrowAsync<TableMetadataUnavailableException>(() => _repo.GetTableMetadataAsync(broken));
        var guid = (string)Scalar("SELECT id FROM TABLES WHERE table_name = 'broken'")!;

        foreach (var id in new[] { "sales.dbo.broken", guid })
        {
            var (status, body) = await Run(await Put(id, new SetDatasetKeysDto(PrimaryKey: ["a"])));
            status.ShouldBe(200);
            body.ShouldContain("\"a\"");
        }

        _repo.InvalidateMetadataCacheForTesting();
        (await _repo.GetTableMetadataAsync(broken))!.PrimaryKeyColumns.ShouldBe(["a"]);
        Scalar("SELECT details_json FROM AUDIT_LOG_ENTRIES WHERE event_type = '" + AuditEventTypes.CatalogKeyDefinitionChanged + "' ORDER BY seq LIMIT 1")!
            .ToString()!.ShouldContain("\"unavailable\":true");
    }

    [Fact]
    public async Task CorruptStoredDefinition_InvalidNewDefinition_StillRejected()
    {
        var broken = new TableIdentifier("sales", "dbo", "broken");
        await _repo.UpsertTableMetadataAsync(Meta(broken) with { PrimaryKeyColumns = ["a", "b"] });
        Exec("UPDATE TABLE_PRIMARY_KEY_COLUMNS SET key_ordinal = 7 WHERE key_ordinal = 2 AND table_id = (SELECT id FROM TABLES WHERE table_name = 'broken')");

        var (status, body) = await Run(await Put("sales.dbo.broken", new SetDatasetKeysDto(PrimaryKey: ["nope"])));

        status.ShouldBe(400);
        body.ShouldBe("{\"error\":\"Invalid table key or index definition.\"}");
    }

    [Fact]
    public async Task IndexUniqueFlag_CanonicalUnique_IsUniqueAlias_DisagreementIs400()
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders));

        foreach (var index in new[] { new DatasetIndexDto("ux1", ["a"], Unique: true), new DatasetIndexDto("ux1", ["a"], IsUnique: true), new DatasetIndexDto("ux1", ["a"], true, true) })
        {
            (await Run(await Put("sales.dbo.orders", new SetDatasetKeysDto(Indexes: [index])))).Status.ShouldBe(200);
            _repo.InvalidateMetadataCacheForTesting();
            (await _repo.GetTableMetadataAsync(Orders))!.Indexes.Single().IsUnique.ShouldBeTrue();
        }

        var (status, body) = await Run(await Put("sales.dbo.orders", new SetDatasetKeysDto(Indexes: [new DatasetIndexDto("ux1", ["a"], IsUnique: true, Unique: false)])));
        status.ShouldBe(400);
        body.ShouldBe("{\"error\":\"Invalid table key or index definition.\"}");
    }

    [Fact]
    public async Task Invalid_400_RuleOnlyNoStackTrace()
    {
        await _repo.UpsertTableMetadataAsync(Meta(Orders));

        foreach (var dto in new[]
        {
            new SetDatasetKeysDto(PrimaryKey: ["no_such_column_xyz"]),
            new SetDatasetKeysDto(PrimaryKey: ["a", "a"]),
            new SetDatasetKeysDto(Indexes: [new DatasetIndexDto("<b>bad</b>", ["a"])]),
            new SetDatasetKeysDto(Indexes: [new DatasetIndexDto("ix", [])]),
            new SetDatasetKeysDto(PrimaryKey: Enumerable.Range(0, 40).Select(i => $"c{i}").ToList())
        })
        {
            var (status, body) = await Put("sales.dbo.orders", dto).ContinueWith(t => Run(t.Result)).Unwrap();
            status.ShouldBe(400);
            body.ShouldBe("{\"error\":\"Invalid table key or index definition.\"}");
        }
    }

    [Fact]
    public async Task NullPayload_400()
    {
        (await Run(await Put("sales.dbo.orders", null))).Status.ShouldBe(400);
    }

    [Theory]
    [InlineData("/api/v1/catalog/datasets/{id}/keys")]
    [InlineData("/api/catalog/datasets/{id}/keys")]
    public void Route_RequiresGovernanceAdmin_SoAnonymous401_AndNonAdmin403(string pattern)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddAuthorization();
        // Services of the other catalog handlers must be known so that the parameter inference does not mistake them for a body.
        builder.Services.AddSingleton(Substitute.For<Autheris.Application.Catalog.Interfaces.ICatalogDiscoveryService>());
        builder.Services.AddSingleton(Substitute.For<Autheris.Application.Catalog.Interfaces.IPrincipalResolverService>());
        builder.Services.AddSingleton(Substitute.For<Autheris.Application.Catalog.Interfaces.IDatasourceTestingService>());
        builder.Services.AddSingleton(Substitute.For<Autheris.Application.Governance.Interfaces.IAccessPlanningService>());
        builder.Services.AddSingleton(Substitute.For<ITableMetadataRepository>());
        builder.Services.AddSingleton(Substitute.For<IAuditWriter>());
        var app = builder.Build();
        app.MapCatalogEndpoints();

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(d => d.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(e => e.RoutePattern.RawText == pattern && e.Metadata.GetMetadata<HttpMethodMetadata>()!.HttpMethods.Contains("PUT"));

        endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().ShouldContain(a => a.Policy == GatewayPolicies.GovernanceAdmin);
        endpoint.Metadata.GetMetadata<IAllowAnonymous>().ShouldBeNull();
    }

    private void Exec(string sql)
    {
        using var cmd = _repo.RawConnection().CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Runs an action (a concurrent masking commit) right after the endpoint resolved the dataset.</summary>
    private sealed class InterleavingRepository(ITableMetadataRepository inner, Func<Task> afterResolve) : ITableMetadataRepository
    {
        public Task<TableMetadata?> GetTableMetadataAsync(TableIdentifier table, CancellationToken ct = default) => inner.GetTableMetadataAsync(table, ct);

        public async Task<TableIdentifier?> GetActiveTableIdentifierAsync(TableIdentifier table, CancellationToken ct = default)
        {
            var result = await inner.GetActiveTableIdentifierAsync(table, ct);
            await afterResolve();
            return result;
        }

        public Task<IReadOnlyList<TableMetadata>> GetAllTablesAsync(CancellationToken ct = default) => inner.GetAllTablesAsync(ct);
        public Task<TableMetadata> UpsertTableMetadataAsync(TableMetadata metadata, CancellationToken ct = default) => inner.UpsertTableMetadataAsync(metadata, ct);
        public Task<TableKeyDefinitionChange?> ReplaceTableKeyDefinitionAsync(TableIdentifier table, IReadOnlyList<string> primaryKey, IReadOnlyList<TableIndexDefinition> indexes, CancellationToken ct = default) =>
            inner.ReplaceTableKeyDefinitionAsync(table, primaryKey, indexes, ct);
    }
}
