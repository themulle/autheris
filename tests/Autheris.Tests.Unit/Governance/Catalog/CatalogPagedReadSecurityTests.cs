namespace Autheris.Tests.Unit.Governance.Catalog;

using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.Catalog;
using Autheris.Application.Common.Interfaces;
using Autheris.Application.Governance.Paging;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using Autheris.Domain.Options;
using Autheris.Infrastructure.Persistence;
using Autheris.Infrastructure.Persistence.Dialects;
using Autheris.Tests.Common.Fakes;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Shouldly;
using Xunit;

[Trait("Category", "Security")]
public sealed class CatalogPagedReadSecurityTests
{
    [Fact]
    public async Task OneStaleTable_LoadsOnlyUncachedIds_NotFullCatalog()
    {
        var t1 = MakeRecord("sales", "public", "accounts", "initial_1", epoch: 1);
        var t2 = MakeRecord("sales", "public", "orders", "initial_2", epoch: 1);
        var t3 = MakeRecord("sales", "public", "shipments", "initial_3", epoch: 1);
        var store = new FakeCatalogStore([t1, t2, t3]);
        var repo = CreateRepository(store, postgresql: true);

        var first = await repo.GetAllTablesAsync();
        first.Count.ShouldBe(3);

        store.RecordedCommands.Clear();
        store.UpdateDescription(t2.Id, "refreshed_2", newEpoch: 2);

        var second = await repo.GetAllTablesAsync();
        second.Count.ShouldBe(3);
        second.Single(t => t.Identifier == t2.Identifier).Table.Description.ShouldBe("refreshed_2");
        second.Single(t => t.Identifier == t1.Identifier).Table.Description.ShouldBe("initial_1");
        second.Single(t => t.Identifier == t3.Identifier).Table.Description.ShouldBe("initial_3");

        // Must never execute a full active-table or full active-column reload on a partial cache miss.
        store.RecordedCommands.ShouldNotContain(c =>
            c.Sql.Contains("FROM TABLES", StringComparison.OrdinalIgnoreCase) &&
            !c.Sql.Contains("is_active = 1 AND id IN", StringComparison.OrdinalIgnoreCase) &&
            !c.Sql.Contains("SELECT id, source_name, schema_name, table_name FROM TABLES", StringComparison.OrdinalIgnoreCase));
        store.RecordedCommands.ShouldNotContain(c =>
            c.Sql.Contains("FROM TABLE_COLUMNS c", StringComparison.OrdinalIgnoreCase) &&
            !c.Sql.Contains("WHERE c.table_id IN", StringComparison.OrdinalIgnoreCase));

        var tableInCmds = store.RecordedCommands
            .Where(c => c.Sql.Contains("FROM TABLES", StringComparison.OrdinalIgnoreCase) && c.Sql.Contains("WHERE is_active = 1 AND id IN", StringComparison.OrdinalIgnoreCase))
            .ToList();
        tableInCmds.Count.ShouldBe(1);
        tableInCmds[0].Parameters.Values.ShouldBe([t2.Id.ToString()]);

        var colInCmds = store.RecordedCommands
            .Where(c => c.Sql.Contains("FROM TABLE_COLUMNS", StringComparison.OrdinalIgnoreCase) && c.Sql.Contains("WHERE c.table_id IN", StringComparison.OrdinalIgnoreCase))
            .ToList();
        colInCmds.Count.ShouldBe(1);
        colInCmds[0].Parameters.Values.ShouldBe([t2.Id.ToString()]);
    }

