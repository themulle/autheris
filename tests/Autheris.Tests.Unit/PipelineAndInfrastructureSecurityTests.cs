using System.Security.Claims;
using System.Text.Json;
using Autheris.Application.Interfaces;
using HotChocolate;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Infrastructure.Messaging;
using Autheris.Application.Services;
using HotChocolate.Language;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class PipelineAndInfrastructureSecurityTests
{
    [Fact]
    public async Task EventBus_WhenChannelFull_DropsOldestAndDoesNotBlockPublisher()
    {
        // Finding F: InProcessChannelEventBus must use DropOldest to prevent publisher blocking DoS
        var bus = new InProcessChannelEventBus(capacity: 2);

        // Slow subscriber to hold consumption
        var tcs = new TaskCompletionSource();
        using var sub = bus.Subscribe<object>("test_chan", async _ =>
        {
            await tcs.Task;
        });

        // 1st item picked up by consumer
        await bus.PublishAsync("test_chan", new { Msg = 1 });
        await Task.Delay(50);

        // Fill capacity and overflow: should NOT block the publisher!
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await bus.PublishAsync("test_chan", new { Msg = 2 }, cts.Token);
        await bus.PublishAsync("test_chan", new { Msg = 3 }, cts.Token);
        // 4th and 5th writes must complete immediately without blocking
        await bus.PublishAsync("test_chan", new { Msg = 4 }, cts.Token);
        await bus.PublishAsync("test_chan", new { Msg = 5 }, cts.Token);

        tcs.SetResult();
        await bus.DisposeAsync();
    }

    [Fact]
    public async Task EventBus_SlowSubscriber_DoesNotHangConsumerLoop()
    {
        // Finding F: Slow or hanging subscribers must not freeze the event bus consumer loop
        var bus = new InProcessChannelEventBus(capacity: 10);

        // 1. Slow subscriber that delays 10 seconds
        using var slowSub = bus.Subscribe<string>("test_event", async _ =>
        {
            await Task.Delay(10_000);
        });

        // 2. Fast subscriber that immediately signals completion
        var fastReceivedTcs = new TaskCompletionSource<bool>();
        using var fastSub = bus.Subscribe<string>("test_event", _ =>
        {
            fastReceivedTcs.TrySetResult(true);
            return Task.CompletedTask;
        });

        // Publish event
        await bus.PublishAsync("test_event", "hello_event");

        // The fast subscriber must receive the event quickly, even though slowSub delays 10s
        var completed = await Task.WhenAny(fastReceivedTcs.Task, Task.Delay(3000));
        completed.ShouldBe(fastReceivedTcs.Task, "Fast subscriber should have received event without waiting 10s for slow subscriber");

        await bus.DisposeAsync();
    }

    [Fact]
    public async Task GatewayExecution_RowFilter_SupportsComparisonOperators()
    {
        // Finding G: Row filtering must properly evaluate comparison operators without failing closed on non-id columns
        var repository = Substitute.For<IGovernanceRepository>();
        var cacheService = Substitute.For<IConsentCacheService>();
        var resolutionService = Substitute.For<IConsentResolutionService>();
        var maskingProvider = Substitute.For<IColumnMaskingProvider>();

        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var metadata = new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), SourceName = "finance", SchemaName = "dbo", TableName = "finance_table_1", DisplayName = "Finance 1" },
            Identifier = tableId,
            Columns = new List<TableColumn>
            {
                new() { Id = Guid.NewGuid(), ColumnName = "id", DataType = "int" },
                new() { Id = Guid.NewGuid(), ColumnName = "name", DataType = "varchar" },
                new() { Id = Guid.NewGuid(), ColumnName = "amount", DataType = "decimal" }
            }
        };
        repository.GetTableMetadataAsync(tableId, Arg.Any<CancellationToken>()).Returns(metadata);

        // Filter: amount > 500
        var decision = new TableAccessDecision(
            Table: tableId,
            IsAllowed: true,
            DeniedReasons: Array.Empty<string>(),
            ColumnAccess: new Dictionary<string, ColumnAccessLevel>
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["name"] = ColumnAccessLevel.Clear,
                ["amount"] = ColumnAccessLevel.Clear
            },
            CombinedRowFilterSql: "amount > 500"
        );

        resolutionService.ResolveAccess(
            Arg.Any<Sid>(),
            Arg.Any<IReadOnlySet<Sid>>(),
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Is(tableId),
            Arg.Any<IReadOnlyList<Consent>>(),
            Arg.Any<DatabaseDialect>())
            .Returns(decision);

        var devEnv = Substitute.For<Microsoft.Extensions.Hosting.IHostEnvironment>();
        devEnv.EnvironmentName.Returns("Development");
        var sqlExecutor = new SqlDataSourceExecutor(environment: devEnv);

        var executionService = new GatewayExecutionService(
            repository,
            resolutionService,
            cacheService,
            maskingProvider,
            null,
            null,
            null,
            dataSourceExecutors: [sqlExecutor]);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("objectSid", "S-1-5-21-USER-1")
        }, "TestAuth"));

        var (rows, _) = await executionService.ExecuteTableQueryAsync(principal, tableId, first: 10);

        // Generated mock rows for finance_table_1 have amount = 100.50m * rowNum
        // For rowNum = 1..10, rowNum >= 5 has amount >= 502.50 > 500.
        // Therefore, rows MUST NOT be empty and all returned rows must satisfy amount > 500!
        rows.ShouldNotBeEmpty();
        rows.ShouldAllBe(r => r.ContainsKey("amount") && Convert.ToDecimal(r["amount"]) > 500m);
    }

    [Fact]
    public async Task AuditLogEntry_DetailsJson_SerializesSafelyWithSpecialCharacters()
    {
        // Finding 16: Audit DetailsJson must use proper JSON serialization to prevent malformed JSON / injection
        var repository = Substitute.For<IGovernanceRepository>();
        var cacheService = Substitute.For<IConsentCacheService>();
        var resolutionService = Substitute.For<IConsentResolutionService>();
        var maskingProvider = Substitute.For<IColumnMaskingProvider>();
        var chunkedExecutor = Substitute.For<IChunkedQueryExecutor>();

        var tableId = new TableIdentifier("finance", "dbo", "finance_table_1");
        var metadata = new TableMetadata
        {
            Table = new Table { Id = Guid.NewGuid(), SourceName = "finance", SchemaName = "dbo", TableName = "finance_table_1", DisplayName = "Finance 1" },
            Identifier = tableId,
            Columns = new List<TableColumn>
            {
                new() { Id = Guid.NewGuid(), ColumnName = "id", DataType = "int" }
            }
        };
        repository.GetTableMetadataAsync(tableId, Arg.Any<CancellationToken>()).Returns(metadata);

        var decision = new TableAccessDecision(
            Table: tableId,
            IsAllowed: false,
            DeniedReasons: new[] { "Invalid \"quote\" and newline \n or backslash \\ and <script>", "Reason 2" },
            ColumnAccess: new Dictionary<string, ColumnAccessLevel>(),
            CombinedRowFilterSql: null
        );

        resolutionService.ResolveAccess(
            Arg.Any<Sid>(),
            Arg.Any<IReadOnlySet<Sid>>(),
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Is(tableId),
            Arg.Any<IReadOnlyList<Consent>>(),
            Arg.Any<DatabaseDialect>())
            .Returns(decision);

        AuditLogEntry? capturedAudit = null;
        await repository.RecordAuditEventAsync(Arg.Do<AuditLogEntry>(a => capturedAudit = a), Arg.Any<CancellationToken>());

        var executionService = new GatewayExecutionService(
            repository,
            resolutionService,
            cacheService,
            maskingProvider,
            chunkedExecutor,
            null);

        var principal = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("objectSid", "S-1-5-21-AUDIT-TEST")
        }, "TestAuth"));

        // Act: Must throw GatewaySecurityException because access is denied, but audit log is recorded before throw
        await Should.ThrowAsync<GatewaySecurityException>(async () =>
        {
            await executionService.ExecuteTableQueryAsync(principal, tableId);
        });

        // Assert: Audit log must have been recorded with valid, parseable JSON containing the exact special characters
        capturedAudit.ShouldNotBeNull();
        capturedAudit.DetailsJson.ShouldNotBeNullOrWhiteSpace();

        // Must parse cleanly as JSON
        using var jsonDoc = JsonDocument.Parse(capturedAudit.DetailsJson);
        jsonDoc.RootElement.GetProperty("is_allowed").GetBoolean().ShouldBeFalse();
        var reasons = jsonDoc.RootElement.GetProperty("reasons");
        reasons.GetArrayLength().ShouldBe(2);
        reasons[0].GetString().ShouldBe("Invalid \"quote\" and newline \n or backslash \\ and <script>");
    }

    [Fact]
    public void ComputeSubjectContextHash_ProducesConsistentHash_AndDifferentiatesRolesAndGroups()
    {
        var hashDefault = IConsentCacheService.ComputeSubjectContextHash(null, null);
        hashDefault.ShouldBe("default");

        var groups1 = new HashSet<Sid> { new("S-1-5-21-G1"), new("S-1-5-21-G2") };
        var roles1 = new HashSet<string> { "Analyst", "DataConsumer" };

        var hash1 = IConsentCacheService.ComputeSubjectContextHash(groups1, roles1);
        hash1.ShouldNotBe("default");

        // Canonical order independence
        var groups2 = new HashSet<Sid> { new("S-1-5-21-G2"), new("S-1-5-21-G1") };
        var roles2 = new HashSet<string> { "DataConsumer", "Analyst" };
        var hash2 = IConsentCacheService.ComputeSubjectContextHash(groups2, roles2);
        hash2.ShouldBe(hash1);

        // Membership change yields different hash
        var groupsChanged = new HashSet<Sid> { new("S-1-5-21-G1") };
        var hashChanged = IConsentCacheService.ComputeSubjectContextHash(groupsChanged, roles1);
        hashChanged.ShouldNotBe(hash1);
    }

    [Fact]
    public void RowFilterSqlBuilder_UserAttribute_FailsClosedWith1Equals0()
    {
        var builder = new RowFilterSqlBuilder();
        var filter = new ConsentRowFilter
        {
            ColumnName = "department_id",
            Operator = "EQ",
            ValueType = "string",
            ValueJson = "\"DEPT_01\"",
            ValueSource = "USER_ATTRIBUTE"
        };

        var condition = builder.FormatCondition(filter, DatabaseDialect.SqlServer);
        condition.ShouldBe("1 = 0");
    }


    [Fact]
    public void CompositeKey_NaN_GeneratesConsistentHashCodeAndEquality()
    {
        var keyFloat = new CompositeKey(float.NaN);
        var keyDouble = new CompositeKey(double.NaN);

        keyFloat.Equals(keyDouble).ShouldBeTrue();
        keyFloat.GetHashCode().ShouldBe(keyDouble.GetHashCode());
    }
}
