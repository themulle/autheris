namespace Autheris.Tests.Unit.Security;

using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.DataSources.Connectors;
using Autheris.Application.DataSources.Connectors.Adapters;
using Autheris.Application.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Connectors;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Connectors;
using Autheris.Tests.Common.Fakes;
using Autheris.Tests.Unit.TestSupport;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class ConnectorSecurityAttestationTests
{
    private static readonly TableIdentifier OrdersId = new("crm", "public", "orders");
    private static readonly TenantId Tenant = new("tenant-a");

    private static TableMetadata CreateMetadata(DataSourceType type) => new()
    {
        Identifier = OrdersId,
        ColumnClassifications = TableMetadataClassificationTestExtensions.PiiFor("email"),
        Table = new Table
        {
            SourceName = "crm",
            SchemaName = "public",
            TableName = "orders",
            DataSourceType = type,
            SourceType = type == DataSourceType.HttpDeclarative ? "Http" : "PostgreSql",
            IsActive = true
        },
        Columns =
        [
            new TableColumn { ColumnName = "id", DataType = "int" },
            new TableColumn { ColumnName = "region", DataType = "varchar" },
            new TableColumn { ColumnName = "email", DataType = "varchar" }
        ]
    };

    private static TableAccessDecision CreateDecision() => TableAccessDecision.Allowed(
        OrdersId,
        new Dictionary<string, ColumnAccessLevel>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = ColumnAccessLevel.Clear,
            ["region"] = ColumnAccessLevel.Clear,
            ["email"] = ColumnAccessLevel.Mask
        },
        rowFilterSql: "[region] = 'EU'",
        hasUnconstrainedColumnAllow: true);

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> SampleRows() =>
    [
        new Dictionary<string, object?> { ["id"] = 1, ["region"] = "EU", ["email"] = "a@eu.example" },
        new Dictionary<string, object?> { ["id"] = 2, ["region"] = "US", ["email"] = "b@us.example" }
    ];

    private static ConnectorSessionContext CreateSession(TableAccessDecision decision) => new(
        Principal: PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER"), new Claim("tenant_id", Tenant.Value)],
            "TestAuth"))),
        Tenant: Tenant,
        AccessDecision: decision,
        ProjectedColumns: ["id", "region", "email"],
        Arguments: new Dictionary<string, object?>());

    [Fact]
    public void ConnectorSecurityAttestation_DefaultsToAllFalse()
    {
        ConnectorSecurityAttestation def = default;
        def.RlsPushdownExecuted.ShouldBeFalse();
        def.InDbColumnMaskingExecuted.ShouldBeFalse();
    }

    [Fact]
    public async Task ReadAsync_WhenMayAttestIsFalse_IgnoresSelfAttestation_FailClosed()
    {
        var metadata = CreateMetadata(DataSourceType.HttpDeclarative);
        var maskingCalls = 0;
        var masking = new StubColumnMaskingProvider((_, _, _) =>
        {
            maskingCalls++;
            return "***";
        });
        var connector = new StubAutherisConnector(
            "http-crm",
            connectorType: "HttpDeclarative",
            capabilities: ConnectorCapabilities.DefaultHttp,
            readBatch: (_, session, _) =>
            {
                session.Attestation = new ConnectorSecurityAttestation(RlsPushdownExecuted: true, InDbColumnMaskingExecuted: true);
                return Task.FromResult(SampleRows());
            });

        var result = await GovernedConnectorReader.ReadAsync(
            connector,
            CreateSession(CreateDecision()),
            metadata,
            new GovernedRowPolicy(masking, HmacKeyId: null),
            CancellationToken.None);

        result.RlsPushdownExecuted.ShouldBeFalse();
        result.InDbMaskingExecuted.ShouldBeFalse();
        result.Rows.Count.ShouldBe(1);
        result.Rows[0]["region"].ShouldBe("EU");
        result.Rows[0]["email"].ShouldBe("***");
        maskingCalls.ShouldBe(1);
    }

    [Fact]
    public async Task ReadAsync_WhenMayAttestIsTrue_HonorsAttestation()
    {
        var metadata = CreateMetadata(DataSourceType.Sql);
        var maskingCalls = 0;
        var masking = new StubColumnMaskingProvider((_, _, _) =>
        {
            maskingCalls++;
            return "***";
        });
        var connector = new StubAutherisConnector(
            "sql-crm",
            connectorType: "Sql",
            capabilities: ConnectorCapabilities.DefaultSql,
            readBatch: (_, session, _) =>
            {
                session.Attestation = new ConnectorSecurityAttestation(RlsPushdownExecuted: true, InDbColumnMaskingExecuted: true);
                return Task.FromResult(SampleRows());
            });

        var result = await GovernedConnectorReader.ReadAsync(
            connector,
            CreateSession(CreateDecision()),
            metadata,
            new GovernedRowPolicy(masking, HmacKeyId: null),
            CancellationToken.None);

        result.RlsPushdownExecuted.ShouldBeTrue();
        result.InDbMaskingExecuted.ShouldBeTrue();
        result.Rows.Count.ShouldBe(2);
        result.Rows[0]["email"].ShouldBe("a@eu.example");
        maskingCalls.ShouldBe(0);
    }

    [Fact]
    public async Task StreamAsync_HonorsAttestationSetDuringReadBatch_AndIgnoresWhenMayAttestIsFalse()
    {
        var decision = CreateDecision();
        var metadata = CreateMetadata(DataSourceType.Sql);

        var attestingConnector = new StubAutherisConnector(
            "sql-crm",
            connectorType: "Sql",
            capabilities: ConnectorCapabilities.DefaultSql,
            readBatch: (_, session, _) =>
            {
                session.Attestation = new ConnectorSecurityAttestation(RlsPushdownExecuted: true, InDbColumnMaskingExecuted: true);
                return Task.FromResult(SampleRows());
            });

        var maskingCalls = 0;
        var masking = new StubColumnMaskingProvider((_, _, _) =>
        {
            maskingCalls++;
            return "***";
        });

        var sqlRows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var row in GovernedConnectorReader.StreamAsync(
            attestingConnector,
            CreateSession(decision),
            metadata,
            decision,
            new GovernedRowPolicy(masking, HmacKeyId: null),
            mayAttest: true))
        {
            sqlRows.Add(row);
        }

        sqlRows.Count.ShouldBe(2);
        sqlRows[0]["email"].ShouldBe("a@eu.example");
        maskingCalls.ShouldBe(0);

        var httpRows = new List<IReadOnlyDictionary<string, object?>>();
        await foreach (var row in GovernedConnectorReader.StreamAsync(
            attestingConnector,
            CreateSession(decision),
            CreateMetadata(DataSourceType.HttpDeclarative),
            decision,
            new GovernedRowPolicy(masking, HmacKeyId: null),
            mayAttest: false))
        {
            httpRows.Add(row);
        }

        httpRows.Count.ShouldBe(1);
        httpRows[0]["region"].ShouldBe("EU");
        httpRows[0]["email"].ShouldBe("***");
        maskingCalls.ShouldBe(1);
    }

    [Fact]
    public async Task SqlConnector_And_LegacyAdapter_PropagateAttestationBackToSession()
    {
        var metadata = CreateMetadata(DataSourceType.Sql);
        var decision = CreateDecision();

        var services = new ServiceCollection();
        services.AddScoped<IDataSourceExecutor>(_ => new AttestingSqlExecutor(SampleRows()));
        await using var provider = services.BuildServiceProvider();

        var sqlConnector = new SqlConnector(
            "sql-crm",
            new InMemoryTableMetadataRepository(metadata),
            StubColumnMaskingProvider.Asterisks,
            provider.GetRequiredService<IServiceScopeFactory>());

        var batchSession = CreateSession(decision);
        batchSession.Items["TableMetadata"] = metadata;
        await sqlConnector.RecordSource.ReadBatchAsync(ConnectorSplit.Default(), batchSession);
        batchSession.Attestation.RlsPushdownExecuted.ShouldBeTrue();
        batchSession.Attestation.InDbColumnMaskingExecuted.ShouldBeTrue();

        var streamSession = CreateSession(decision);
        streamSession.Items["TableMetadata"] = metadata;
        await foreach (var _ in sqlConnector.RecordSource.ReadSplitAsync(ConnectorSplit.Default(), streamSession))
        {
            streamSession.Attestation.RlsPushdownExecuted.ShouldBeTrue();
            streamSession.Attestation.InDbColumnMaskingExecuted.ShouldBeTrue();
        }

        var legacyAdapter = new LegacyDataSourceExecutorAdapter(new AttestingSqlExecutor(SampleRows()), "sql-legacy");
        var legacySession = CreateSession(decision);
        legacySession.Items["TableMetadata"] = metadata;
        await legacyAdapter.RecordSource.ReadBatchAsync(ConnectorSplit.Default(), legacySession);
        legacySession.Attestation.RlsPushdownExecuted.ShouldBeTrue();
        legacySession.Attestation.InDbColumnMaskingExecuted.ShouldBeTrue();
    }

    [Fact]
    public async Task GatewayExecutionService_WhenMayAttestIsFalse_IgnoresExecutorAttestation()
    {
        var metadata = CreateMetadata(DataSourceType.HttpDeclarative);
        var decision = CreateDecision();
        var maskingCalls = 0;
        var masking = new StubColumnMaskingProvider((_, _, _) =>
        {
            maskingCalls++;
            return "***";
        });

        var executor = new AttestingHttpExecutor(SampleRows());
        var cache = new InMemoryConsentCacheService();
        var service = new GatewayExecutionService(
            new InMemoryTableMetadataRepository(metadata),
            new InMemoryConsentRepository(),
            new InMemoryAuditLogRepository(),
            StubConsentResolutionService.Fixed(decision),
            cache,
            masking,
            options: Options.Create(new GatewayOptions()),
            dataSourceExecutors: [executor]);

        var user = PipelinePrincipals.MarkComplete(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.PrimarySid, "S-1-5-21-USER"),
            new Claim("tenant_id", Tenant.Value)
        ], "TestAuth")));

        var page = await service.ExecuteTablePageAsync(user, OrdersId, new TablePageRequest(10, 0));

        page.Rows.Count.ShouldBe(1);
        page.Rows[0]["region"].ShouldBe("EU");
        page.Rows[0]["email"].ShouldBe("***");
        maskingCalls.ShouldBe(1);
    }

    private sealed class AttestingSqlExecutor(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) : IDataSourceExecutor
    {
        public DataSourceType SupportedType => DataSourceType.Sql;

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(DataSourceExecutionContext context, CancellationToken ct = default)
        {
            context.Attestation = new ConnectorSecurityAttestation(RlsPushdownExecuted: true, InDbColumnMaskingExecuted: true);
            return Task.FromResult(rows);
        }
    }

    private sealed class AttestingHttpExecutor(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) : IDataSourceExecutor
    {
        public DataSourceType SupportedType => DataSourceType.HttpDeclarative;

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ExecuteAsync(DataSourceExecutionContext context, CancellationToken ct = default)
        {
            context.Attestation = new ConnectorSecurityAttestation(RlsPushdownExecuted: true, InDbColumnMaskingExecuted: true);
            return Task.FromResult(rows);
        }
    }
}