    [Fact]
    public async Task EpochReadBeforeData_ConcurrentWriteMakesEntryStale()
    {
        var rec = MakeRecord("sales", "public", "ledger", "old_snapshot", epoch: 1);
        var store = new FakeCatalogStore([rec]);
        var repo = CreateRepository(store, postgresql: true);

        // Simulate a concurrent write occurring after POLICY_EPOCHS was read and before metadata caching completes.
        store.BeforeTableEntityQueryHook = (sql, _) =>
        {
            if (sql.Contains("id IN", StringComparison.OrdinalIgnoreCase))
            {
                store.BeforeTableEntityQueryHook = null;
                store.SetEpochOnly(rec.Id, newEpoch: 2);
            }
        };

        var first = await repo.GetAllTablesAsync();
        first.ShouldHaveSingleItem().Table.Description.ShouldBe("old_snapshot");

        // Now DB data also reflects the newer commit; the cache entry was stamped with epoch 1 (< 2) so it must be treated as stale.
        store.UpdateDescription(rec.Id, "concurrent_new", newEpoch: 2);
        var second = await repo.GetAllTablesAsync();
        second.ShouldHaveSingleItem().Table.Description.ShouldBe("concurrent_new");

        // Also verify parameter-budget chunking (> 500 uncached ids) splits into <= 500 parameter chunks.
        var largeRecords = Enumerable.Range(0, 620)
            .Select(i => MakeRecord("bulk", "public", $"tbl_{i:D4}", $"d_{i}", epoch: 1))
            .ToList();
        var largeStore = new FakeCatalogStore(largeRecords);
        var largeRepo = CreateRepository(largeStore, postgresql: true);
        var loaded = await largeRepo.GetAllTablesAsync();
        loaded.Count.ShouldBe(620);
        largeStore.RecordedCommands
            .Where(c => c.Sql.Contains("WHERE is_active = 1 AND id IN", StringComparison.OrdinalIgnoreCase))
            .Select(c => c.Parameters.Count)
            .ShouldBe([500, 120]);
    }

    [Fact]
    public async Task QueryTables_ClampsLimitTo500_AndValidatesAfterCursor()
    {
        var records = new[]
        {
            MakeRecord("sales", "public", "alpha", "d1"),
            MakeRecord("sales", "public", "beta", "d2"),
            MakeRecord("sales", "public", "gamma", "d3")
        };
        var store = new FakeCatalogStore(records);
        var repo = CreateRepository(store, postgresql: true);

        var high = await repo.QueryTablesAsync(new CatalogTableQuery(Limit: 10_000));
        high.Items.Select(t => t.Identifier.TableName).ShouldBe(["alpha", "beta", "gamma"]);
        high.HasMore.ShouldBeFalse();
        store.RecordedCommands.Last(c => c.Sql.Contains("LIMIT @take", StringComparison.Ordinal))
            .Parameters["@take"].ShouldBe(501);

        store.RecordedCommands.Clear();
        var zero = await repo.QueryTablesAsync(new CatalogTableQuery(Limit: 0));
        zero.Items.Count.ShouldBe(1);
        zero.HasMore.ShouldBeTrue();
        store.RecordedCommands.Last(c => c.Sql.Contains("LIMIT @take", StringComparison.Ordinal))
            .Parameters["@take"].ShouldBe(2);

        var page1 = await repo.QueryTablesAsync(new CatalogTableQuery(Limit: 2));
        page1.Items.Select(t => t.Identifier.TableName).ShouldBe(["alpha", "beta"]);
        page1.HasMore.ShouldBeTrue();
        page1.NextAfter.ShouldNotBeNull();

        var page2 = await repo.QueryTablesAsync(new CatalogTableQuery(After: page1.NextAfter, Limit: 2));
        page2.Items.Select(t => t.Identifier.TableName).ShouldBe(["gamma"]);
        page2.HasMore.ShouldBeFalse();
        page2.NextAfter.ShouldBeNull();

        Should.Throw<ArgumentException>(() => repo.QueryTablesAsync(new CatalogTableQuery(After: string.Empty)).GetAwaiter().GetResult());
        Should.Throw<ArgumentException>(() => repo.QueryTablesAsync(new CatalogTableQuery(After: "invalid-cursor")).GetAwaiter().GetResult());
        Should.Throw<ArgumentException>(() => repo.QueryTablesAsync(new CatalogTableQuery(After: PageCursor.Encode("two.parts"))).GetAwaiter().GetResult());
        Should.Throw<ArgumentException>(() => repo.QueryTablesAsync(new CatalogTableQuery(After: new string('a', 300))).GetAwaiter().GetResult());
    }

