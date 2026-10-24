namespace Autheris.Tests.Unit.DataApi;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Data.Interfaces;
using Autheris.Application.Data.Services;
using Autheris.Application.Interfaces;
using Autheris.Application.Policy;
using Autheris.Application.Sql.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using System.Linq;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class GovernedDataQueryServiceTests
{
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IUnifiedPolicyDecisionPoint _pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
    private readonly IGovernedSqlExecutionService _sqlExecutionService = Substitute.For<IGovernedSqlExecutionService>();
    private readonly IFederatedQueryExecutionService _federatedExecutionService = Substitute.For<IFederatedQueryExecutionService>();
    private readonly IColumnMaskingProvider _maskingProvider = Substitute.For<IColumnMaskingProvider>();

    private readonly TableIdentifier _customerTable = new("sales", "dbo", "customers");

    private TableMetadata CreateCustomerTableMetadata(DataSourceType sourceType = DataSourceType.Sql, IReadOnlyList<string>? primaryKey = null)
    {
        return new TableMetadata
        {
            Identifier = _customerTable,
            Table = new Table
            {
                Id = Guid.NewGuid(),
                SourceName = "sales",
                SchemaName = "dbo",
                TableName = "customers",
                DataSourceType = sourceType,
                IsActive = true
            },
            Columns = new List<TableColumn>
            {
                new() { ColumnName = "id", DataType = "int", IsSensitive = false },
                new() { ColumnName = "email", DataType = "varchar", IsSensitive = true },
                new() { ColumnName = "name", DataType = "varchar", IsSensitive = false }
            },
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = new() { RuleType = "MASK_EMAIL" }
            },
            PrimaryKeyColumns = primaryKey ?? new[] { "id" }
        };
    }

    [Fact]
    public async Task ExecuteQueryAsync_ValidRequest_AppliesMaskingAndLimits()
    {
        // Arrange
        var metadata = CreateCustomerTableMetadata();
        _metadataRepo.GetTableMetadataAsync(_customerTable, Arg.Any<CancellationToken>())
            .Returns(metadata);

        var decision = TableAccessDecision.Allowed(
            _customerTable,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["email"] = ColumnAccessLevel.Mask,
                ["name"] = ColumnAccessLevel.Clear
            },
            hasUnconstrainedColumnAllow: true);

        _pdp.EvaluateAccessAsync(
            _customerTable,
            metadata,
            Arg.Any<Autheris.Domain.Security.SecurityPrincipalContext>(),
            Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<CancellationToken>())
            .Returns(decision);

        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 1, ["email"] = "doe@example.com", ["name"] = "John Doe" }
        };

        _sqlExecutionService.ExecuteQueryBufferedAsync(
            Arg.Any<GovernedSqlQueryRequest>(),
            Arg.Any<ClaimsPrincipal>(),
            Arg.Any<TenantId>(),
            Arg.Any<CancellationToken>())
            .Returns(new GovernedSqlResult("SELECT id, email, name FROM sales.dbo.customers", new[] { "id", "email", "name" }, rows, 1, 10));

        _maskingProvider.MaskValue("email", "doe@example.com", Arg.Any<MaskingRule>())
            .Returns("d***@example.com");

        var service = new GovernedDataQueryService(
            _metadataRepo,
            _pdp,
            _sqlExecutionService,
            _federatedExecutionService,
            _maskingProvider);

        var request = new DatasetQueryRequest(
            Table: _customerTable,
            SelectColumns: new[] { "id", "email" },
            Limit: 10,
            Offset: 0);

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", "user-123"),
            new Claim("tenant", "default")
        }, "TestAuth"));
        var context = new RequestContext(user, new TenantId("default"));

        // Act
        var result = await service.ExecuteQueryAsync(request, context);

        // Assert
        result.ShouldNotBeNull();
        result.Dataset.ShouldBe("sales.dbo.customers");
        result.Limit.ShouldBe(10);
        result.Columns.Count.ShouldBe(2);
        result.Columns[0].Name.ShouldBe("id");
        result.Columns[0].Masked.ShouldBeFalse();
        result.Columns[1].Name.ShouldBe("email");
        result.Columns[1].Masked.ShouldBeTrue();

        result.Data.Count.ShouldBe(1);
        result.Data[0]["email"].ShouldBe("d***@example.com");
    }

    [Fact]
    public async Task ExecuteQueryAsync_ForbiddenTable_ThrowsForbiddenException()
    {
        // Arrange
        var metadata = CreateCustomerTableMetadata();
        _metadataRepo.GetTableMetadataAsync(_customerTable, Arg.Any<CancellationToken>())
            .Returns(metadata);

        var deniedDecision = TableAccessDecision.Denied(_customerTable, "ReBAC Access Denied: Caller does not have can_query on sales.dbo.customers");

        _pdp.EvaluateAccessAsync(
            _customerTable,
            metadata,
            Arg.Any<Autheris.Domain.Security.SecurityPrincipalContext>(),
            Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<CancellationToken>())
            .Returns(deniedDecision);

        var service = new GovernedDataQueryService(
            _metadataRepo,
            _pdp,
            _sqlExecutionService,
            _federatedExecutionService,
            _maskingProvider);

        var request = new DatasetQueryRequest(_customerTable, Limit: 10);
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "unauthorized-user") }, "TestAuth"));
        var context = new RequestContext(user, new TenantId("default"));

        // Act & Assert
        await Should.ThrowAsync<UnauthorizedAccessException>(async () =>
        {
            await service.ExecuteQueryAsync(request, context);
        });
    }

    [Fact]
    public async Task ExecuteQueryAsync_FilterOnMaskedColumn_RejectsWithBadRequest()
    {
        // Arrange
        var metadata = CreateCustomerTableMetadata();
        _metadataRepo.GetTableMetadataAsync(_customerTable, Arg.Any<CancellationToken>())
            .Returns(metadata);

        var decision = TableAccessDecision.Allowed(
            _customerTable,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["email"] = ColumnAccessLevel.Mask,
                ["name"] = ColumnAccessLevel.Clear
            },
            hasUnconstrainedColumnAllow: true);

        _pdp.EvaluateAccessAsync(
            _customerTable,
            metadata,
            Arg.Any<Autheris.Domain.Security.SecurityPrincipalContext>(),
            Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<CancellationToken>())
            .Returns(decision);

        var service = new GovernedDataQueryService(
            _metadataRepo,
            _pdp,
            _sqlExecutionService,
            _federatedExecutionService,
            _maskingProvider);

        var request = new DatasetQueryRequest(
            Table: _customerTable,
            FilterExpression: "email eq 'secret'",
            Limit: 10);

        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "user-123") }, "TestAuth"));
        var context = new RequestContext(user, new TenantId("default"));

        // Act & Assert (Oracle inference rejection)
        var exception = await Should.ThrowAsync<ArgumentException>(async () =>
        {
            await service.ExecuteQueryAsync(request, context);
        });

        exception.Message.ShouldContain("email");
    }

    // #324 (SEC-RG-10): "eq null" / "ne null" rewrite to IS [NOT] NULL before the guards, which still see the masked column.
    [Trait("Category", "Security")]
    [Theory]
    [InlineData("email eq null")]
    [InlineData("email ne null")]
    [InlineData("EMAIL EQ NULL")]
    public async Task IsNullOnMaskedColumn_Denied(string filter)
    {
        var metadata = CreateCustomerTableMetadata();
        _metadataRepo.GetTableMetadataAsync(_customerTable, Arg.Any<CancellationToken>()).Returns(metadata);
        var decision = TableAccessDecision.Allowed(
            _customerTable,
            new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["email"] = ColumnAccessLevel.Mask,
                ["name"] = ColumnAccessLevel.Clear
            },
            hasUnconstrainedColumnAllow: true);
        _pdp.EvaluateAccessAsync(_customerTable, metadata, Arg.Any<Autheris.Domain.Security.SecurityPrincipalContext>(),
            Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>()).Returns(decision);
        var service = new GovernedDataQueryService(_metadataRepo, _pdp, _sqlExecutionService, _federatedExecutionService, _maskingProvider);
        var user = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "user-123") }, "TestAuth"));

        var exception = await Should.ThrowAsync<ArgumentException>(() => service.ExecuteQueryAsync(
            new DatasetQueryRequest(Table: _customerTable, FilterExpression: filter, Limit: 10), new RequestContext(user, new TenantId("default"))));

        exception.Message.ShouldContain("email");
        await _sqlExecutionService.DidNotReceive().ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>());
    }

    // ---- Pagination window (FR-07/FR-09/FR-10) ----

    private void AllowAll(TableMetadata metadata)
    {
        _metadataRepo.GetTableMetadataAsync(_customerTable, Arg.Any<CancellationToken>()).Returns(metadata);
        _pdp.EvaluateAccessAsync(
            _customerTable,
            metadata,
            Arg.Any<Autheris.Domain.Security.SecurityPrincipalContext>(),
            Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<CancellationToken>())
            .Returns(TableAccessDecision.Allowed(
                _customerTable,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["email"] = ColumnAccessLevel.Clear,
                    ["name"] = ColumnAccessLevel.Clear
                },
                hasUnconstrainedColumnAllow: true));
    }

    private static List<IReadOnlyDictionary<string, object?>> Rows(int count) =>
        Enumerable.Range(1, count)
            .Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["id"] = i, ["name"] = "n" + i })
            .ToList();

    private GovernedDataQueryService CreateService(GatewayOptions? options = null, AnalyticsOptions? analytics = null) =>
        new(_metadataRepo, _pdp, _sqlExecutionService, _federatedExecutionService, _maskingProvider,
            options == null ? null : Options.Create(options),
            analytics == null ? null : Options.Create(analytics));

    private static RequestContext Ctx() =>
        new(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "user-123") }, "TestAuth")), new TenantId("default"));

    private static GovernedSqlResult SqlResult(int rowCount, bool truncated = false) =>
        new("q", new[] { "id", "name" }, Rows(rowCount), rowCount, 1, truncated);

    [Fact]
    public async Task ExecuteQueryAsync_SingleSource_WithOffset_EmitsOffsetRowsLimitInTrinoForm()
    {
        AllowAll(CreateCustomerTableMetadata());
        GovernedSqlQueryRequest? captured = null;
        _sqlExecutionService.ExecuteQueryBufferedAsync(
                Arg.Do<GovernedSqlQueryRequest>(r => captured = r),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(51));

        var result = await CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 50, Offset: 10, Transport: "Rest"), Ctx());

        captured.ShouldNotBeNull();
        captured.Sql.ShouldContain("OFFSET 10 ROWS");
        captured.Sql.ShouldEndWith("OFFSET 10 ROWS LIMIT 51");
        captured.Sql.ShouldNotContain("LIMIT 61");
        captured.Sql.ShouldContain("FROM sales.dbo.customers");
        captured.Transport.ShouldBe("Rest");

        // The database already skipped the offset: all 51 rows are the page plus the HasMore probe, nothing is skipped in memory.
        result.Count.ShouldBe(50);
        result.Data[0]["id"].ShouldBe(1);
        result.Data[49]["id"].ShouldBe(50);
        result.HasMore.ShouldBeTrue();
        result.Offset.ShouldBe(10);
    }

    [Fact]
    public async Task ExecuteQueryAsync_SingleSource_OffsetZero_EmitsLimitOnly()
    {
        AllowAll(CreateCustomerTableMetadata());
        GovernedSqlQueryRequest? captured = null;
        _sqlExecutionService.ExecuteQueryBufferedAsync(
                Arg.Do<GovernedSqlQueryRequest>(r => captured = r),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(3));

        await CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 50, Offset: 0), Ctx());

        captured.ShouldNotBeNull();
        captured.Sql.ShouldNotContain("OFFSET", Case.Insensitive);
        captured.Sql.ShouldEndWith("LIMIT 51");
    }

    [Fact]
    [Trait("Category", "Security")]
    public async Task PlannerCallerAudit_ZeroNeverBecomesDefault()
    {
        // SEC-PG-07 (#301 limit=0): 0 is an explicit zero-row page. It is neither the default 50 nor unbounded; the plan still runs
        // (authorization, masking) and the statement is bounded to one look-ahead row.
        AllowAll(CreateCustomerTableMetadata());
        GovernedSqlQueryRequest? captured = null;
        _sqlExecutionService.ExecuteQueryBufferedAsync(
                Arg.Do<GovernedSqlQueryRequest>(r => captured = r),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(3));

        var result = await CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 0, Offset: 0), Ctx());

        result.Limit.ShouldBe(0);
        result.Count.ShouldBe(0);
        result.Data.ShouldBeEmpty();
        result.HasMore.ShouldBeTrue();
        captured.ShouldNotBeNull();
        captured.Sql.ShouldEndWith("LIMIT 1");
        captured.Sql.ShouldNotContain("LIMIT 0");
        captured.Sql.ShouldNotContain("LIMIT 51");
    }

    [Fact]
    public async Task ExecuteQueryAsync_NegativeLimit_IsRejected()
    {
        AllowAll(CreateCustomerTableMetadata());

        await Should.ThrowAsync<ArgumentException>(() => CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: -1), Ctx()));
    }

    [Fact]
    public async Task ExecuteQueryAsync_WithOffset_LastPage_HasMoreFalse()
    {
        AllowAll(CreateCustomerTableMetadata());
        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(45));

        var result = await CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 50, Offset: 10), Ctx());

        result.Count.ShouldBe(45);
        result.HasMore.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteQueryAsync_OffsetBeyondData_ReturnsEmptyPage()
    {
        AllowAll(CreateCustomerTableMetadata());
        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(0));

        var result = await CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 50, Offset: 100), Ctx());

        result.Count.ShouldBe(0);
        result.HasMore.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteQueryAsync_SingleSource_DeepOffset_NotCappedByWindow()
    {
        AllowAll(CreateCustomerTableMetadata());
        GovernedSqlQueryRequest? captured = null;
        _sqlExecutionService.ExecuteQueryBufferedAsync(
                Arg.Do<GovernedSqlQueryRequest>(r => captured = r),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(51));
        var service = CreateService(
            new GatewayOptions { WebSql = new WebSqlOptions { MaxAllowedRows = 1000 } },
            new AnalyticsOptions { DatasetApi = new DatasetApiOptions { MaxOffset = 100_000 } });

        var result = await service.ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 50, Offset: 50_000), Ctx());

        captured.ShouldNotBeNull();
        captured.Sql.ShouldEndWith("OFFSET 50000 ROWS LIMIT 51");
        result.Count.ShouldBe(50);
        result.HasMore.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteQueryAsync_OffsetAboveMaxOffset_Rejected()
    {
        AllowAll(CreateCustomerTableMetadata());
        var service = CreateService(analytics: new AnalyticsOptions { DatasetApi = new DatasetApiOptions { MaxOffset = 1000 } });

        await Should.ThrowAsync<ArgumentException>(() => service.ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 50, Offset: 1001), Ctx()));
        await _sqlExecutionService.DidNotReceive().ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OffsetAboveDefaultCap_Throws()
    {
        AllowAll(CreateCustomerTableMetadata());
        var service = CreateService();

        await Should.ThrowAsync<ArgumentException>(() => service.ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 10, Offset: 10_001), Ctx()));
        await _sqlExecutionService.DidNotReceive().ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OffsetAtCap_Allowed()
    {
        AllowAll(CreateCustomerTableMetadata());
        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(0));
        var service = CreateService();

        var result = await service.ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 10, Offset: 10_000), Ctx());

        result.Count.ShouldBe(0);
    }

    [Fact]
    public async Task ExecuteQueryAsync_NoPrimaryKeyNoOrderBy_OffsetPositive_UsesClearColumnsAsOrder()
    {
        AllowAll(CreateCustomerTableMetadata(primaryKey: Array.Empty<string>()));
        GovernedSqlQueryRequest? captured = null;
        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Do<GovernedSqlQueryRequest>(r => captured = r), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(5));
        var service = CreateService();

        var result = await service.ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 50, Offset: 10), Ctx());

        result.Count.ShouldBe(5);
        captured.ShouldNotBeNull();
        captured!.Sql.ShouldContain("ORDER BY \"id\"");
    }

    [Fact]
    public async Task ExecuteQueryAsync_CrossSource_StillUsesWindowCapAndNoOffset()
    {
        AllowAll(CreateCustomerTableMetadata(DataSourceType.HttpDeclarative));
        GovernedSqlQueryRequest? captured = null;
        _federatedExecutionService.ExecuteQueryBufferedAsync(
                Arg.Do<GovernedSqlQueryRequest>(r => captured = r),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(61));

        var result = await CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 50, Offset: 10, Transport: "Rest"), Ctx());

        captured.ShouldNotBeNull();
        captured.Sql.ShouldNotContain("OFFSET", Case.Insensitive);
        captured.Sql.ShouldEndWith("LIMIT 61");
        result.Count.ShouldBe(50);
        result.Data[0]["id"].ShouldBe(11);
        result.Data[49]["id"].ShouldBe(60);
        result.HasMore.ShouldBeTrue();
    }

    [Theory]
    [InlineData(949, 50, true)]   // window 1000 == effective cap
    [InlineData(950, 50, false)]  // window 1001 > cap
    [InlineData(980, 50, false)]  // the reviewed scenario: MaxAllowedRows=1000, offset=980, limit=50
    public async Task ExecuteQueryAsync_WindowCapFollowsWebSqlMaxAllowedRows(int offset, int limit, bool allowed)
    {
        AllowAll(CreateCustomerTableMetadata(DataSourceType.HttpDeclarative));
        _federatedExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(offset + limit + 1));
        var service = CreateService(new GatewayOptions { WebSql = new WebSqlOptions { MaxAllowedRows = 1000 } });
        var request = new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: limit, Offset: offset);

        if (allowed)
        {
            (await service.ExecuteQueryAsync(request, Ctx())).Count.ShouldBe(limit);
        }
        else
        {
            await Should.ThrowAsync<ArgumentException>(() => service.ExecuteQueryAsync(request, Ctx()));
            await _federatedExecutionService.DidNotReceive().ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData(9949, 50, true)]
    [InlineData(9950, 50, false)]
    public async Task ExecuteQueryAsync_DefaultWindowCap_Is10000(int offset, int limit, bool allowed)
    {
        AllowAll(CreateCustomerTableMetadata(DataSourceType.HttpDeclarative));
        _federatedExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(offset + limit + 1));
        var service = CreateService(new GatewayOptions { WebSql = new WebSqlOptions { MaxAllowedRows = 0 } });
        var request = new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: limit, Offset: offset);

        if (allowed)
        {
            (await service.ExecuteQueryAsync(request, Ctx())).Count.ShouldBe(limit);
        }
        else
        {
            await Should.ThrowAsync<ArgumentException>(() => service.ExecuteQueryAsync(request, Ctx()));
        }
    }

    [Fact]
    public async Task ExecuteQueryAsync_CrossSourcePath_CapsWindowAtDuckDbMaxResultRows()
    {
        var metadata = CreateCustomerTableMetadata(DataSourceType.HttpDeclarative);
        AllowAll(metadata);
        _federatedExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(100));
        var service = CreateService(new GatewayOptions
        {
            WebSql = new WebSqlOptions { MaxAllowedRows = 0 },
            DuckDbOlap = new DuckDbOlapOptions { MaxResultRows = 500 }
        });

        await Should.ThrowAsync<ArgumentException>(() => service.ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 50, Offset: 450), Ctx()));
        (await service.ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 50, Offset: 0), Ctx())).Count.ShouldBe(50);
    }

    [Fact]
    public async Task ExecuteQueryAsync_TruncatedBelowWindow_FailsClosedInsteadOfShortPage()
    {
        AllowAll(CreateCustomerTableMetadata());
        // The SQL layer cut the result (Truncated) before the requested page plus probe row was served.
        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(10, truncated: true));

        await Should.ThrowAsync<ArgumentException>(() => CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 50, Offset: 980), Ctx()));
    }

    [Fact]
    public async Task ExecuteQueryAsync_WhenInDbColumnMaskingExecuted_SkipsSecondMaskPass()
    {
        var metadata = CreateCustomerTableMetadata();
        _metadataRepo.GetTableMetadataAsync(_customerTable, Arg.Any<CancellationToken>()).Returns(metadata);
        _pdp.EvaluateAccessAsync(
                _customerTable,
                metadata,
                Arg.Any<Autheris.Domain.Security.SecurityPrincipalContext>(),
                Arg.Any<IReadOnlyList<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(TableAccessDecision.Allowed(
                _customerTable,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["email"] = ColumnAccessLevel.Mask
                },
                hasUnconstrainedColumnAllow: true));

        const string alreadyMaskedBySql = "a1b2c3d4e5f6";
        var sqlResult = new GovernedSqlResult(
            OriginalSql: "SELECT id, email FROM sales.dbo.customers LIMIT 51",
            Columns: new[] { "id", "email" },
            Rows: new IReadOnlyDictionary<string, object?>[]
            {
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["id"] = 1, ["email"] = alreadyMaskedBySql }
            },
            RowCount: 1,
            ElapsedMilliseconds: 1,
            InDbColumnMaskingExecuted: true);

        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(sqlResult);

        var result = await CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "email" }), Ctx());

        result.Columns.Single(c => c.Name == "email").Masked.ShouldBeTrue();
        result.Data[0]["email"].ShouldBe(alreadyMaskedBySql);
        _maskingProvider.DidNotReceive().MaskValue(Arg.Any<string>(), Arg.Any<object?>(), Arg.Any<MaskingRule>());
    }

    [Fact]
    public async Task ExecuteQueryAsync_FallbackInMemoryMasking_ScopesHmacRuleToTenant()
    {
        var baseMetadata = CreateCustomerTableMetadata();
        var metadata = new TableMetadata
        {
            Identifier = baseMetadata.Identifier,
            Table = baseMetadata.Table,
            Columns = baseMetadata.Columns,
            PrimaryKeyColumns = baseMetadata.PrimaryKeyColumns,
            ColumnMaskingRules = new Dictionary<string, MaskingRule>(StringComparer.OrdinalIgnoreCase)
            {
                ["email"] = new MaskingRule { RuleType = "HMAC" }
            }
        };
        _metadataRepo.GetTableMetadataAsync(_customerTable, Arg.Any<CancellationToken>()).Returns(metadata);
        _pdp.EvaluateAccessAsync(
                _customerTable,
                metadata,
                Arg.Any<Autheris.Domain.Security.SecurityPrincipalContext>(),
                Arg.Any<IReadOnlyList<string>?>(),
                Arg.Any<CancellationToken>())
            .Returns(TableAccessDecision.Allowed(
                _customerTable,
                new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
                {
                    ["id"] = ColumnAccessLevel.Clear,
                    ["email"] = ColumnAccessLevel.Mask
                },
                hasUnconstrainedColumnAllow: true));

        var sqlResult = new GovernedSqlResult(
            OriginalSql: "SELECT id, email FROM sales.dbo.customers LIMIT 51",
            Columns: new[] { "id", "email" },
            Rows: new IReadOnlyDictionary<string, object?>[]
            {
                new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["id"] = 1, ["email"] = "alice@example.com" }
            },
            RowCount: 1,
            ElapsedMilliseconds: 1,
            InDbColumnMaskingExecuted: false);

        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(sqlResult);

        MaskingRule? capturedRule = null;
        _maskingProvider.MaskValue("email", "alice@example.com", Arg.Do<MaskingRule>(r => capturedRule = r))
            .Returns("hmac-masked");

        var result = await CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "email" }), Ctx());

        result.Data[0]["email"].ShouldBe("hmac-masked");
        capturedRule.ShouldNotBeNull();
        capturedRule.RuleType.ShouldBe("HMAC_SHA256");
        capturedRule.HmacKeyId.ShouldBe("key-2026-q1|tenant:default");
    }

    private TableMetadata UnorderableUnkeyed(DataSourceType type = DataSourceType.Sql) =>
        CreateCustomerTableMetadata(type, Array.Empty<string>()) with
        {
            Columns = new List<TableColumn>
            {
                new() { ColumnName = "id", DataType = "xml" },
                new() { ColumnName = "name", DataType = "xml" }
            }
        };

    [Fact]
    public async Task ExecuteQueryAsync_CrossSourceUnkeyedTable_PlansStableOrderBeforeTheInMemorySkip()
    {
        AllowAll(CreateCustomerTableMetadata(DataSourceType.HttpDeclarative, Array.Empty<string>()));
        GovernedSqlQueryRequest? captured = null;
        _federatedExecutionService.ExecuteQueryBufferedAsync(
                Arg.Do<GovernedSqlQueryRequest>(r => captured = r),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(61));

        await CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 50, Offset: 10, Transport: "Rest"), Ctx());

        captured.ShouldNotBeNull();
        captured.Sql.ShouldContain("ORDER BY \"id\", \"email\", \"name\" LIMIT 61");
        captured.Sql.ShouldNotContain("OFFSET", Case.Insensitive);
    }

    [Theory]
    [InlineData(DataSourceType.Sql)]
    [InlineData(DataSourceType.HttpDeclarative)]
    public async Task ExecuteQueryAsync_NoOrderableColumnAndOffset_Rejects400WithoutColumnNames(DataSourceType type)
    {
        AllowAll(UnorderableUnkeyed(type));

        var ex = await Should.ThrowAsync<ArgumentException>(() => CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id" }, Limit: 5, Offset: 5), Ctx()));

        ex.Message.ShouldContain("no orderable column readable in clear text");
        ex.Message.ShouldNotContain("\"id\"");
    }

    [Fact]
    public async Task ExecuteQueryAsync_NoOrderableColumnAndMoreRowsThanThePage_Rejects400()
    {
        AllowAll(UnorderableUnkeyed());
        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(6));

        var ex = await Should.ThrowAsync<ArgumentException>(() => CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id" }, Limit: 5, Offset: 0), Ctx()));

        ex.Message.ShouldContain("no orderable column readable in clear text");
    }

    [Fact]
    public async Task ExecuteQueryAsync_NoOrderableColumnAndFullPageWithoutMoreRows_Succeeds()
    {
        AllowAll(UnorderableUnkeyed());
        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(5));

        var result = await CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id" }, Limit: 5, Offset: 0), Ctx());

        result.HasMore.ShouldBeFalse();
        result.Count.ShouldBe(5);
    }

    [Fact]
    public async Task ExecuteQueryAsync_UnavailableMetadata_IsNotFound_NeverReadsOrLeaksDetail()
    {
        _metadataRepo.GetTableMetadataAsync(_customerTable, Arg.Any<CancellationToken>())
            .Returns<TableMetadata?>(_ => throw new Autheris.Domain.Exceptions.TableMetadataUnavailableException(_customerTable));

        await Should.ThrowAsync<Autheris.Domain.Exceptions.TableNotFoundException>(() => CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id" }, Limit: 5), Ctx()));

        await _sqlExecutionService.DidNotReceive().ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>());
    }
}
