namespace Autheris.Tests.Unit.Governance.Classification;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Policy.Classification;
using Autheris.Application.Sql.Masking;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #1043: a batch of access decisions reads the persisted column classifications and mask overrides once
/// (<see cref="ClassificationAccessResolver.PrefetchColumnRowsAsync"/>). The prefetch must give the same classification outcome as the
/// per-table reads, and must only be used for the tenant and the columns it was read for (otherwise the resolver reads as before).
/// </summary>
[Trait("Category", "Security")]
public sealed class ColumnPolicyRowPrefetchTests
{
    private static readonly TenantId Tenant = new("tenant-prefetch");
    private static readonly DateTimeOffset FixedNow = new(2026, 11, 23, 8, 0, 0, TimeSpan.Zero);
    private static readonly TableIdentifier People = new("hr", "dbo", "people");

    private static readonly Guid IdColumn = Guid.NewGuid();
    private static readonly Guid SsnColumn = Guid.NewGuid();
    private static readonly Guid MailColumn = Guid.NewGuid();

    private static TableMetadata Metadata() => new()
    {
        Identifier = People,
        Table = new Table { Id = Guid.NewGuid(), SourceName = "hr", SchemaName = "dbo", TableName = "people", SourceType = "PostgreSql", IsActive = true },
        Columns =
        [
            new TableColumn { Id = IdColumn, ColumnName = "id", DataType = "INTEGER" },
            new TableColumn { Id = SsnColumn, ColumnName = "ssn", DataType = "TEXT" },
            new TableColumn { Id = MailColumn, ColumnName = "mail", DataType = "TEXT" }
        ]
    };

    private static Consent Grant() => new()
    {
        Id = Guid.NewGuid(),
        TenantId = Tenant,
        TableIdentifier = People,
        GranteeType = GranteeType.User,
        GranteeSid = new Sid("S-1-5-21-1-1-1-1001"),
        Effect = ConsentEffect.Allow,
        ValidFrom = FixedNow.AddDays(-1),
        ValidTo = FixedNow.AddDays(1),
        ColumnRules =
        [
            new ConsentColumnRule { ColumnName = "id", AccessLevel = ColumnAccessLevel.Clear },
            new ConsentColumnRule { ColumnName = "ssn", AccessLevel = ColumnAccessLevel.Clear },
            new ConsentColumnRule { ColumnName = "mail", AccessLevel = ColumnAccessLevel.Clear }
        ]
    };

    private static TestInMemoryClassificationStore Store()
    {
        var store = new TestInMemoryClassificationStore(Tenant.Value);
        store.ColumnClassifications.Add(new ColumnClassification { TableColumnId = SsnColumn.ToString(), Scope = ClassificationScopes.Global, Code = ClassificationScopes.PiiCode, Source = "manual", AssignedBy = "owner", AssignedAt = FixedNow });
        store.ColumnClassifications.Add(new ColumnClassification { TableColumnId = MailColumn.ToString(), Scope = Tenant.Value, Code = ClassificationScopes.ConfidentialCode, Source = "manual", AssignedBy = "owner", AssignedAt = FixedNow });
        store.ColumnMaskOverrides.Add(new ColumnMaskOverride { TableColumnId = SsnColumn.ToString(), Scope = ClassificationScopes.Global, MaskFunction = MaskFunction.Nullify, UpdatedBy = "owner", UpdatedAt = FixedNow });
        return store;
    }

    private static ClassificationAccessResolver Resolver(TestInMemoryClassificationStore store)
    {
        var time = new AdjustableClassificationTimeProvider(FixedNow);
        var maskProvider = new SqlDataMaskingProvider(Options.Create(new GatewayOptions()), timeProvider: time);
        return new ClassificationAccessResolver(maskProvider, store, time);
    }

    private static ColumnPolicyRowPrefetch Empty(string tenant, params Guid[] columns) => new(
        tenant,
        FixedNow,
        columns.Select(static c => c.ToString()).ToList(),
        new Dictionary<string, IReadOnlyList<ColumnClassification>>(),
        new Dictionary<string, IReadOnlyList<ColumnMaskOverride>>());

