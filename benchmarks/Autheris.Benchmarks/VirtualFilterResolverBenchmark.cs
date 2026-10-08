using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Autheris.Application.VirtualFilters;
using Autheris.Domain.Common;
using Autheris.Domain.Interfaces;
using Autheris.Domain.Model;

namespace Autheris.Benchmarks;

/// <summary>
/// Virtual filters, phase 9: cost of one resolution (caller x object) with 200 bindings in 5 profiles over 150 tables,
/// with the generation memo (steady state) and without it (first access after a change). Target: under 50 µs per
/// decision with a memo hit.
/// </summary>
public sealed class VirtualFilterResolverBenchmark
{
    private static readonly TenantId Tenant = new("tenant_lwe");
    private static readonly Sid User = new("S-1-5-21-LWE-DAVID");
    private static readonly HashSet<Sid> Groups = [new("S-1-5-21-GROUP-CRANES")];
    private static readonly HashSet<string> Roles = ["CraneAnalyst", "Reader"];

    private sealed class Snapshots(VirtualFilterSnapshot snapshot) : IVirtualFilterSnapshotProvider
    {
        private long _generation = snapshot.Generation;
        public bool BumpEveryCall { get; set; }

        public ValueTask<VirtualFilterSnapshot> GetAsync(CancellationToken ct = default) =>
            ValueTask.FromResult(BumpEveryCall ? snapshot with { Generation = Interlocked.Increment(ref _generation) } : snapshot);

        public void Invalidate() { }
    }

    private readonly List<TableMetadata> _tables;
    private readonly Snapshots _snapshots;
    private readonly MandatoryRowFilterResolver _resolver;

    public VirtualFilterResolverBenchmark()
    {
        var filters = Enumerable.Range(0, 40).Select(i => new VirtualFilter
        {
            TenantId = Tenant,
            Name = $"filter_{i:00}",
            Source = "lwetem_prod",
            KeyColumns = [i % 2 == 0 ? "client.client_id" : "client.crane_serial_number"],
            Structured = new StructuredFilterDefinition
            {
                From = new TableIdentifier("lwetem_prod", "conf", "client"),
                FromAlias = "client",
                Joins = [new FilterJoin(new TableIdentifier("lwetem_prod", "md", "crane"), "crane", "crane.serial_number", "client.crane_serial_number")],
                Where = [new FilterCondition("crane.is_delivered", FilterConditionOperator.IsNull)]
            }
        }).ToList();

        var schemas = new[] { "fms", "tem", "dm", "conf", "md" };
        var profiles = Enumerable.Range(0, 5).Select(p => new AccessProfile
        {
            TenantId = Tenant,
            Name = $"profile_{p}",
            GranteeType = p switch { 0 => GranteeType.User, 1 => GranteeType.Group, _ => GranteeType.Role },
            GranteeSid = p switch { 0 => User, 1 => Groups.First(), _ => (Sid?)null },
            RoleName = p >= 2 ? (p == 2 ? "CraneAnalyst" : p == 3 ? "Reader" : "Other") : null,
            Scope = "lwetem_prod.*.*",
            Uncovered = UncoveredPolicy.Skip,
            Bindings = filters.Select((f, i) => new FilterBinding
            {
                FilterName = f.Name,
                TargetPattern = $"lwetem_prod.({schemas[i % schemas.Length]}|{schemas[(i + 1) % schemas.Length]}).*.{(i % 2 == 0 ? "client_id" : "crane_serial_number")}"
            }).ToList()
        }).ToList();

        _tables = Enumerable.Range(0, 150).Select(i => new TableMetadata
        {
            Identifier = new TableIdentifier("lwetem_prod", schemas[i % schemas.Length], $"table_{i:000}"),
            Table = new Table { SourceName = "lwetem_prod", SchemaName = schemas[i % schemas.Length], TableName = $"table_{i:000}", SourceType = "SqlServer" },
            Columns = new[] { "id", "ts", i % 3 == 0 ? "client_id" : "value", i % 4 == 0 ? "crane_serial_number" : "other" }
                .Select(c => new TableColumn { ColumnName = c }).ToList()
        }).ToList();

        _snapshots = new Snapshots(new VirtualFilterSnapshot(1, filters, profiles));
        _resolver = new MandatoryRowFilterResolver(_snapshots, new StructuredFilterSqlBuilder());
    }

    public int Bindings => 5 * 40;

    public int Tables => _tables.Count;

    public ValueTask<MandatoryFilterOutcome> ResolveAsync(int i) =>
        _resolver.ResolveAsync(new MandatoryFilterQuery(User, Groups, Roles, Tenant, _tables[i % _tables.Count]));

    public static async Task RunAsync()
    {
        Console.WriteLine("--- Virtual filter resolution (200 bindings, 5 profiles, 150 tables) ---");
        var bench = new VirtualFilterResolverBenchmark();

        foreach (var (label, cold, iterations) in new[] { ("Memo hit (steady state)", false, 300_000), ("Memo miss (after a change)", true, 20_000) })
        {
            bench._snapshots.BumpEveryCall = cold;
            for (int i = 0; i < 2_000; i++) await bench.ResolveAsync(i);

            var latencies = new double[iterations];
            var sw = new Stopwatch();
            for (int i = 0; i < iterations; i++)
            {
                sw.Restart();
                await bench.ResolveAsync(i);
                sw.Stop();
                latencies[i] = sw.Elapsed.TotalMicroseconds;
            }

            Array.Sort(latencies);
            Console.WriteLine($" {label,-28}: P50 {latencies[iterations / 2],8:F2} µs  P99 {latencies[(int)(iterations * 0.99)],8:F2} µs  max {latencies[^1],9:F2} µs");
        }

        Console.WriteLine();
    }
}