    [Fact]
    public async Task QueryTables_NamePrefix_EscapesLikeMetacharacters()
    {
        var special = MakeRecord("sales", "public", "ord%_[\\'x_tail", "escaped");
        var decoy = MakeRecord("sales", "public", "ord_other", "decoy");
        var store = new FakeCatalogStore([special, decoy]);

        foreach (var pg in new[] { true, false })
        {
            store.RecordedCommands.Clear();
            var repo = CreateRepository(store, postgresql: pg);
            var page = await repo.QueryTablesAsync(new CatalogTableQuery(NamePrefix: "ord%_[\\'x"));

            page.Items.ShouldHaveSingleItem().Identifier.ShouldBe(special.Identifier);

            var queryCmd = store.RecordedCommands.First(c => c.Sql.Contains("LIKE LOWER(@namePrefix) ESCAPE '\\'", StringComparison.Ordinal));
            queryCmd.Parameters["@namePrefix"].ShouldBe("ord\\%\\_\\[\\\\'x%");
            queryCmd.Sql.ShouldContain(pg
                ? "ORDER BY LOWER(source_name), LOWER(schema_name), LOWER(table_name) LIMIT @take"
                : "ORDER BY LOWER(source_name), LOWER(schema_name), LOWER(table_name) OFFSET 0 ROWS FETCH NEXT @take ROWS ONLY");
        }
    }

    [Fact]
    public async Task GetTables_And_QueryTables_RejectOversizedRequestList()
    {
        var active = MakeRecord("sales", "public", "orders", "active");
        var inactive = MakeRecord("sales", "public", "archived", "inactive", isActive: false);
        var store = new FakeCatalogStore([active, inactive]);
        var repo = CreateRepository(store, postgresql: true);

        CatalogTableQuery.MaxRequestIdentifiers.ShouldBe(500);
        ITableMetadataRepository iface = repo;

        var oversizedIds = Enumerable.Range(0, 501)
            .Select(i => new TableIdentifier("sales", "public", $"t_{i}"))
            .ToArray();

        Should.Throw<ArgumentException>(() => CatalogTableQuery.ValidateRequestIdentifiers(oversizedIds));
        await Should.ThrowAsync<ArgumentException>(() => iface.GetTablesForRequestAsync(oversizedIds));

        // Internal batch GetTablesAsync (> 500) chunks safely by MaxIdentifiersPerChunk = 500 rather than failing.
        store.RecordedCommands.Clear();
        var internalBatch = await repo.GetTablesAsync(oversizedIds);
        internalBatch.ShouldBeEmpty();
        store.RecordedCommands
            .Count(c => c.Sql.Contains("is_active = 1 AND (", StringComparison.OrdinalIgnoreCase))
            .ShouldBe(2);

        // Oversized filter strings are rejected fail-closed.
        await Should.ThrowAsync<ArgumentException>(() => repo.QueryTablesAsync(new CatalogTableQuery(NamePrefix: new string('x', 257))));
        await Should.ThrowAsync<ArgumentException>(() => repo.QueryTablesAsync(new CatalogTableQuery(DataSource: new string('d', 257))));

        // Unknown and inactive identifiers both return empty identically (no existence oracle).
        var unknownOnly = await repo.GetTablesAsync([new TableIdentifier("sales", "public", "does_not_exist")]);
        var inactiveOnly = await repo.GetTablesAsync([inactive.Identifier]);
        unknownOnly.ShouldBeEmpty();
        inactiveOnly.ShouldBeEmpty();

        // IncludeInactive defaults to false; only explicit opt-in returns inactive tables.
        var defaultPaged = await repo.QueryTablesAsync(new CatalogTableQuery());
        defaultPaged.Items.Select(t => t.Identifier).ShouldBe([active.Identifier]);
        var withInactive = await repo.QueryTablesAsync(new CatalogTableQuery(IncludeInactive: true));
        withInactive.Items.Select(t => t.Identifier).ShouldBe([inactive.Identifier, active.Identifier]);
    }

    private static GovernanceRepository CreateRepository(FakeCatalogStore store, bool postgresql)
    {
        IGovernanceSqlDialect dialect = postgresql
            ? new PostgreSqlGovernanceDialect(() => store.Open())
            : new SqlServerGovernanceDialect(() => store.Open());
        return new GovernanceRepository(
            dialect,
            options: Options.Create(new GatewayOptions()),
            logger: NullLogger<GovernanceRepository>.Instance);
    }

