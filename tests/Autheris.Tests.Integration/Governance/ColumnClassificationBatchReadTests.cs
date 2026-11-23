namespace Autheris.Tests.Integration.Governance;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Autheris.Application.Common.Interfaces;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;
using Xunit;

/// <summary>
/// Issue #1043: the set-based column classification and mask override reads return, per column, exactly the rows and order of the
/// per-column reads they replace (global scope, and global plus one tenant), so the sensitivity lookup and the classification
/// resolver keep their decisions.
/// </summary>
[Trait("Category", "Security")]
public sealed class ColumnClassificationBatchReadTests : IClassFixture<ColumnClassificationBatchReadTests.HostFixture>
{
    private const string TenantA = "tenant-batch-a";
    private const string TenantB = "tenant-batch-b";
    private readonly HostFixture _fixture;

    public ColumnClassificationBatchReadTests(HostFixture fixture) => _fixture = fixture;

    [Theory]
    [InlineData(null)]
    [InlineData(TenantA)]
    [InlineData(TenantB)]
    public async Task BatchReads_EqualThePerColumnReads(string? tenantScope)
    {
        var (repo, columnIds) = await _fixture.SeedAsync();

        var classifications = await repo.GetColumnClassificationsBatchAsync(columnIds, tenantScope);
        var overrides = await repo.GetColumnMaskOverridesBatchAsync(columnIds, tenantScope);

        foreach (var id in columnIds)
        {
            var single = await repo.GetColumnClassificationsAsync(id, tenantScope);
            var batch = classifications.TryGetValue(id, out var c) ? c : [];
            batch.Select(Key).ShouldBe(single.Select(Key), $"classifications of column {id} (scope {tenantScope ?? "*"})");

            var singleOverrides = await repo.GetColumnMaskOverridesAsync(id, tenantScope);
            var batchOverrides = overrides.TryGetValue(id, out var o) ? o : [];
            batchOverrides.Select(Key).ShouldBe(singleOverrides.Select(Key), $"overrides of column {id} (scope {tenantScope ?? "*"})");
        }

        // The seed has rows in both tenants: the tenant filter must not leak the other tenant's rows.
        if (tenantScope is not null)
        {
            classifications.Values.SelectMany(static l => l).ShouldAllBe(r => r.Scope == ClassificationScopes.Global || r.Scope == tenantScope);
            overrides.Values.SelectMany(static l => l).ShouldAllBe(r => r.Scope == ClassificationScopes.Global || r.Scope == tenantScope);
        }
    }

    [Fact]
    public async Task BatchReads_OfNoColumns_AreEmpty()
    {
        var (repo, _) = await _fixture.SeedAsync();

        (await repo.GetColumnClassificationsBatchAsync([], TenantA)).ShouldBeEmpty();
        (await repo.GetColumnMaskOverridesBatchAsync([], TenantA)).ShouldBeEmpty();
    }

    private static string Key(ColumnClassification c) => $"{c.Scope}|{c.TableColumnId.ToLowerInvariant()}|{c.Code}|{c.Source}";

    private static string Key(ColumnMaskOverride o) => $"{o.Id}|{o.Scope}|{o.TableColumnId.ToLowerInvariant()}|{o.Code}|{o.MaskFunction}|{o.ParamsJson}|{o.KeyRef}";

    public sealed class HostFixture : IDisposable
    {
        private readonly System.Threading.SemaphoreSlim _lock = new(1, 1);
        private (IClassificationRepository Repo, IReadOnlyList<string> ColumnIds)? _seeded;

        public WebApplicationFactory<Program> Root { get; } = new();

        public WebApplicationFactory<Program> Factory { get; }

        public HostFixture()
        {
            Factory = Root.WithWebHostBuilder(b =>
            {
                b.UseSetting("Gateway:Authentication:EnableTestAuthHandler", "true");
                b.UseSetting("Gateway:GovernanceDb:Provider", "PostgreSql");
                b.UseSetting("Gateway:GovernanceDb:ConnectionString", GovernanceTestDatabase.Create("ColumnClassificationBatchRead"));
            });
        }

        public async Task<(IClassificationRepository Repo, IReadOnlyList<string> ColumnIds)> SeedAsync()
        {
            await _lock.WaitAsync();
            try
            {
                if (_seeded is { } done)
                {
                    return done;
                }

                var services = Factory.Services;
                var governance = services.GetRequiredService<IGovernanceRepository>();
                var repo = services.GetRequiredService<IClassificationRepository>();
                var table = new TableIdentifier("dom_batch", "main", "people");
                var columns = Enumerable.Range(0, 6).Select(i => new TableColumn { Id = Guid.NewGuid(), ColumnName = $"c{i}", DataType = "text" }).ToList();
                await governance.UpsertTableMetadataAsync(new TableMetadata
                {
                    Table = new Table { Id = Guid.NewGuid(), DisplayName = table.TableName, TableName = table.TableName, SchemaName = table.Schema, SourceType = "PostgreSql", SourceName = table.Domain },
                    Identifier = table,
                    Columns = columns
                });

                var stored = await governance.GetTableMetadataAsync(table);
                var ids = stored!.Columns.Select(static c => c.Id.ToString()).ToList();
                var now = DateTimeOffset.UtcNow;

                async Task Classify(string columnId, string scope, string code) => await repo.AssignColumnClassificationAsync(new ColumnClassification
                {
                    Scope = scope, TableColumnId = columnId, Code = code, Source = "test", AssignedBy = "test", AssignedAt = now
                });

                async Task Override(string columnId, string scope, string? code, MaskFunction function) => await repo.UpsertColumnMaskOverrideAsync(new ColumnMaskOverride
                {
                    Scope = scope, TableColumnId = columnId, Code = code, MaskFunction = function, ParamsJson = "{\"constant\":\"***\"}", UpdatedBy = "test", UpdatedAt = now
                });

                // c0: global PII + PUBLIC; c1: tenant A only; c2: tenant B only; c3: global + both tenants; c4, c5: none.
                await Classify(ids[0], ClassificationScopes.Global, ClassificationScopes.PiiCode);
                await Classify(ids[0], ClassificationScopes.Global, ClassificationScopes.PublicCode);
                await Classify(ids[1], TenantA, ClassificationScopes.ConfidentialCode);
                await Classify(ids[2], TenantB, ClassificationScopes.FinancialCode);
                await Classify(ids[3], ClassificationScopes.Global, ClassificationScopes.FinancialCode);
                await Classify(ids[3], TenantA, ClassificationScopes.PiiCode);
                await Classify(ids[3], TenantB, ClassificationScopes.CredentialsCode);

                await Override(ids[0], ClassificationScopes.Global, null, MaskFunction.TypeSafeRedact);
                await Override(ids[1], TenantA, ClassificationScopes.ConfidentialCode, MaskFunction.Redact);
                await Override(ids[2], TenantB, null, MaskFunction.Nullify);
                await Override(ids[3], ClassificationScopes.Global, ClassificationScopes.FinancialCode, MaskFunction.Nullify);
                await Override(ids[3], TenantA, ClassificationScopes.PiiCode, MaskFunction.TypeSafeRedact);

                // Include an id twice and an id no row refers to.
                var requested = new List<string>(ids) { ids[0], Guid.NewGuid().ToString() };
                _seeded = (repo, requested);
                return _seeded.Value;
            }
            finally
            {
                _lock.Release();
            }
        }

        public void Dispose()
        {
            Factory.Dispose();
            Root.Dispose();
            _lock.Dispose();
            Npgsql.NpgsqlConnection.ClearAllPools();
        }
    }
}
