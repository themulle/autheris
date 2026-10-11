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

    private TableMetadata CreateCustomerTableMetadata(DataSourceType sourceType = DataSourceType.Sql)
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
            PrimaryKeyColumns = new[] { "id" }
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
            .Returns(new GovernedSqlResult("SELECT id, email, name FROM sales.dbo.customers", "SELECT ...", new[] { "id", "email", "name" }, rows, 1, 10));

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

    private GovernedDataQueryService CreateService(GatewayOptions? options = null) =>
        new(_metadataRepo, _pdp, _sqlExecutionService, _federatedExecutionService, _maskingProvider,
            options == null ? null : Options.Create(options));

    private static RequestContext Ctx() =>
        new(new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("sub", "user-123") }, "TestAuth")), new TenantId("default"));

    private static GovernedSqlResult SqlResult(int rowCount, bool truncated = false) =>
        new("q", "q", new[] { "id", "name" }, Rows(rowCount), rowCount, 1, truncated);

    [Fact]
    public async Task ExecuteQueryAsync_WithOffset_EmitsNoOffsetAndLimitOffsetPlusLimitPlusOne_ThreePartName()
    {
        AllowAll(CreateCustomerTableMetadata());
        GovernedSqlQueryRequest? captured = null;
        _sqlExecutionService.ExecuteQueryBufferedAsync(
                Arg.Do<GovernedSqlQueryRequest>(r => captured = r),
                Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(61));

        var result = await CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 50, Offset: 10, Transport: "Rest"), Ctx());

        captured.ShouldNotBeNull();
        captured.Sql.ShouldNotContain("OFFSET", Case.Insensitive);
        captured.Sql.ShouldEndWith("LIMIT 61");
        captured.Sql.ShouldContain("FROM sales.dbo.customers");
        captured.Transport.ShouldBe("Rest");

        // 61 rows fetched, first 10 skipped: ids 11..60 returned and the 61st row proves HasMore.
        result.Count.ShouldBe(50);
        result.Data[0]["id"].ShouldBe(11);
        result.Data[49]["id"].ShouldBe(60);
        result.HasMore.ShouldBeTrue();
    }

    [Fact]
    public async Task ExecuteQueryAsync_WithOffset_LastPage_HasMoreFalse()
    {
        AllowAll(CreateCustomerTableMetadata());
        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(55));

        var result = await CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 50, Offset: 10), Ctx());

        result.Count.ShouldBe(45);
        result.Data[0]["id"].ShouldBe(11);
        result.HasMore.ShouldBeFalse();
    }

    [Fact]
    public async Task ExecuteQueryAsync_OffsetBeyondData_ReturnsEmptyPage()
    {
        AllowAll(CreateCustomerTableMetadata());
        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(5));

        var result = await CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 50, Offset: 100), Ctx());

        result.Count.ShouldBe(0);
        result.HasMore.ShouldBeFalse();
    }

    [Theory]
    [InlineData(949, 50, true)]   // window 1000 == effective cap
    [InlineData(950, 50, false)]  // window 1001 > cap
    [InlineData(980, 50, false)]  // the reviewed scenario: MaxAllowedRows=1000, offset=980, limit=50
    public async Task ExecuteQueryAsync_WindowCapFollowsWebSqlMaxAllowedRows(int offset, int limit, bool allowed)
    {
        AllowAll(CreateCustomerTableMetadata());
        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
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
            await _sqlExecutionService.DidNotReceive().ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>());
        }
    }

    [Theory]
    [InlineData(9949, 50, true)]
    [InlineData(9950, 50, false)]
    public async Task ExecuteQueryAsync_DefaultWindowCap_Is10000(int offset, int limit, bool allowed)
    {
        AllowAll(CreateCustomerTableMetadata());
        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
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
        // The SQL layer cut the result (Truncated) before the requested window was served.
        _sqlExecutionService.ExecuteQueryBufferedAsync(Arg.Any<GovernedSqlQueryRequest>(), Arg.Any<ClaimsPrincipal>(), Arg.Any<TenantId>(), Arg.Any<CancellationToken>())
            .Returns(SqlResult(1000, truncated: true));

        await Should.ThrowAsync<ArgumentException>(() => CreateService().ExecuteQueryAsync(
            new DatasetQueryRequest(_customerTable, SelectColumns: new[] { "id", "name" }, Limit: 50, Offset: 980), Ctx()));
    }
}