    private static FakeTableRecord MakeRecord(string domain, string schema, string table, string description, long epoch = 1, bool isActive = true) =>
        new(Guid.NewGuid(), domain, schema, table, description, epoch, isActive);

    private sealed record RecordedCommand(string Sql, IReadOnlyDictionary<string, object?> Parameters);

    private sealed class FakeTableRecord(Guid id, string domain, string schema, string tableName, string description, long epoch, bool isActive)
    {
        public Guid Id { get; } = id;
        public string Domain { get; } = domain;
        public string Schema { get; } = schema;
        public string TableName { get; } = tableName;
        public TableIdentifier Identifier => new(Domain, Schema, TableName);
        public string Description { get; set; } = description;
        public long Epoch { get; set; } = epoch;
        public bool IsActive { get; } = isActive;
    }

    private sealed class FakeCatalogStore(IEnumerable<FakeTableRecord> seed)
    {
        private readonly List<FakeTableRecord> _tables = [.. seed];
        public List<RecordedCommand> RecordedCommands { get; } = [];
        public Action<string, IReadOnlyDictionary<string, object?>>? BeforeTableEntityQueryHook { get; set; }

        public DbConnection Open() => new CallbackDbConnection(Execute);

        public void SetEpochOnly(Guid tableId, long newEpoch)
            => _tables.Single(t => t.Id == tableId).Epoch = newEpoch;

        public void UpdateDescription(Guid tableId, string description, long newEpoch)
        {
            var row = _tables.Single(t => t.Id == tableId);
            row.Description = description;
            row.Epoch = newEpoch;
        }

        private DbDataReader Execute(string sql, IReadOnlyDictionary<string, object?> parameters)
        {
            RecordedCommands.Add(new RecordedCommand(sql, parameters));

            if (sql.Contains("to_regclass", StringComparison.OrdinalIgnoreCase) ||
                sql.Contains("OBJECT_ID", StringComparison.OrdinalIgnoreCase) ||
                sql.Contains("SESSIONPROPERTY", StringComparison.OrdinalIgnoreCase))
            {
                return new StubDbDataReader(["present"], [[1]]);
            }

            if (sql.Contains("FROM POLICY_EPOCHS", StringComparison.OrdinalIgnoreCase))
            {
                var rows = _tables
                    .Select(t => new object?[] { t.Id.ToString(), t.Epoch })
                    .ToList();
                return new StubDbDataReader(["id", "epoch"], rows);
            }

            if (sql.StartsWith("SELECT id, source_name, schema_name, table_name FROM TABLES", StringComparison.OrdinalIgnoreCase))
            {
                var rows = _tables
                    .Where(t => t.IsActive)
                    .Select(t => new object?[] { t.Id.ToString(), t.Domain, t.Schema, t.TableName })
                    .ToList();
                return new StubDbDataReader(["id", "source_name", "schema_name", "table_name"], rows);
            }

            if (sql.Contains("FROM TABLES", StringComparison.OrdinalIgnoreCase))
            {
                BeforeTableEntityQueryHook?.Invoke(sql, parameters);
                var matched = FilterTables(sql, parameters);
                var rows = matched
                    .Select(t => new object?[]
                    {
                        t.Id.ToString(), "sql", t.Domain, t.Schema, t.TableName,
                        t.TableName, "NORMAL", 0, t.IsActive ? 1 : 0, 0,
                        null, null, t.Description, null, null, null, null, null, 0, null
                    })
                    .ToList();
                return new StubDbDataReader(
                    [
                        "id", "source_type", "source_name", "schema_name", "table_name",
                        "display_name", "sensitivity", "requires_four_eyes", "is_active", "data_source_type",
                        "http_endpoint_json", "plugin_name", "description", "long_description", "doc_source",
                        "required_filter_columns_json", "texts_json", "min_group_size", "non_personal", "dbt_unique_id"
                    ],
                    rows);
            }

            if (sql.Contains("FROM TABLE_COLUMNS", StringComparison.OrdinalIgnoreCase))
            {
                var requested = new HashSet<string>(
                    parameters.Values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty),
                    StringComparer.OrdinalIgnoreCase);
                var rows = _tables
                    .Where(t => requested.Count == 0 || requested.Contains(t.Id.ToString()))
                    .Select(t => new object?[]
                    {
                        Guid.NewGuid().ToString(), t.Id.ToString(), "id", "int", 0,
                        null, null, null, null, null,
                        null, null, null, null, null, null, null, null
                    })
                    .ToList();
                return new StubDbDataReader(
                    [
                        "id", "table_id", "column_name", "data_type", "is_sensitive",
                        "m_id", "rule_type", "pattern_or_format", "replacement", "hmac_key_id",
                        "desc", "long_desc", "meta", "doc", "disp", "texts", "collation", "len_sem"
                    ],
                    rows);
            }

