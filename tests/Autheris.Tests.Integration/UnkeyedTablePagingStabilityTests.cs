namespace Autheris.Tests.Integration;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>Issue #94 (SEC, plan 13.3): ordering and paging of an unkeyed table never leak rows the consent row filter excludes.</summary>
public sealed class UnkeyedTablePagingStabilityTests : IDisposable
{
    private const string Source = "default";
    private static readonly Sid User = new("S-1-5-21-PAGING-1");
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"paging-{Guid.NewGuid():N}.db");
    private WebApplicationFactory<Program>? _host;

    public UnkeyedTablePagingStabilityTests()
    {
        using var conn = new SqliteConnection($"Data Source={_path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "CREATE TABLE air1 (vehicle TEXT, seq INTEGER, pressure REAL); " +
            string.Join(' ', Enumerable.Range(1, 12).Select(i => $"INSERT INTO air1 VALUES ('v{i % 2}', {i}, {i}.5);"));
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task RowFilteredUnkeyedTable_PagesContainOnlyPermittedRows()
    {
        _host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.UseSetting("Gateway:Tenancy:DefaultTenantId", Autheris.Tests.Common.TestTenants.Default.Value);
            builder.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
            builder.UseSetting("Gateway:GovernanceDb:Provider", "Sqlite");
            builder.UseSetting("Gateway:GovernanceDb:ConnectionString", $"Data Source=gov-paging-{Guid.NewGuid():N};Mode=Memory;Cache=Shared");
            builder.UseSetting($"Gateway:DataSources:Connections:{Source}:Provider", "Sqlite");
            builder.UseSetting($"Gateway:DataSources:Connections:{Source}:ConnectionString", $"Data Source={_path}");
        });

        var identifier = new TableIdentifier(Source, "main", "air1");
        using (var scope = _host.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<IGovernanceRepository>();
            await repo.UpsertTableMetadataAsync(new TableMetadata
            {
                Table = new Table { Id = Guid.NewGuid(), DisplayName = "air1", TableName = "air1", SchemaName = "main", SourceType = "Sqlite", SourceName = Source },
                Identifier = identifier,
                Columns =
                [
                    new TableColumn { ColumnName = "vehicle", DataType = "varchar" },
                    new TableColumn { ColumnName = "seq", DataType = "int" },
                    new TableColumn { ColumnName = "pressure", DataType = "float" }
                ]
            });
            var meta = (await repo.GetTableMetadataAsync(identifier))!;
            var vehicle = meta.Columns.Single(c => c.ColumnName == "vehicle");
            await repo.CreateConsentAsync(new Consent
            {
                TenantId = Autheris.Tests.Common.TestTenants.Default,
                TableId = meta.Table.Id,
                TableIdentifier = identifier,
                Effect = ConsentEffect.Allow,
                GranteeType = GranteeType.User,
                GranteeSid = User,
                ValidFrom = DateTimeOffset.UtcNow.AddHours(-1),
                ValidTo = DateTimeOffset.UtcNow.AddHours(24),
                ColumnRules = meta.Columns.Select(c => new ConsentColumnRule { ColumnName = c.ColumnName, AccessLevel = ColumnAccessLevel.Clear }).ToList(),
                RowFilters =
                [
                    new ConsentRowFilter { TableColumnId = vehicle.Id, ColumnName = "vehicle", Operator = "EQ", ValueType = "string", ValueJson = "\"v0\"" }
                ]
            });
        }

        var client = _host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Test-User-Sid", User.Value);

        var seen = new List<(string Vehicle, string Seq)>();
        string? url = $"/odata/v4/{Source}/main/air1?$top=4";
        for (var guard = 0; url != null && guard < 10; guard++)
        {
            var response = await client.GetAsync(url);
            response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            seen.AddRange(doc.RootElement.GetProperty("value").EnumerateArray()
                .Select(r => (r.GetProperty("vehicle").GetString()!, r.GetProperty("seq").GetRawText())));
            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var next) ? new Uri(next.GetString()!).PathAndQuery : null;
        }

        seen.ShouldAllBe(r => r.Vehicle == "v0");
        seen.Count.ShouldBe(6);
        seen.Select(r => r.Seq).Distinct().Count().ShouldBe(6);
    }

    public void Dispose()
    {
        _host?.Dispose();
        SqliteConnection.ClearAllPools();
        try { File.Delete(_path); } catch (IOException) { }
    }
}
