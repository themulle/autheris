using System.Reflection;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Policy.Classification;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Tenancy;
using NSubstitute;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace Autheris.Tests.Unit.Governance.Classification;

[Trait("Category", "Security")]
public sealed class ClassificationMutationFlushTests
{
    private static readonly DateTimeOffset FixedTimestamp = new(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Restrictive_classification_or_mask_change_flushes_catalog_version_immediately_inside_write_scope()
    {
        var repo = new InMemoryClassificationRepository();
        var dirty = new CatalogDirtyFlag();
        var time = new FakeTimeProvider(FixedTimestamp);
        var factory = new CatalogWriteScopeFactory(repo, dirty, timeProvider: time);

        await using (factory.Begin("classification-admin"))
        {
            // Simulate prior deferred additive table writes inside the same ambient scope.
            var activeScope = CatalogWriteScopeContext.TryGetActive(repo).ShouldNotBeNull();
            await activeScope.RecordAdditiveWriteAsync(1, 0);
            repo.VersionFlushes.ShouldBe(0);

            await repo.AssignColumnClassificationAsync(new ColumnClassification
            {
                Scope = "*",
                TableColumnId = "col-email",
                Code = "PII",
                Source = "OWNER",
                AssignedBy = "owner-sid",
                AssignedAt = FixedTimestamp
            });

            // Both the restrictive mutation bump and the deferred additive scope delta flush immediately.
            repo.VersionFlushes.ShouldBe(2);
            dirty.IsDirty.ShouldBeFalse();

            await repo.UpsertColumnMaskOverrideAsync(new ColumnMaskOverride
            {
                Scope = "*",
                TableColumnId = "col-email",
                Code = "PII",
                MaskFunction = MaskFunction.Hash,
                KeyRef = "kv-pii",
                CatalogText = CatalogTextVisibility.Hidden,
                UpdatedBy = "owner-sid",
                UpdatedAt = FixedTimestamp
            });

            repo.VersionFlushes.ShouldBe(3);

            await repo.UpsertClassificationMaskRuleAsync(new ClassificationMaskRule
            {
                Scope = "*",
                Code = "FINANCIAL",
                MaskFunction = MaskFunction.Nullify,
                CatalogText = CatalogTextVisibility.Hidden,
                UpdatedBy = "cluster-admin",
                UpdatedAt = FixedTimestamp
            });

            repo.VersionFlushes.ShouldBe(4);
        }

        // Scope disposal does not leave an unflushed restrictive change.
        repo.VersionFlushes.ShouldBe(4);
    }

    [Fact]
    public async Task Failed_scope_flush_during_restrictive_classification_mutation_sets_dirty_flag()
    {
        var repo = new InMemoryClassificationRepository();
        var dirty = new CatalogDirtyFlag();
        var time = new FakeTimeProvider(FixedTimestamp);
        var factory = new CatalogWriteScopeFactory(repo, dirty, timeProvider: time);

        await using (factory.Begin("ingestion-then-restrictive"))
        {
            var activeScope = CatalogWriteScopeContext.TryGetActive(repo).ShouldNotBeNull();
            await activeScope.RecordAdditiveWriteAsync(1, 0);

            repo.ThrowOnScopeFlush = true;
            await repo.AssignColumnClassificationAsync(new ColumnClassification
            {
                Scope = "*",
                TableColumnId = "col-ssn",
                Code = "PII_SPECIAL",
                Source = "OWNER",
                AssignedBy = "owner-sid",
                AssignedAt = FixedTimestamp
            });

            dirty.IsDirty.ShouldBeTrue();
        }
    }

    [Fact]
    public async Task Admin_api_mutations_flush_immediately_and_hidden_catalog_text_reaches_the_search_index_resolver()
    {
        var repo = new InMemoryClassificationRepository();
        var columnId = Guid.NewGuid();
        var table = new TableIdentifier("crm", "public", "customers");
        repo.ColumnTables[columnId.ToString()] = table;
        var ownership = Substitute.For<IDataOwnershipRepository>();
        ownership.IsAuthorizedApproverForTableAsync(Arg.Any<TableIdentifier>(), Arg.Any<Sid>(), Arg.Any<CancellationToken>()).Returns(true);
        var service = new ClassificationAdminService(repo, ownership, null, new FakeTimeProvider(FixedTimestamp));
        var caller = new ClassificationAdminCaller(new TenantId("tenant-a"), "S-1-OWNER", IsClusterAdmin: false, IsGovernanceAdmin: false);
        var meta = new TableMetadata
        {
            Identifier = table,
            OwnerTenantId = "tenant-a",
            Table = new Table { Id = Guid.NewGuid(), SourceName = "crm", SchemaName = "public", TableName = "customers", IsActive = true },
            Columns = [new TableColumn { Id = columnId, ColumnName = "ssn", DataType = "varchar" }]
        };

        (await service.AssignColumnClassificationAsync(caller, columnId.ToString(), "PII", ClassificationAdminScope.Tenant)).IsOk.ShouldBeTrue();
        repo.VersionFlushes.ShouldBe(1);

        var before = await ClassificationAccessResolver.ResolveIndexingHiddenTextColumnsAsync(meta, repo);
        before.ShouldNotContain("ssn");

        var hidden = await service.UpsertMaskOverrideAsync(
            caller, columnId.ToString(), "PII", new MaskRuleInput(MaskFunction.Nullify, null, null, CatalogTextVisibility.Hidden), ClassificationAdminScope.Tenant);
        hidden.IsOk.ShouldBeTrue();
        repo.VersionFlushes.ShouldBe(2);

        // The index rebuild that the flushed catalog version triggers resolves the new HIDDEN override: the column text is not indexed.
        var after = await ClassificationAccessResolver.ResolveIndexingHiddenTextColumnsAsync(meta, repo);
        after.ShouldContain("ssn");

        var govAdmin = caller with { IsGovernanceAdmin = true };
        (await service.UpsertMaskRuleAsync(
            govAdmin, "FINANCIAL", new MaskRuleInput(MaskFunction.Nullify, null, null, CatalogTextVisibility.Hidden), ClassificationAdminScope.Tenant, null)).IsOk.ShouldBeTrue();
        repo.VersionFlushes.ShouldBe(3);

        (await service.RemoveColumnClassificationAsync(caller, columnId.ToString(), "PII", ClassificationAdminScope.Tenant)).IsOk.ShouldBeTrue();
        repo.VersionFlushes.ShouldBe(4);
    }

    [Fact]
    public void Repository_classifications_sql_constants_and_allowlist_cover_all_classification_tables()
    {
        Enum.GetValues<GovernanceTable>().Select(t => t.ToSqlName())
            .ShouldContain("CONSENT_CLASSIFICATION_ACCESS");

        var sqlHolder = typeof(GovernanceRepository).GetNestedType("ClassificationsSql", BindingFlags.NonPublic);
        sqlHolder.ShouldNotBeNull();

        var constants = sqlHolder!
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!)
            .ToList();

        constants.Count.ShouldBeGreaterThan(15);
        constants.ShouldAllBe(sql => !sql.Contains("SELECT *", StringComparison.OrdinalIgnoreCase));
        constants.Any(sql => sql.Contains("DATA_CLASSIFICATIONS", StringComparison.Ordinal)).ShouldBeTrue();
        constants.Any(sql => sql.Contains("CLASSIFICATION_MASK_RULES", StringComparison.Ordinal)).ShouldBeTrue();
        constants.Any(sql => sql.Contains("COLUMN_CLASSIFICATIONS", StringComparison.Ordinal)).ShouldBeTrue();
        constants.Any(sql => sql.Contains("COLUMN_MASK_OVERRIDES", StringComparison.Ordinal)).ShouldBeTrue();
    }
}

