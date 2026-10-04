namespace Autheris.Tests.Unit.Kernel;

using System;
using System.Collections.Generic;
using System.Security;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Interfaces;
using Autheris.Application.Kernel;
using Autheris.Application.Policy;
using Autheris.Domain.Common;
using Autheris.Domain.Exceptions;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Kernel;
using Autheris.Domain.Model;
using Autheris.Domain.Security;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

public sealed class GovernedExecutionKernelTests
{
    private readonly ITableMetadataRepository _metadataRepo = Substitute.For<ITableMetadataRepository>();
    private readonly IUnifiedPolicyDecisionPoint _pdp = Substitute.For<IUnifiedPolicyDecisionPoint>();
    private readonly IExecutionGuardrailService _guardrailService = new ExecutionGuardrailService();
    private readonly IColumnMaskingProvider _maskingProvider = Substitute.For<IColumnMaskingProvider>();
    private readonly IGatewayExecutionService _gatewayExecutionService = Substitute.For<IGatewayExecutionService>();

    private GovernedExecutionKernel CreateKernel() => new(
        _metadataRepo,
        _pdp,
        _guardrailService,
        _maskingProvider,
        _gatewayExecutionService,
        NullLogger<GovernedExecutionKernel>.Instance);

    private static SecurityPrincipalContext CreateTestSecurityContext(string tenant = "tenant-1", string user = "user-1") => new()
    {
        UserSid = new Sid(user),
        TenantId = new TenantId(tenant),
        GroupSids = new HashSet<Sid>(),
        TenantRoles = new HashSet<string> { "DataViewer" },
        ClusterRoles = new HashSet<string>(),
        AuthenticationScheme = "Bearer",
        IsAuthenticated = true
    };

    [Fact]
    public void TableIdentifierNormalizer_SinglePart_ExpandsDefaults()
    {
        var id = TableIdentifierNormalizer.Normalize("customers", defaultDomain: "sales", defaultSchema: "public", dialect: DatabaseDialect.PostgreSql);
        id.Domain.ShouldBe("sales");
        id.Schema.ShouldBe("public");
        id.TableName.ShouldBe("customers");
    }

    [Fact]
    public void TableIdentifierNormalizer_CaseFolding_PostgresLower_OracleUpper()
    {
        var pg = TableIdentifierNormalizer.Normalize("Sales.Public.Orders", dialect: DatabaseDialect.PostgreSql);
        pg.Domain.ShouldBe("sales");
        pg.Schema.ShouldBe("public");
        pg.TableName.ShouldBe("orders");

        var ora = TableIdentifierNormalizer.Normalize("sales.public.orders", dialect: DatabaseDialect.Oracle);
        ora.Domain.ShouldBe("SALES");
        ora.Schema.ShouldBe("PUBLIC");
        ora.TableName.ShouldBe("ORDERS");
    }

    [Fact]
    public void ExecutionGuardrail_RejectsMultiStatement_AllowsSemicolonInLiteral()
    {
        var guardrail = new ExecutionGuardrailService();

        // Single statement with semicolon inside string literal is allowed
        Should.NotThrow(() => guardrail.ValidateSingleStatement("SELECT * FROM users WHERE note = 'hello; world'"));

        // Multiple statements are rejected
        var ex = Should.Throw<ArgumentException>(() =>
            guardrail.ValidateSingleStatement("SELECT * FROM users; DROP TABLE logs;"));
        ex.Message.ShouldContain("Multiple SQL statements detected");
    }

    [Fact]
    public async Task Kernel_UnknownTable_ThrowsTableNotFoundException()
    {
        var kernel = CreateKernel();
        var secCtx = CreateTestSecurityContext();
        var req = new GovernedExecutionRequest(
            new TableIdentifier("default", "public", "ghost_table"),
            RequestedColumns: null,
            SqlPredicate: null,
            RequestedLimit: 100,
            ExecutionEngineType.RelationalSql);

        _metadataRepo.GetTableMetadataAsync(Arg.Any<TableIdentifier>(), Arg.Any<CancellationToken>())
            .Returns((TableMetadata?)null);
        _metadataRepo.GetAllTablesAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<TableMetadata>());

