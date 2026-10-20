namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog.Search;
using Autheris.Application.Connectors;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.GraphQL.Catalog;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Hardware;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using StackExchange.Redis;
using Xunit;

[Trait("Category", "Security")]
public sealed class Perf254SecurityTests
{
    private static readonly Regex Sha256HexRegex = new("^[a-f0-9]{64}$", RegexOptions.Compiled);

    [Fact]
    public void SEC_PERF254_01_DelimiterInjectionDefense_GuaranteesDistinctHashes()
    {
        // Delimiter collisions: "a" + ":" + "b_c" vs "a_b" + ":" + "c"
        var set1 = new[] { new TableIdentifier("corp", "sales", "order_items") };
        var set2 = new[] { new TableIdentifier("corp", "sales_order", "items") };

        var hash1 = CatalogVisibleBitSet.ComputeSha256(set1);
        var hash2 = CatalogVisibleBitSet.ComputeSha256(set2);

        hash1.ShouldNotBe(hash2);
        hash1.Length.ShouldBe(64);
        hash2.Length.ShouldBe(64);
        Sha256HexRegex.IsMatch(hash1).ShouldBeTrue();
        Sha256HexRegex.IsMatch(hash2).ShouldBeTrue();
    }

    [Fact]
    public void SEC_PERF254_02_ContainerMemoryLimits_AreStrictlyClamped()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "cgroup_sec_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var v2Path = Path.Combine(tempDir, "memory.max");
            var v1Path = Path.Combine(tempDir, "memory.limit_in_bytes");

            // Extremely small: 10 MB -> clamped to 64 MB
            File.WriteAllText(v2Path, "10485760\n");
            var minLimit = ContainerMemoryDetector.ResolveGarnetMemoryLimitMb(null, v2Path, v1Path);
            minLimit.ShouldBe(64);