internal sealed class InMemoryClassificationRepository : IClassificationRepository, ICatalogVersionFlusher
{
    private readonly List<DataClassification> _classifications = [];
    private readonly List<ClassificationMaskRule> _rules = [];
    private readonly List<ColumnClassification> _columnClassifications = [];
    private readonly List<ColumnMaskOverride> _overrides = [];
    private int _flushes;

    public int VersionFlushes => Volatile.Read(ref _flushes);

    public bool ThrowOnScopeFlush { get; set; }

    public Dictionary<string, TableIdentifier> ColumnTables { get; } = new(StringComparer.Ordinal);

    public Task<TableIdentifier?> GetTableIdentifierByColumnIdAsync(string tableColumnId, CancellationToken ct = default) =>
        Task.FromResult<TableIdentifier?>(ColumnTables.TryGetValue(tableColumnId, out var table) ? table : null);

    public Task FlushCatalogVersionAsync(long structureDelta, long contentDelta, CancellationToken ct = default)
    {
        if (ThrowOnScopeFlush)
        {
            throw new InvalidOperationException("Simulated catalog flush outage.");
        }

        Interlocked.Increment(ref _flushes);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<DataClassification>> GetClassificationsAsync(
        string? tenantScope, bool includeInactive = false, CancellationToken ct = default)
    {
        var validTenant = tenantScope is null || ClassificationScopes.IsGlobal(tenantScope)
            ? null
            : ClassificationScopes.ValidateScope(tenantScope, allowGlobal: false);
        IReadOnlyList<DataClassification> result = _classifications
            .Where(c => (c.Scope == "*" || c.Scope == validTenant) && (includeInactive || c.IsActive))
            .OrderBy(c => c.RestrictionRank)
            .ThenBy(c => c.Code, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<DataClassification?> GetClassificationAsync(string scope, string code, CancellationToken ct = default) =>
        Task.FromResult(_classifications.FirstOrDefault(c => c.Scope == scope && c.Code == code));

    public async Task<DataClassification> UpsertClassificationAsync(DataClassification classification, CancellationToken ct = default)
    {
        _classifications.RemoveAll(c => c.Scope == classification.Scope && c.Code == classification.Code);
        _classifications.Add(classification);
        await FlushImmediatelyAsync(ct);
        return classification;
    }

    public Task<IReadOnlyList<ClassificationMaskRule>> GetClassificationMaskRulesAsync(string? tenantScope, CancellationToken ct = default)
    {
        var validTenant = tenantScope is null || ClassificationScopes.IsGlobal(tenantScope)
            ? null
            : ClassificationScopes.ValidateScope(tenantScope, allowGlobal: false);
        IReadOnlyList<ClassificationMaskRule> result = _rules
            .Where(r => r.Scope == "*" || r.Scope == validTenant)
            .ToList();
        return Task.FromResult(result);
    }

    public Task<ClassificationMaskRule?> GetClassificationMaskRuleAsync(string scope, string code, CancellationToken ct = default) =>
        Task.FromResult(_rules.FirstOrDefault(r => r.Scope == scope && r.Code == code));

    public async Task<ClassificationMaskRule> UpsertClassificationMaskRuleAsync(ClassificationMaskRule rule, CancellationToken ct = default)
    {
        _rules.RemoveAll(r => r.Scope == rule.Scope && r.Code == rule.Code);
        _rules.Add(rule);
        await FlushImmediatelyAsync(ct);
        return rule;
    }

    public Task<IReadOnlyList<ColumnClassification>> GetColumnClassificationsAsync(
        string tableColumnId, string? tenantScope = null, CancellationToken ct = default)
    {
        var validTenant = tenantScope is null || ClassificationScopes.IsGlobal(tenantScope)
            ? null
            : ClassificationScopes.ValidateScope(tenantScope, allowGlobal: false);
        IReadOnlyList<ColumnClassification> result = _columnClassifications
            .Where(c => c.TableColumnId == tableColumnId && (c.Scope == "*" || c.Scope == validTenant))
            .ToList();
        return Task.FromResult(result);
    }

    public async Task AssignColumnClassificationAsync(ColumnClassification assignment, CancellationToken ct = default)
    {
        _columnClassifications.RemoveAll(c =>
            c.Scope == assignment.Scope && c.TableColumnId == assignment.TableColumnId && c.Code == assignment.Code);
        _columnClassifications.Add(assignment);
        await FlushImmediatelyAsync(ct);
    }

    public async Task<bool> RemoveColumnClassificationAsync(string scope, string tableColumnId, string code, CancellationToken ct = default)
    {
        var removed = _columnClassifications.RemoveAll(c =>
            c.Scope == scope && c.TableColumnId == tableColumnId && c.Code == code) > 0;
        if (removed)
        {
            await FlushImmediatelyAsync(ct);
        }

        return removed;
    }

    public Task<IReadOnlyList<ColumnMaskOverride>> GetColumnMaskOverridesAsync(
        string tableColumnId, string? tenantScope = null, CancellationToken ct = default)
    {
        var validTenant = tenantScope is null || ClassificationScopes.IsGlobal(tenantScope)
            ? null
            : ClassificationScopes.ValidateScope(tenantScope, allowGlobal: false);
        IReadOnlyList<ColumnMaskOverride> result = _overrides
            .Where(o => o.TableColumnId == tableColumnId && (o.Scope == "*" || o.Scope == validTenant))
            .ToList();
        return Task.FromResult(result);
    }

    public async Task<ColumnMaskOverride> UpsertColumnMaskOverrideAsync(ColumnMaskOverride maskOverride, CancellationToken ct = default)
    {
        _overrides.RemoveAll(o =>
            o.Scope == maskOverride.Scope && o.TableColumnId == maskOverride.TableColumnId && o.Code == maskOverride.Code);
        _overrides.Add(maskOverride);
        await FlushImmediatelyAsync(ct);
        return maskOverride;
    }

    public async Task<bool> DeleteColumnMaskOverrideAsync(string scope, string tableColumnId, string? code, CancellationToken ct = default)
    {
        var removed = _overrides.RemoveAll(o =>
            o.Scope == scope && o.TableColumnId == tableColumnId && o.Code == code) > 0;
        if (removed)
        {
            await FlushImmediatelyAsync(ct);
        }

        return removed;
    }

    public Task<IReadOnlyList<ConsentClassificationAccess>> GetConsentClassificationAccessAsync(
        string tenantId, Guid consentId, CancellationToken ct = default)
    {
        IReadOnlyList<ConsentClassificationAccess> rows = _consentAccess
            .Where(x => x.TenantId == tenantId && x.ConsentId == consentId)
            .OrderBy(x => x.Code, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(rows);
    }

    public Task<IReadOnlyList<ConsentClassificationAccess>> GetConsentClassificationAccessBatchAsync(
        string tenantId, IReadOnlyList<Guid> consentIds, CancellationToken ct = default)
    {
        var idSet = consentIds.ToHashSet();
        IReadOnlyList<ConsentClassificationAccess> rows = _consentAccess
            .Where(x => x.TenantId == tenantId && idSet.Contains(x.ConsentId))
            .OrderBy(x => x.ConsentId)
            .ThenBy(x => x.Code, StringComparer.Ordinal)
            .ToList();
        return Task.FromResult(rows);
    }

    public async Task<ConsentClassificationAccess> UpsertConsentClassificationAccessAsync(
        ConsentClassificationAccess entry, CancellationToken ct = default)
    {
        _consentAccess.RemoveAll(x => x.TenantId == entry.TenantId && x.ConsentId == entry.ConsentId && x.Code == entry.Code);
        _consentAccess.Add(entry);
        await FlushImmediatelyAsync(ct);
        return entry;
    }

    private readonly List<ConsentClassificationAccess> _consentAccess = [];

    private async Task FlushImmediatelyAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _flushes);
        var scope = CatalogWriteScopeContext.TryGetActive(this);
        if (scope is not null)
        {
            await scope.FlushPendingAsync(ct).ConfigureAwait(false);
        }
    }
}
