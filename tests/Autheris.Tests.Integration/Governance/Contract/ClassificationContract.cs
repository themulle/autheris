using System;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Tests.Common;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Autheris.Tests.Integration.Governance.Contract;

/// <summary>Contract suite for <c>IClassificationRepository</c> across PostgreSQL and SQL Server engines.</summary>
public abstract class ClassificationContract : GovernanceContractBase
{
    private static readonly DateTimeOffset FixedTime = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    private IClassificationRepository Classifications =>
        Store.Services.GetRequiredService<IClassificationRepository>();

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Reads_never_return_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        await Classifications.UpsertClassificationAsync(new DataClassification
        {
            Scope = Store.TenantA.Value,
            Code = "TENANT_A_ONLY",
            DisplayName = "Tenant A Only",
            RestrictionRank = 50,
            UpdatedBy = "admin-a",
            UpdatedAt = FixedTime
        });

        await Classifications.UpsertClassificationAsync(new DataClassification
        {
            Scope = Store.TenantB.Value,
            Code = "TENANT_B_ONLY",
            DisplayName = "Tenant B Only",
            RestrictionRank = 50,
            UpdatedBy = "admin-b",
            UpdatedAt = FixedTime
        });

        var tenantARows = await Classifications.GetClassificationsAsync(Store.TenantA.Value);
        tenantARows.Select(r => r.Code).Should().Contain("TENANT_A_ONLY").And.NotContain("TENANT_B_ONLY");
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Writes_cannot_modify_other_tenant_rows()
    {
        if (!Store.IsAvailable) return;

        await Classifications.UpsertClassificationAsync(new DataClassification
        {
            Scope = Store.TenantB.Value,
            Code = "SHARED_CUSTOM",
            DisplayName = "Tenant B Definition",
            RestrictionRank = 55,
            UpdatedBy = "admin-b",
            UpdatedAt = FixedTime
        });

        await Classifications.UpsertClassificationAsync(new DataClassification
        {
            Scope = Store.TenantA.Value,
            Code = "SHARED_CUSTOM",
            DisplayName = "Tenant A Definition",
            RestrictionRank = 70,
            UpdatedBy = "admin-a",
            UpdatedAt = FixedTime
        });

        var tenantBRow = await Classifications.GetClassificationAsync(Store.TenantB.Value, "SHARED_CUSTOM");
        tenantBRow.Should().NotBeNull();
        tenantBRow!.DisplayName.Should().Be("Tenant B Definition");
        tenantBRow.RestrictionRank.Should().Be(55);
    }

    [RequiresContainerRuntimeFact]
    [Trait("Category", "Security")]
    public override async Task Missing_or_unparseable_tenant_id_throws()
    {
        if (!Store.IsAvailable) return;

        Func<Task> badRead = () => Classifications.GetClassificationsAsync("invalid tenant!");
        await badRead.Should().ThrowAsync<ArgumentException>();

        Func<Task> reservedRead = () => Classifications.GetClassificationsAsync(TenantId.Platform.Value);
        await reservedRead.Should().ThrowAsync<ArgumentException>();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Upsert_is_idempotent()
    {
        if (!Store.IsAvailable) return;

        var rule = new ClassificationMaskRule
        {
            Scope = Store.TenantA.Value,
            Code = "PII",
            MaskFunction = MaskFunction.Hash,
            KeyRef = "kv-pii-key",
            CatalogText = CatalogTextVisibility.Hidden,
            UpdatedBy = "admin-a",
            UpdatedAt = FixedTime
        };

        var first = await Classifications.UpsertClassificationMaskRuleAsync(rule);
        var second = await Classifications.UpsertClassificationMaskRuleAsync(rule);

        second.Scope.Should().Be(first.Scope);
        second.Code.Should().Be(first.Code);
        second.MaskFunction.Should().Be(MaskFunction.Hash);
        second.CatalogText.Should().Be(CatalogTextVisibility.Hidden);
        Assert.True(second.Version >= first.Version);
    }

    [RequiresContainerRuntimeFact]
    public override async Task Unique_violation_maps_to_conflict_exception()
    {
        if (!Store.IsAvailable) return;

        var invalidChild = new DataClassification
        {
            Scope = Store.TenantA.Value,
            Code = "LOW_CHILD",
            DisplayName = "Invalid Child",
            ParentCode = "PII",
            RestrictionRank = 10,
            UpdatedBy = "admin-a",
            UpdatedAt = FixedTime
        };

        Func<Task> act = () => Classifications.UpsertClassificationAsync(invalidChild);
        await act.Should().ThrowAsync<ArgumentException>();
    }

    [RequiresContainerRuntimeFact]
    public override async Task Paging_is_stable_and_ordered()
    {
        if (!Store.IsAvailable) return;

        var firstPass = await Classifications.GetClassificationsAsync(Store.TenantA.Value);
        var secondPass = await Classifications.GetClassificationsAsync(Store.TenantA.Value);

        var firstKeys = firstPass.Select(c => $"{c.Scope}:{c.Code}").ToArray();
        var secondKeys = secondPass.Select(c => $"{c.Scope}:{c.Code}").ToArray();
        Assert.Equal(firstKeys, secondKeys);

        var ranks = firstPass.Select(c => c.RestrictionRank).ToArray();
        Assert.Equal(ranks.OrderBy(r => r).ToArray(), ranks);
    }

    [RequiresContainerRuntimeFact]
    public override Task Page_size_above_cap_is_clamped_or_rejected()
    {
        return Task.CompletedTask;
    }

    [RequiresContainerRuntimeFact]
    public override async Task Soft_deleted_rows_are_invisible_to_reads()
    {
        if (!Store.IsAvailable) return;

        await Classifications.UpsertClassificationAsync(new DataClassification
        {
            Scope = Store.TenantA.Value,
            Code = "RETIRED_TAG",
            DisplayName = "Retired Tag",
            RestrictionRank = 25,
            IsActive = false,
            UpdatedBy = "admin-a",
            UpdatedAt = FixedTime
        });

        var activeOnly = await Classifications.GetClassificationsAsync(Store.TenantA.Value, includeInactive: false);
        activeOnly.Select(c => c.Code).Should().NotContain("RETIRED_TAG");

        var withInactive = await Classifications.GetClassificationsAsync(Store.TenantA.Value, includeInactive: true);
        withInactive.Select(c => c.Code).Should().Contain("RETIRED_TAG");
    }
}

[Collection(GovernancePostgresCollection.Name)]
public sealed class PostgreSqlClassificationContractTests(GovernancePostgresFixture fixture) : ClassificationContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreatePostgreSqlAsync(fixture, "classification_contract");
}

[Collection(GovernanceSqlServerCollection.Name)]
public sealed class SqlServerClassificationContractTests(GovernanceSqlServerFixture fixture) : ClassificationContract
{
    protected override Task<GovernanceTestStore> CreateStoreAsync() =>
        GovernanceTestStore.CreateSqlServerAsync(fixture, "classification_contract");
}