        await Should.ThrowAsync<TableNotFoundException>(() => kernel.ExecuteAsync(req, secCtx, CancellationToken.None));
    }

    [Fact]
    public async Task Kernel_PdpDenied_ThrowsSecurityException()
    {
        var kernel = CreateKernel();
        var secCtx = CreateTestSecurityContext();
        var table = new TableIdentifier("sales", "public", "orders");
        var meta = new TableMetadata
        {
            Identifier = table,
            Columns = new[] { new TableColumn { ColumnName = "id", DataType = "int" } }
        };

        _metadataRepo.GetTableMetadataAsync(table, Arg.Any<CancellationToken>()).Returns(meta);
        _pdp.EvaluateAccessAsync(table, meta, secCtx, Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(TableAccessDecision.Denied(table, "Zero Trust Denied"));

        var req = new GovernedExecutionRequest(table, null, null, 100, ExecutionEngineType.RelationalSql);

        var ex = await Should.ThrowAsync<SecurityException>(() => kernel.ExecuteAsync(req, secCtx, CancellationToken.None));
        ex.Message.ShouldContain("Zero Trust Denied");
    }

    [Fact]
    public async Task Kernel_AuthorizedAndMasked_AppliesMaskingProvider()
    {
        var kernel = CreateKernel();
        var secCtx = CreateTestSecurityContext();
        var table = new TableIdentifier("hr", "public", "employees");
        var meta = new TableMetadata
        {
            Identifier = table,
            Columns = new[]
            {
                new TableColumn { ColumnName = "id", DataType = "int" },
                new TableColumn { ColumnName = "salary", DataType = "decimal", IsSensitive = true }
            },
            ColumnMaskingRules = new Dictionary<string, MaskingRule>
            {
                ["salary"] = new MaskingRule { RuleType = "REDACT", Replacement = "[CONFIDENTIAL]" }
            }
        };

        _metadataRepo.GetTableMetadataAsync(table, Arg.Any<CancellationToken>()).Returns(meta);

        // PDP allows table with Mask on salary
        var decision = TableAccessDecision.Allowed(
            table,
            new Dictionary<string, ColumnAccessLevel>
            {
                ["id"] = ColumnAccessLevel.Clear,
                ["salary"] = ColumnAccessLevel.Mask
            },
            rowFilterSql: null,
            hasUnconstrainedColumnAllow: false);

        _pdp.EvaluateAccessAsync(table, meta, secCtx, Arg.Any<IReadOnlyList<string>?>(), Arg.Any<CancellationToken>())
            .Returns(decision);

        // Gateway returns 1 raw row
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rawRows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["id"] = 101, ["salary"] = 95000m }
        };

        _gatewayExecutionService.ExecuteTableQueryAsync(
            Arg.Any<ClaimsPrincipal?>(),
            table,
            Arg.Any<int?>(),
            Arg.Any<int?>(),
            Arg.Any<IReadOnlyDictionary<string, object?>?>(),
            Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string[]>?>(),
            Arg.Any<CancellationToken>())
            .Returns((rawRows, decision));

        _maskingProvider.MaskValue("salary", 95000m, Arg.Any<MaskingRule>())
            .Returns("[CONFIDENTIAL]");

        var req = new GovernedExecutionRequest(table, null, null, 100, ExecutionEngineType.RelationalSql);
        var result = await kernel.ExecuteAsync(req, secCtx, CancellationToken.None);

        result.ShouldNotBeNull();
        result.Rows.Count.ShouldBe(1);
        result.Rows[0]["id"].ShouldBe(101);
        result.Rows[0]["salary"].ShouldBe("[CONFIDENTIAL]");
    }
}
