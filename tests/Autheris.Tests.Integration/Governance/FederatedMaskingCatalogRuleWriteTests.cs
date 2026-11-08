namespace Autheris.Tests.Integration.Governance;

using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Federation.Services;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;
using Xunit;

/// <summary>GHSA-7pjw-hvff regression: a masking rule written through the real governance repository invalidates the federated catalog cache.</summary>
[Trait("Category", "Security")]
public sealed class FederatedMaskingCatalogRuleWriteTests : IDisposable
{
    private static readonly TenantId TenantA = new("tenant-a");

    private readonly PostgreSqlGovernanceRepositoryHandle _handle = new();

    public void Dispose() => _handle.Repository.Dispose();

    [Fact]
    public async Task Ghsa_7pjw_hvff_regression()
    {
        var real = _handle.Repository;
        var tableId = Guid.NewGuid();
        TableMetadata Stored(string ruleType) => new()
        {
            Identifier = new TableIdentifier("src_a", "dbo", "orders"),
            Table = new Table { Id = tableId, SourceName = "src_a", SchemaName = "dbo", TableName = "orders", DisplayName = "orders", IsActive = true },
            OwnerTenantId = "tenant-a",
            Columns = [new TableColumn { ColumnName = "pin", DataType = "TEXT" }],
            ColumnMaskingRules = new Dictionary<string, MaskingRule> { ["pin"] = new MaskingRule { RuleType = ruleType } }
        };

        var live = new CatalogFederatedMaskingCatalog(real, Options.Create(new GatewayOptions()));
        await real.UpsertTableMetadataAsync(Stored("MASK_EMAIL"));
        (await live.GetClassificationsAsync(TenantA))["pin"].RuleType.ShouldBe("MASK_EMAIL");
        await real.UpsertTableMetadataAsync(Stored("NULLIFY"));
        (await live.GetClassificationsAsync(TenantA))["pin"].RuleType.ShouldBe("NULLIFY");
    }

    private sealed class PostgreSqlGovernanceRepositoryHandle
    {
        public Autheris.Infrastructure.Persistence.PostgreSqlGovernanceRepository Repository { get; } =
            GovernanceTestDatabase.CreateRepository(Substitute.For<IEpochValidationService>());
    }
}