    private static string Describe(IReadOnlyDictionary<string, ColumnClassificationGrantOutcome> combined) =>
        string.Join(";", combined.OrderBy(static kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static kv => $"{kv.Key}={kv.Value.WinningClassificationCode}/{kv.Value.WinningMaskFunction}/{kv.Value.MaskSpec?.Kind}"));

    [Fact]
    public async Task Prefetch_GivesTheSameOutcome_AsThePerTableReads()
    {
        var store = Store();
        var resolver = Resolver(store);
        var metadata = Metadata();
        var grant = Grant();

        var (_, direct) = await resolver.ResolveForTableAsync(metadata, [grant], Tenant, null);
        var prefetch = await resolver.PrefetchColumnRowsAsync([metadata], Tenant.Value);
        prefetch.ShouldNotBeNull();
        var (_, viaPrefetch) = await resolver.ResolveForTableAsync(metadata, [grant], Tenant, null, columnRowPrefetch: prefetch);

        Describe(viaPrefetch).ShouldBe(Describe(direct));
        direct.ShouldContainKey("ssn");
        direct.ShouldContainKey("mail");
    }

    [Fact]
    public async Task CoveringPrefetch_ReplacesTheRepositoryReads()
    {
        var store = Store();
        var resolver = Resolver(store);
        var metadata = Metadata();
        var prefetch = await resolver.PrefetchColumnRowsAsync([metadata], Tenant.Value);

        // Rows written after the prefetch are not seen by decisions of the same batch: the prefetch is the source.
        store.ColumnClassifications.Add(new ColumnClassification { TableColumnId = IdColumn.ToString(), Scope = ClassificationScopes.Global, Code = ClassificationScopes.FinancialCode, Source = "manual", AssignedBy = "owner", AssignedAt = FixedNow });

        var (_, viaPrefetch) = await resolver.ResolveForTableAsync(metadata, [Grant()], Tenant, null, columnRowPrefetch: prefetch);
        viaPrefetch.ShouldNotContainKey("id");

        var (_, direct) = await resolver.ResolveForTableAsync(metadata, [Grant()], Tenant, null);
        direct.ShouldContainKey("id");
    }

    [Fact]
    public async Task PrefetchOfAnotherTenant_IsNotUsed()
    {
        var resolver = Resolver(Store());
        var metadata = Metadata();

        // A prefetch of another tenant that has no rows: using it would drop the PII and CONFIDENTIAL classifications.
        var foreign = Empty("tenant-other", IdColumn, SsnColumn, MailColumn);
        foreign.Covers(Tenant.Value, [IdColumn.ToString()], FixedNow).ShouldBeFalse();

        var (_, combined) = await resolver.ResolveForTableAsync(metadata, [Grant()], Tenant, null, columnRowPrefetch: foreign);
        combined.ShouldContainKey("mail");
        combined["mail"].WinningClassificationCode.ShouldBe(ClassificationScopes.ConfidentialCode);
        combined.ShouldContainKey("ssn");
    }

    [Fact]
    public async Task PrefetchThatDoesNotCoverEveryColumn_IsNotUsed()
    {
        var resolver = Resolver(Store());
        var metadata = Metadata();
        // Covers only "id" and has no rows: using it for the table would drop the PII classification of "ssn".
        var prefetch = Empty(Tenant.Value, IdColumn);
        prefetch.Covers(Tenant.Value, [IdColumn.ToString(), SsnColumn.ToString()], FixedNow).ShouldBeFalse();

        var (_, combined) = await resolver.ResolveForTableAsync(metadata, [Grant()], Tenant, null, columnRowPrefetch: prefetch);
        combined.ShouldContainKey("ssn");
        combined["ssn"].WinningClassificationCode.ShouldBe(ClassificationScopes.PiiCode);
    }

    [Fact]
    public void StalePrefetch_IsNotUsed()
    {
        var prefetch = Empty(Tenant.Value, IdColumn);

        prefetch.Covers(Tenant.Value, [IdColumn.ToString()], FixedNow.AddSeconds(5)).ShouldBeTrue();
        prefetch.Covers(Tenant.Value, [IdColumn.ToString()], FixedNow.AddSeconds(6)).ShouldBeFalse();
        prefetch.Covers(Tenant.Value, [IdColumn.ToString()], FixedNow.AddSeconds(-1)).ShouldBeFalse();
    }
}
