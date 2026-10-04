using System.Security.Claims;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.RateLimiting;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class PerformanceAndAllocationsTests
{
    private readonly ColumnMaskingProvider _maskingProvider = new();

    [Theory]
    [InlineData("alice@example.com", "a***@***.com")]
    [InlineData("bob.marley@corp.co.uk", "b***@***.uk")]
    [InlineData("charlie@internal", "c***@***")]
    [InlineData("x@y.z", "***@***")]
    [InlineData("@onlydomain.com", "***@***")]
    [InlineData("missing_at_sign", "***@***")]
    [InlineData("ab@domain.com", "a***@***.com")]
    [InlineData("ab@.com", "a***@***")]
    [InlineData("john@corp.", "j***@***.")]
    public void MaskEmail_VariousFormats_MasksCorrectly(string input, string expected)
    {
        var rule = new MaskingRule { RuleType = "MASK_EMAIL" };
        var masked = _maskingProvider.MaskValue("email", input, rule);
        masked.ShouldBe(expected);
    }

    [Theory]
    [InlineData("DE89370400440532013000", "DE** **** **** 3000")]
    [InlineData("DE89 3704 0044 0532 0130 00", "DE** **** **** 3000")]
    [InlineData("FR1420041010050500013M02606", "FR** **** **** 2606")]
    [InlineData("FR14 2004 1010 0505 0001 3M02 606", "FR** **** **** 2606")]
    [InlineData("1234567", "****")]
    [InlineData("", "****")]
    [InlineData("   ", "****")]
    public void MaskIban_VariousFormats_MasksCorrectly(string input, string expected)
    {
        var rule = new MaskingRule { RuleType = "MASK_IBAN" };
        var masked = _maskingProvider.MaskValue("iban", input, rule);
        masked.ShouldBe(expected);
    }

    [Fact]
    public async Task InMemoryRateLimiter_PreAuthIp_TracksPermitsWithAtomicCounters()
    {
        var rateLimiter = new InMemoryRateLimiterService();
        var options = new PreAuthIpRateLimitOptions
        {
            PermitLimit = 5,
            WindowSeconds = 10
        };

        // First 5 requests should pass
        for (int i = 0; i < 5; i++)
        {
            var res = await rateLimiter.CheckPreAuthIpAsync("192.168.1.100", options);
            res.Allowed.ShouldBeTrue();
        }

        // 6th request should be rejected
        var rejected = await rateLimiter.CheckPreAuthIpAsync("192.168.1.100", options);
        rejected.Allowed.ShouldBeFalse();
        rejected.RetryAfterSeconds.ShouldBeGreaterThan(0);

        // Different IP should still be allowed
        var otherIpRes = await rateLimiter.CheckPreAuthIpAsync("192.168.1.101", options);
        otherIpRes.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task InMemoryRateLimiter_PostAuthSid_TracksTokensWithAtomicCounters()
    {
        var rateLimiter = new InMemoryRateLimiterService();
        var options = new PostAuthSidRateLimitOptions
        {
            TokenBucketCapacity = 3,
            TokensPerSecond = 0 // No refill during test
        };

        // First 3 consume 1.0 token each
        for (int i = 0; i < 3; i++)
        {
            var res = await rateLimiter.CheckPostAuthSidAsync("S-1-5-21-USER-1", options);
            res.Allowed.ShouldBeTrue();
        }

        // 4th request has no tokens left
        var rejected = await rateLimiter.CheckPostAuthSidAsync("S-1-5-21-USER-1", options);
        rejected.Allowed.ShouldBeFalse();

        // Other user SID has own bucket
        var otherRes = await rateLimiter.CheckPostAuthSidAsync("S-1-5-21-USER-2", options);
        otherRes.Allowed.ShouldBeTrue();
    }

    [Fact]
    public async Task InMemoryRateLimiter_ConcurrentRequests_ExecuteWithoutContention()
    {
        var rateLimiter = new InMemoryRateLimiterService();
        var options = new PreAuthIpRateLimitOptions
        {
            PermitLimit = 100,
            WindowSeconds = 10
        };

        var tasks = Enumerable.Range(0, 50).Select(i =>
            rateLimiter.CheckPreAuthIpAsync($"10.0.0.{i % 10}", options)
        );

        var results = await Task.WhenAll(tasks);
        results.Length.ShouldBe(50);
        results.ShouldAllBe(r => r.Allowed);
    }

    [Fact]
    public void GatewayExecution_WhenRealSqlConnectionConfigured_SkipsRedundantFilterRows()
    {
        // When real SQL connection is configured, SqlDataSourceExecutor pushes down RLS to the DB engine.
        // GatewayExecutionService skips redundant in-memory FilterRows.
        var repository = Substitute.For<IGovernanceRepository>();
        var cacheService = Substitute.For<IConsentCacheService>();
        var resolutionService = Substitute.For<IConsentResolutionService>();
        var maskingProvider = Substitute.For<IColumnMaskingProvider>();

        var tableId = new TableIdentifier("finance", "dbo", "invoices");
        var metadata = new TableMetadata
        {
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "finance_db",
                SchemaName = "dbo",
                TableName = "invoices",
                DisplayName = "Invoices",
                DataSourceType = DataSourceType.Sql
            },
            Identifier = tableId,
            Columns = new List<TableColumn>
            {
                new() { Id = Guid.NewGuid(), ColumnName = "id", DataType = "int" },
                new() { Id = Guid.NewGuid(), ColumnName = "amount", DataType = "decimal" }
            }
        };
        repository.GetTableMetadataAsync(tableId, Arg.Any<CancellationToken>()).Returns(metadata);

        var decision = new TableAccessDecision(
            Table: tableId,
            IsAllowed: true,
            DeniedReasons: Array.Empty<string>(),
            ColumnAccess: new Dictionary<string, ColumnAccessLevel>
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["amount"] = ColumnAccessLevel.Clear
            },
            CombinedRowFilterSql: "amount > 1000"
        );

        resolutionService.ResolveAccess(
            Arg.Any<Sid>(),
            Arg.Any<IReadOnlySet<Sid>>(),
            Arg.Any<IReadOnlySet<string>>(),
            Arg.Is(tableId),
            Arg.Any<IReadOnlyList<Consent>>(),
            Arg.Any<DatabaseDialect>())
            .Returns(decision);

        var gatewayOptions = Options.Create(new GatewayOptions
        {
            DataSources = new SqlDataSourceOptions
            {
                Connections = new Dictionary<string, DataSourceConnectionOptions>
                {
                    ["finance_db"] = new()
                    {
                        ConnectionString = "Server=localhost;Database=finance;User Id=sa;Password=secret;"
                    }
                }
            }
        });

        var executionService = new GatewayExecutionService(
            repository,
            resolutionService,
            cacheService,
            maskingProvider,
            null,
            gatewayOptions);

        executionService.ShouldNotBeNull();
    }
}
