using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Cache;
using Autheris.Infrastructure.Persistence;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit;

public class InvoiceItemsBatchExecutionTests : IDisposable
{
    private readonly SqliteGovernanceRepository _repository;
    private readonly ConsentResolutionService _resolutionService;
    private readonly ConsentCacheService _cacheService;
    private readonly ColumnMaskingProvider _maskingProvider;
    private readonly IOptions<GatewayOptions> _options;

    public InvoiceItemsBatchExecutionTests()
    {
        var epochService = new EpochValidationService();
        _repository = new SqliteGovernanceRepository(epochService);
        _resolutionService = new ConsentResolutionService();
        var memoryCache = new MemoryCache(new MemoryCacheOptions());
        var eventBus = new Autheris.Infrastructure.Messaging.InProcessChannelEventBus();
        _cacheService = new ConsentCacheService(memoryCache, epochService, eventBus);
        _options = Options.Create(new GatewayOptions
        {
            DataMasking = new DataMaskingOptions { HmacSecretKeyVaultRef = "Test-Vault-Key" }
        });
        _maskingProvider = new ColumnMaskingProvider(_options);
    }

    public void Dispose()
    {
        _repository.Dispose();
    }

    [Fact]
    public async Task LoadInvoiceItemsBatchAsync_DispatchesGovernedQueryToExecutor_WithParameterizedInFilter()
    {
        // Arrange
        var mockExecutor = Substitute.For<IDataSourceExecutor>();
        mockExecutor.SupportedType.Returns(DataSourceType.Sql);

        DataSourceExecutionContext? capturedContext = null;
        mockExecutor.ExecuteAsync(Arg.Do<DataSourceExecutionContext>(ctx => capturedContext = ctx), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(new List<IReadOnlyDictionary<string, object?>>
            {
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = "ITEM-101",
                    ["parent_id"] = "INV-001",
                    ["product_name"] = "Production Widget",
                    ["price"] = 99.95m,
                    ["sensitive_note"] = "Real Database Note"
                }
            }));

        var service = new GatewayExecutionService(
            _repository,
            _repository,
            _repository,
            _resolutionService,
            _cacheService,
            _maskingProvider,
            new ChunkedQueryExecutor(500),
            _options,
            drainController: null,
            dataSourceExecutors: new[] { mockExecutor });

        var childTableId = new TableIdentifier("finance", "dbo", "finance_items");
        var userSid = new Sid("S-1-5-21-EXECUTOR-TEST");
        var decision = TableAccessDecision.Allowed(
            childTableId,
            new Dictionary<string, ColumnAccessLevel>
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["parent_id"] = ColumnAccessLevel.Clear,
                ["product_name"] = ColumnAccessLevel.Clear,
                ["price"] = ColumnAccessLevel.Clear,
                ["sensitive_note"] = ColumnAccessLevel.Clear
            },
            hasUnconstrainedColumnAllow: true);

        await _cacheService.SetCachedDecisionAsync(new TenantId("tenant-test"), userSid, childTableId, decision, TimeSpan.FromMinutes(5));

        var claims = new[]
        {
            new Claim("objectSid", userSid.Value),
            new Claim("tenant_id", "tenant-test")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        // Act
        var result = await service.LoadInvoiceItemsBatchAsync(principal, new[] { "INV-001" });

        // Assert
        result.ShouldContainKey("INV-001");
        var items = result["INV-001"];
        items.Count.ShouldBe(1);
        items[0].Id.ShouldBe("ITEM-101");
        items[0].InvoiceId.ShouldBe("INV-001");
        items[0].ProductName.ShouldBe("Production Widget");
        items[0].Price.ShouldBe(99.95m);
        items[0].SensitiveNote.ShouldBe("Real Database Note");

        // Verify that mockExecutor was called with TableFilterClause containing parameterized IN filter
        await mockExecutor.Received(1).ExecuteAsync(Arg.Any<DataSourceExecutionContext>(), Arg.Any<CancellationToken>());
        capturedContext.ShouldNotBeNull();
        capturedContext.Items.ShouldContainKey(TableQueryItems.Filter);
        var filter = capturedContext.Items[TableQueryItems.Filter] as TableFilterClause;
        filter.ShouldNotBeNull();
        filter.ReferencedColumns.ShouldContain("parent_id");
        filter.Parameters.Values.ShouldContain("INV-001");
    }

    [Fact]
    public async Task LoadInvoiceItemsBatchAsync_WhenNoExecutorConfiguredAndNoConnection_ReturnsEmptyListsFailClosed()
    {
        // Arrange - service with no executors
        var service = new GatewayExecutionService(
            _repository,
            _repository,
            _repository,
            _resolutionService,
            _cacheService,
            _maskingProvider,
            new ChunkedQueryExecutor(500),
            _options,
            drainController: null,
            dataSourceExecutors: Array.Empty<IDataSourceExecutor>());

        var childTableId = new TableIdentifier("finance", "dbo", "finance_items");
        var userSid = new Sid("S-1-5-21-NO-EXEC-TEST");
        var decision = TableAccessDecision.Allowed(
            childTableId,
            new Dictionary<string, ColumnAccessLevel>(),
            hasUnconstrainedColumnAllow: true);

        await _cacheService.SetCachedDecisionAsync(userSid, childTableId, decision, TimeSpan.FromMinutes(5));

        var claims = new[]
        {
            new Claim("objectSid", userSid.Value),
            new Claim("tenant_id", "tenant-test")
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));

        // Act
        var result = await service.LoadInvoiceItemsBatchAsync(principal, new[] { "INV-001", "INV-002" });

        // Assert - fail-closed: empty lists for each requested invoice ID
        result.ShouldContainKey("INV-001");
        result["INV-001"].ShouldBeEmpty();
        result.ShouldContainKey("INV-002");
        result["INV-002"].ShouldBeEmpty();
    }
}