            return new StubDbDataReader(["id"], Array.Empty<object?[]>());
        }

        private List<FakeTableRecord> FilterTables(string sql, IReadOnlyDictionary<string, object?> parameters)
        {
            IEnumerable<FakeTableRecord> q = _tables;
            if (sql.Contains("is_active = 1", StringComparison.OrdinalIgnoreCase))
            {
                q = q.Where(t => t.IsActive);
            }

            if (sql.Contains("WHERE is_active = 1 AND id IN", StringComparison.OrdinalIgnoreCase))
            {
                var ids = new HashSet<string>(
                    parameters.Values.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture) ?? string.Empty),
                    StringComparer.OrdinalIgnoreCase);
                q = q.Where(t => ids.Contains(t.Id.ToString()));
            }
            else if (sql.Contains("LOWER(source_name) = LOWER(@d", StringComparison.OrdinalIgnoreCase))
            {
                var wanted = new HashSet<TableIdentifier>();
                for (var i = 0; parameters.TryGetValue("@d" + i, out var d); i++)
                {
                    wanted.Add(new TableIdentifier(
                        Convert.ToString(d, CultureInfo.InvariantCulture)!,
                        Convert.ToString(parameters["@s" + i], CultureInfo.InvariantCulture)!,
                        Convert.ToString(parameters["@t" + i], CultureInfo.InvariantCulture)!));
                }

                q = q.Where(t => wanted.Contains(t.Identifier));
            }

            if (parameters.TryGetValue("@dataSource", out var ds) && ds is string domain)
            {
                q = q.Where(t => string.Equals(t.Domain, domain, StringComparison.OrdinalIgnoreCase));
            }

            if (parameters.TryGetValue("@schema", out var sc) && sc is string schema)
            {
                q = q.Where(t => string.Equals(t.Schema, schema, StringComparison.OrdinalIgnoreCase));
            }

            if (parameters.TryGetValue("@namePrefix", out var np) && np is string pattern && pattern.EndsWith('%'))
            {
                var unescaped = UnescapeLikePrefix(pattern[..^1]);
                q = q.Where(t => t.TableName.StartsWith(unescaped, StringComparison.OrdinalIgnoreCase));
            }

            if (parameters.TryGetValue("@afterDomain", out var ad) && ad is string afterD)
            {
                var afterId = new TableIdentifier(
                    afterD,
                    Convert.ToString(parameters["@afterSchema"], CultureInfo.InvariantCulture)!,
                    Convert.ToString(parameters["@afterTable"], CultureInfo.InvariantCulture)!);
                q = q.Where(t => CatalogTableCursor.Compare(t.Identifier, afterId) > 0);
            }

            q = q.OrderBy(t => t.Domain, StringComparer.OrdinalIgnoreCase)
                 .ThenBy(t => t.Schema, StringComparer.OrdinalIgnoreCase)
                 .ThenBy(t => t.TableName, StringComparer.OrdinalIgnoreCase);

            if (parameters.TryGetValue("@take", out var tk) && tk is int take)
            {
                q = q.Take(take);
            }

            return q.ToList();
        }

        private static string UnescapeLikePrefix(string escaped)
        {
            var chars = new List<char>(escaped.Length);
            for (var i = 0; i < escaped.Length; i++)
            {
                if (escaped[i] == '\\' && i + 1 < escaped.Length)
                {
                    chars.Add(escaped[++i]);
                }
                else
                {
                    chars.Add(escaped[i]);
                }
            }

            return new string(chars.ToArray());
        }
    }
}
