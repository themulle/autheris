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

    private TableMetadata CreateCustomerTableMetadata()
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
                DataSourceType = DataSourceType.Sql,
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
}
