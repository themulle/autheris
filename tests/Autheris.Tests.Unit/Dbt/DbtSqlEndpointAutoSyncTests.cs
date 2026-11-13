namespace Autheris.Tests.Unit.Dbt;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Lineage;
using Autheris.Application.Sql.Endpoints.Services;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Extensions.Dbt;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>Issue #680 (WP-OI-CAT-5): dbt auto-sync follows SqlEndpoints.Enabled and reports a failure once per run.</summary>
public sealed class DbtSqlEndpointAutoSyncTests : IDisposable
{
    private const string FailedCode = "SQL_ENDPOINT_AUTOSYNC_FAILED";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "dbt_autosync_" + Guid.NewGuid().ToString("N"));

    public DbtSqlEndpointAutoSyncTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    private sealed class CountingLogger : ILogger<DbtMetadataIngestionService>
    {
        public List<(LogLevel Level, Exception? Ex, string Message)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, exception, formatter(state, exception)));
    }

    private static string Manifest(params string[] names)
    {
        var nodes = new Dictionary<string, object>();
        foreach (var name in names)
        {
            nodes[$"model.proj.{name}"] = new
            {
                name, database = "warehouse", schema = "public", description = (string?)null,
                config = new { materialized = "table" }, tags = new[] { "finance" }, meta = new { },
                columns = new Dictionary<string, object> { ["id"] = new { name = "id", data_type = "varchar", description = (string?)null, tags = Array.Empty<string>(), meta = new { } } },
                depends_on = new { nodes = Array.Empty<string>() }
            };
        }

        return JsonSerializer.Serialize(new { metadata = new { dbt_version = "1.8.0" }, nodes, sources = new { } });
    }

    private static (DbtMetadataIngestionService Service, CountingLogger Log) Build(string queriesDir, bool enabled, bool autoSync, bool nullSqlEndpoints = false, bool nullOptions = false)
    {
        var sources = new SqlDataSourceOptions();
        sources.Connections["warehouse"] = new DataSourceConnectionOptions { Provider = "PostgreSql" };
        var options = new GatewayOptions
        {
            Dbt = new DbtOptions { CreateMissingTables = true },
            DataSources = sources,
            SqlEndpoints = nullSqlEndpoints ? null! : new SqlEndpointsOptions { Enabled = enabled, AutoSyncFromDbt = autoSync, Directory = queriesDir }
        };
        var repo = Substitute.For<ITableMetadataRepository>();
        repo.UpsertTableMetadataAsync(Arg.Any<TableMetadata>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<TableMetadata>()));
        var log = new CountingLogger();
        var service = new DbtMetadataIngestionService(
            proposalRepository: new InMemoryDbtProposalRepository(),
            metadataRepository: repo,
            lineageGraphStore: Substitute.For<ILineageGraphStore>(),
            epochRepository: null,
            gatewayOptions: nullOptions ? null : Microsoft.Extensions.Options.Options.Create(options),
            sqlEndpointLoader: new SqlEndpointLoader(new InMemorySqlEndpointRegistry(), NullLogger<SqlEndpointLoader>.Instance),
            relationRepository: null,
            virtualFilterAdmin: null,
            rebacStore: null,
            tagImport: null,
            syntheticRowGenerator: null,
            logger: log);
        return (service, log);
    }

    private static Task<DbtSyncResult> Sync(DbtMetadataIngestionService service, bool dryRun, params string[] names)
        => service.IngestManifestStreamAsClusterAdminAsync(new MemoryStream(Encoding.UTF8.GetBytes(Manifest(names))), dryRun);

    /// <summary>A path below a regular file cannot be created, also when running as root.</summary>
    private string UnwritableDirectory()
    {
        var file = Path.Combine(_root, "blocker");
        File.WriteAllText(file, "x");
        return Path.Combine(file, "queries");
    }

    [Fact]
    public async Task AutoSync_SqlEndpointsDisabled_WritesNoFile()
    {
        var dir = Path.Combine(_root, "queries");
        var (service, _) = Build(dir, enabled: false, autoSync: true);

        var result = await Sync(service, false, "orders", "customers");

        result.Success.ShouldBeTrue();
        Directory.Exists(dir).ShouldBeFalse();
    }

    [Fact]
    public async Task AutoSync_ReadOnlyDirectory_ReportsOneWarningPerRun()
    {
        var (service, _) = Build(UnwritableDirectory(), enabled: true, autoSync: true);

        var result = await Sync(service, false, "orders", "customers", "payments");

        var warnings = result.Warnings.Where(w => w.Contains(FailedCode, StringComparison.Ordinal)).ToList();
        warnings.Count.ShouldBe(1);
        warnings[0].ShouldContain("3 dbt model(s)");
    }

    [Fact]
    public async Task AutoSync_ReadOnlyDirectory_LogsOnce()
    {
        var (service, log) = Build(UnwritableDirectory(), enabled: true, autoSync: true);

        await Sync(service, false, "orders", "customers", "payments");

        log.Entries.Count(e => e.Level >= LogLevel.Warning && e.Message.Contains(FailedCode, StringComparison.Ordinal)).ShouldBe(1);
        log.Entries.Where(e => e.Level >= LogLevel.Warning).ShouldAllBe(e => e.Ex == null);
    }

    [Fact]
    public async Task AutoSync_Enabled_WritesFilePerModel()
    {
        var dir = Path.Combine(_root, "queries");
        var (service, _) = Build(dir, enabled: true, autoSync: true);

        var result = await Sync(service, false, "orders", "customers");

        result.Warnings.ShouldNotContain(w => w.Contains(FailedCode, StringComparison.Ordinal));
        File.Exists(Path.Combine(dir, "orders.sql")).ShouldBeTrue();
        File.Exists(Path.Combine(dir, "customers.sql")).ShouldBeTrue();
    }

    [Fact]
    public async Task AutoSync_DryRun_WritesNothing()
    {
        var dir = Path.Combine(_root, "queries");
        var (service, _) = Build(dir, enabled: true, autoSync: true);

        await Sync(service, true, "orders");

        Directory.Exists(dir).ShouldBeFalse();
    }

    [Fact]
    public async Task AutoSync_InvalidIdentifiers_AggregatesOneRejectionWarning()
    {
        var dir = Path.Combine(_root, "queries");
        var (service, _) = Build(dir, enabled: true, autoSync: true);

        var result = await Sync(service, false, "bad-one", "bad two", "orders");

        result.Warnings.Count(w => w.Contains("rejected for 2 dbt model(s)", StringComparison.Ordinal)).ShouldBe(1);
        result.Warnings.ShouldNotContain(w => w.Contains(FailedCode, StringComparison.Ordinal));
        File.Exists(Path.Combine(dir, "orders.sql")).ShouldBeTrue();
    }

    [Theory]
    [InlineData(true, false, false, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, true, false, true)]
    public async Task AutoSync_GatingCombinations_WriteNoFile(bool enabled, bool autoSync, bool nullSqlEndpoints, bool nullOptions)
    {
        var dir = Path.Combine(_root, "queries");
        var (service, _) = Build(dir, enabled, autoSync, nullSqlEndpoints, nullOptions);

        await Sync(service, false, "orders");

        Directory.Exists(dir).ShouldBeFalse();
    }
}