            // Extremely large: 64 GB -> clamped to 4096 MB
            File.WriteAllText(v2Path, "68719476736\n");
            var maxLimit = ContainerMemoryDetector.ResolveGarnetMemoryLimitMb(null, v2Path, v1Path);
            maxLimit.ShouldBe(4096);
        }
        finally
        {
            Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task SEC_PERF254_03_StreamingMemoryBounding_AbortsOnByteLimit()
    {
        var metadata = new TableMetadata
        {
            Identifier = new TableIdentifier("corp", "hr", "employees"),
            Table = new Table { SourceName = "s", SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "Id", DataType = "int" }, new TableColumn { ColumnName = "Data", DataType = "varchar" }]
        };

        var rows = new List<IReadOnlyDictionary<string, object?>>(50);
        for (int i = 0; i < 50; i++)
        {
            rows.Add(new Dictionary<string, object?> { ["Id"] = i, ["Data"] = new string('x', 500) });
        }

        var recordSource = Substitute.For<IConnectorRecordSource>();
        recordSource.ReadBatchAsync(Arg.Any<ConnectorSplit>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(rows));

        var splitManager = Substitute.For<IConnectorSplitManager>();
        splitManager.GetSplitsAsync(Arg.Any<TableMetadata>(), Arg.Any<ConnectorSessionContext>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ConnectorSplit>>([ConnectorSplit.Default()]));

        var connector = Substitute.For<IAutherisConnector>();
        connector.RecordSource.Returns(recordSource);
        connector.SplitManager.Returns(splitManager);

        var decision = TableAccessDecision.Allowed(metadata.Identifier, new Dictionary<string, ColumnAccessLevel>(), hasUnconstrainedColumnAllow: true);
        var session = new ConnectorSessionContext(new System.Security.Claims.ClaimsPrincipal(), new TenantId("t1"), decision, ["Id", "Data"], new Dictionary<string, object?>());

        // MaxBytes = 1000 bytes (each row has >500 bytes of data)
        var policy = new GovernedRowPolicy(Substitute.For<IColumnMaskingProvider>(), null, MaxBytes: 1000);

        var ex = await Should.ThrowAsync<GatewaySecurityException>(async () =>
        {
            await foreach (var row in GovernedConnectorReader.StreamAsync(connector, session, metadata, decision, policy))
            {
            }
        });

        ex.ErrorCode.ShouldBe("RESPONSE_TOO_LARGE");
    }

    [Fact]
    public void SEC_PERF254_04_HmacTenantIsolation_NeverSharesMaskedValuesAcrossTenants()
    {
        var maskingProvider = new ColumnMaskingProvider();
        var baseRule = new MaskingRule { RuleType = "HMAC" };

        var tenant1 = new TenantId("tenant-1");
        var tenant2 = new TenantId("tenant-2");

        var rule1 = MaskingRule.CreateTenantScopedHmacRule(baseRule, tenant1, "key1");
        var rule2 = MaskingRule.CreateTenantScopedHmacRule(baseRule, tenant2, "key1");

        var val1 = maskingProvider.MaskValue("ssn", "sensitive-info", rule1)?.ToString();
        var val2 = maskingProvider.MaskValue("ssn", "sensitive-info", rule2)?.ToString();

        val1.ShouldNotBeNull();
        val2.ShouldNotBeNull();
        val1.ShouldNotBe(val2);
        val1.ShouldNotContain("sensitive-info");
        val2.ShouldNotContain("sensitive-info");
    }

    [Fact]
    public async Task SEC_PERF254_05_SchemaPartitioning_EnforcesPartitionFilter()
    {
        var metaRepo = Substitute.For<ITableMetadataRepository>();
        var relRepo = Substitute.For<ITableRelationRepository>();

        var tableA = new TableMetadata
        {
            Identifier = new TableIdentifier("sales", "dbo", "orders"),
            Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };
        var tableB = new TableMetadata
        {
            Identifier = new TableIdentifier("hr", "dbo", "payroll"),
            Table = new Table { IsActive = true, SourceType = "PostgreSql", DataSourceType = DataSourceType.Sql },
            Columns = [new TableColumn { ColumnName = "id", DataType = "int" }]
        };

        metaRepo.GetAllTablesAsync(Arg.Any<CancellationToken>()).Returns([tableA, tableB]);
        relRepo.GetRelationsForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns([]);

        // Partition filter: only sales
        var schema = await CatalogSchemaModel.BuildFilteredAsync(metaRepo, relRepo, t => t.Identifier.Domain == "sales");

        schema.Tables.Count.ShouldBe(1);
        schema.Tables[0].Identifier.Domain.ShouldBe("sales");
    }

    private sealed class SecurityTestLogger : ILogger<EpochValidationService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    [Fact]
    public async Task SEC_PERF254_06_RollbackDiscrimination_EmptyBootstrapLogsDebug_RegressionLogsWarningRateLimited()
    {
        var logger = new SecurityTestLogger();

        var db = Substitute.For<IDatabase>();
        var mux = Substitute.For<IConnectionMultiplexer>();
        mux.IsConnected.Returns(true);
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        var sensitivity = Substitute.For<ITableSensitivityLookup>();
        sensitivity.IsSensitiveAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>()).Returns(new ValueTask<bool>(false));

        var time = new FakeTimeProvider();
        var service = new EpochValidationService(Options.Create(new GatewayOptions()), Substitute.For<IEventBus>(), sensitivity, mux, logger, time);

        var table = new TableIdentifier("corp", "hr", "payroll");

        // Case 1: Cold bootstrap (Redis returns null, highestSeen = 0)
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult(RedisValue.Null));
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)1L)));

        var ep1 = await service.GetCurrentEpochAsync(table);
        ep1.ShouldBe(1L);

        // Cold bootstrap logged at Debug, never at Warning
        logger.Entries.Any(l => l.Level == LogLevel.Debug && l.Message.Contains("Empty policy epoch cache bootstrap")).ShouldBeTrue();
        logger.Entries.Any(l => l.Level == LogLevel.Warning).ShouldBeFalse();

        // Case 2: Advance to 5, then regression to 2
        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult((RedisValue)"5"));
        await service.GetCurrentEpochAsync(table);

        db.StringGetAsync(Arg.Any<RedisKey>(), Arg.Any<CommandFlags>()).Returns(Task.FromResult((RedisValue)"2"));
        db.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>(), Arg.Any<CommandFlags>())
            .Returns(Task.FromResult(RedisResult.Create((RedisValue)6L)));

        await service.GetCurrentEpochAsync(table);

        // First rollback logged at Warning
        logger.Entries.Count(l => l.Level == LogLevel.Warning && l.Message.Contains("Policy epoch rollback detected")).ShouldBe(1);

        // Advance 10 seconds and regress again
        time.Advance(TimeSpan.FromSeconds(10));
        await service.GetCurrentEpochAsync(table);

        // Still 1 Warning (rate-limited)
        logger.Entries.Count(l => l.Level == LogLevel.Warning && l.Message.Contains("Policy epoch rollback detected")).ShouldBe(1);
    }
}
