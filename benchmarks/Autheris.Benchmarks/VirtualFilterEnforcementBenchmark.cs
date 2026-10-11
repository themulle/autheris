using System;
using System.Collections.Generic;
using System.Linq;
using Autheris.Application.VirtualFilters.Federation;
using Autheris.Domain.Common;
using Autheris.Domain.Model;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Toolchains.InProcess.Emit;
using BenchmarkDotNet.Running;

namespace Autheris.Benchmarks;

/// <summary>
/// F-GOV-14 (NFR-7, CR-08): cost of the Tier 3 gateway semi-join. 50,000 upstream rows are filtered against 10,000
/// authorized keys (half of the rows are authorized). Run with <c>dotnet run -c Release --project
/// benchmarks/Autheris.Benchmarks -- vfenf</c>. The allocated bytes per operation must stay near the size of the result
/// list (one reference per row), i.e. the probe itself allocates nothing for a single-column key.
/// </summary>
[MemoryDiagnoser]
public class VirtualFilterEnforcementBenchmark
{
    private const int Rows = 50_000;
    private const int Keys = 10_000;

    private VirtualFilterEnforcement _integerKey = null!;
    private VirtualFilterEnforcement _textKey = null!;
    private VirtualFilterEnforcement _compositeKey = null!;
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> _integerRows = null!;
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> _textRows = null!;
    private IReadOnlyList<IReadOnlyDictionary<string, object?>> _compositeRows = null!;

    /// <summary>Short in-process run (no per-benchmark project build); numbers are indicative, the allocation column is exact.</summary>
    public static void Run() => BenchmarkRunner.Run<VirtualFilterEnforcementBenchmark>(
        DefaultConfig.Instance.AddJob(Job.ShortRun.WithToolchain(InProcessEmitToolchain.Instance)));

    [GlobalSetup]
    public void Setup()
    {
        var tenant = new TenantId("tenant_vf");
        var filter = new VirtualFilter
        {
            TenantId = tenant,
            Name = "vf_bench",
            Source = "erp",
            KeyColumns = ["client.client_id"],
            Structured = new StructuredFilterDefinition { From = new TableIdentifier("erp", "auth", "client"), FromAlias = "client" }
        };
        var composite = filter with { Name = "vf_bench_composite", KeyColumns = ["client.client_id", "client.region"] };

        _integerKey = Build(filter, "integer", [VirtualFilterKeyType.IntegerNumber], Enumerable.Range(0, Keys).Select(i => new object?[] { (long)(i * 2) }), "client_id");
        _textKey = Build(filter, "varchar", [VirtualFilterKeyType.Text], Enumerable.Range(0, Keys).Select(i => new object?[] { "K" + (i * 2) }), "client_id");
        _compositeKey = Build(composite, "varchar", [VirtualFilterKeyType.Text, VirtualFilterKeyType.Text], Enumerable.Range(0, Keys).Select(i => new object?[] { "K" + (i * 2), "EU" }), "client_id", "region");

        _integerRows = Enumerable.Range(0, Rows).Select(i => Row(("client_id", (long)(i % (2 * Keys))), ("name", "n"))).ToArray();
        _textRows = Enumerable.Range(0, Rows).Select(i => Row(("client_id", "K" + (i % (2 * Keys))), ("name", "n"))).ToArray();
        _compositeRows = Enumerable.Range(0, Rows).Select(i => Row(("client_id", "K" + (i % (2 * Keys))), ("region", "EU"))).ToArray();
    }

    [Benchmark] public int IntegerKey_50kRows_10kKeys() => _integerKey.Apply(_integerRows).Count;

    [Benchmark] public int TextKey_50kRows_10kKeys() => _textKey.Apply(_textRows).Count;

    [Benchmark] public int CompositeKey_50kRows_10kKeys() => _compositeKey.Apply(_compositeRows).Count;

    private static IReadOnlyDictionary<string, object?> Row(params (string Column, object? Value)[] values) =>
        values.ToDictionary(v => v.Column, v => v.Value, StringComparer.OrdinalIgnoreCase);

    private static VirtualFilterEnforcement Build(
        VirtualFilter filter, string idType, VirtualFilterKeyType[] types, IEnumerable<object?[]> tuples, params string[] columns)
    {
        var table = new TableMetadata
        {
            Identifier = new TableIdentifier("crm_api", "public", "customers"),
            Table = new Table
            {
                TableName = "customers",
                SchemaName = "public",
                SourceType = "Http",
                DataSourceType = DataSourceType.HttpDeclarative,
                SourceName = "crm_api",
                IsActive = true
            },
            Columns = columns.Select(c => new TableColumn { ColumnName = c, DataType = c == "client_id" ? idType : "varchar" }).ToList()
        };
        var pair = new AppliedVirtualFilterBinding(
            filter,
            new FilterBinding { FilterName = filter.Name, TargetPattern = "crm_api.public.customers" },
            1);
        return VirtualFilterEnforcement.Create(table, [(pair, VirtualFilterKeySet.Create(types, tuples))]);
    }
}
