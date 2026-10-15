namespace Autheris.Tests.Integration;

using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>Issue #94: OData reads of a table without a primary key and without an <c>id</c> column must work (no phantom <c>id</c> key).</summary>
public abstract class UnkeyedTableODataReadBase : IDisposable
{
    private const string Source = "default";
    private static readonly Sid User = new("S-1-5-21-UNKEYED-1");
    private WebApplicationFactory<Program>? _host;

    protected abstract string Provider { get; }
    protected abstract string SourceType { get; }
    protected abstract string ConnectionString { get; }
    protected abstract string Schema { get; }

    /// <summary>False when the database is unavailable on this machine (the test then returns early).</summary>
    protected abstract bool IsAvailable { get; }

    private async Task<HttpClient> ClientAsync()
    {
        _host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-unkeyed-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.UseSetting($"Gateway:DataSources:Connections:{Source}:Provider", Provider);
            builder.UseSetting($"Gateway:DataSources:Connections:{Source}:ConnectionString", ConnectionString);
        });

        var identifier = new TableIdentifier(Source, Schema, "air1");
        using (var scope = _host.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            await repo.UpsertTableMetadataAsync(new TableMetadata
            {
                Table = new Table { Id = Guid.NewGuid(), DisplayName = "air1", TableName = "air1", SchemaName = Schema, SourceType = SourceType, SourceName = Source },
                Identifier = identifier,
                Columns =
                [
                    new TableColumn { ColumnName = "vehicle", DataType = "varchar" },
                    new TableColumn { ColumnName = "seq", DataType = "int" },
                    new TableColumn { ColumnName = "pressure", DataType = "float" }
                ]
            });
            var meta = (await repo.GetTableMetadataAsync(identifier))!;
            meta.PrimaryKeyColumns.ShouldBeEmpty();
            await repo.CreateConsentAsync(new Consent
            {
                TableId = meta.Table.Id,
                TableIdentifier = identifier,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = User,
                ValidFrom = DateTimeOffset.UtcNow.AddHours(-1),
                ValidTo = DateTimeOffset.UtcNow.AddHours(24),
                ColumnRules = meta.Columns.Select(c => new ConsentColumnRule { ColumnName = c.ColumnName, AccessLevel = ColumnAccessLevel.Clear }).ToList()
            });
        }

        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", User.Value);
        return client;
    }

    private static async Task<string[]> SeqsAsync(HttpResponseMessage response)
    {
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("value").EnumerateArray().Select(r => r.GetProperty("seq").GetRawText()).ToArray();
    }

    [Fact]
    public async Task Top_WithoutKeyAndWithoutIdColumn_Returns200()
    {
        if (!IsAvailable) { return; }
        var client = await ClientAsync();

        var seqs = await SeqsAsync(await client.GetAsync($"/odata/v4/{Source}/{Schema}/air1?$top=5"));

        seqs.Length.ShouldBe(5);
    }

    [Fact]
    public async Task TopAndSkip_WithoutKey_ReturnStableDisjointPages()
    {
        if (!IsAvailable) { return; }
        var client = await ClientAsync();

        var page1 = await SeqsAsync(await client.GetAsync($"/odata/v4/{Source}/{Schema}/air1?$top=5&$skip=0"));
        var page2 = await SeqsAsync(await client.GetAsync($"/odata/v4/{Source}/{Schema}/air1?$top=5&$skip=5"));
        var page2Again = await SeqsAsync(await client.GetAsync($"/odata/v4/{Source}/{Schema}/air1?$top=5&$skip=5"));

        page2.Length.ShouldBe(5);
        page2.ShouldBe(page2Again);
        page1.Intersect(page2).ShouldBeEmpty();
    }

    [Fact]
    public async Task FollowingTheNextLink_WithoutKey_VisitsEveryRowExactlyOnce()
    {
        if (!IsAvailable) { return; }
        var client = await ClientAsync();

        var seen = new System.Collections.Generic.List<string>();
        string? url = $"/odata/v4/{Source}/{Schema}/air1?$top=5";
        for (var guard = 0; url != null && guard < 10; guard++)
        {
            var response = await client.GetAsync(url);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            seen.AddRange(doc.RootElement.GetProperty("value").EnumerateArray().Select(r => r.GetProperty("seq").GetRawText()));
            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next)
                ? new Uri(next.GetString()!).PathAndQuery
                : null;
        }

        seen.Count.ShouldBe(12);
        seen.Distinct().Count().ShouldBe(12);
    }

    [Fact]
    public async Task Metadata_ForUnkeyedTable_IsServed()
    {
        if (!IsAvailable) { return; }
        var client = await ClientAsync();

        var response = await client.GetAsync($"/odata/v4/$metadata");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        System.Xml.Linq.XDocument.Parse(await response.Content.ReadAsStringAsync());
    }

    protected static string InsertRows() =>
        string.Join(' ', Enumerable.Range(1, 12).Select(i => $"INSERT INTO air1 (vehicle, seq, pressure) VALUES ('v{(i % 2)}', {i}, {i}.5);"));

    public virtual void Dispose() => _host?.Dispose();
}

public sealed class UnkeyedTableODataReadSqliteTests : UnkeyedTableODataReadBase
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"unkeyed-{Guid.NewGuid():N}.db");

    public UnkeyedTableODataReadSqliteTests()
    {
        using var conn = new SqliteConnection($"Data Source={_path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE air1 (vehicle TEXT, seq INTEGER, pressure REAL); " + InsertRows();
        cmd.ExecuteNonQuery();
    }

    protected override string Provider => "Sqlite";
    protected override string SourceType => "Sqlite";
    protected override string ConnectionString => $"Data Source={_path}";
    protected override string Schema => "main";
    protected override bool IsAvailable => true;

    public override void Dispose()
    {
        base.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }
}

public sealed class UnkeyedTableODataReadSqlServerTests : UnkeyedTableODataReadBase, IAsyncLifetime
{
    private SqlServerTestDatabase? _db;

    protected override string Provider => "SqlServer";
    protected override string SourceType => "SqlServer";
    protected override string ConnectionString => _db!.ConnectionString;
    protected override string Schema => "dbo";
    protected override bool IsAvailable => _db is { IsAvailable: true };

    public async Task InitializeAsync()
    {
        _db = await SqlServerTestDatabase.CreateAsync();
        if (!_db.IsAvailable)
        {
            return;
        }

        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE dbo.air1 (vehicle nvarchar(20) NULL, seq int NOT NULL, pressure float NULL); " + InsertRows().Replace("INTO air1", "INTO dbo.air1");
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DisposeAsync()
    {
        Dispose();
        if (_db is not null)
        {
            await _db.DisposeAsync();
        }
    }
}
