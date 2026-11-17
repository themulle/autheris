# Performance top wins, before/after (2026-11-17)

Source plan: `docs/plans/2026-11-17-plan-dotnet-11-performance.md`. Raw numbers: `perf-top-wins-2026-11-17-*.json`.

Method: .NET 11.0.0, BenchmarkDotNet 0.15.8 ShortRun in-process (the pinned BDN version cannot launch a `net11.0` child process), `[MemoryDiagnoser]`, 16 shared cores with other builds running. Allocation per op is exact; time (3 short iterations) is indicative only.

| Benchmark | Alloc before (B/op) | Alloc after (B/op) | Delta | Mean before (us) | Mean after (us) | Delta |
|---|---|---|---|---|---|---|
| AccessDecisionPointBenchmark.DecideCachedAsync | 2,352 | 2,232 | -5% | 1.12 | 1.02 | -9% |
| AccessDecisionPointBenchmark.DecideUncachedAsync | 10,489 | 10,337 | -1% | 5.75 | 4.76 | -17% |
| AccessDecisionPointBenchmark.ExplainCachedAsync | 3,064 | 2,944 | -4% | 1.62 | 1.36 | -16% |
| AccessDecisionPointBenchmark.ExplainUncachedAsync | 11,201 | 11,049 | -1% | 5.60 | 4.45 | -21% |
| AuditChainBenchmark.HmacEntryHash | 1,848 | 152 | -92% | 2.17 | 1.65 | -24% |
| AuditChainBenchmark.Sha256EntryHash | 1,904 | 152 | -92% | 1.14 | 0.84 | -27% |
| SqlCompilerBenchmarks.Ast(Bucket=0.5KB) | 131,321 | 130,425 | -1% | 1,460.51 | 604.73 | -59% |
| SqlCompilerBenchmarks.AstCached(Bucket=0.5KB) | 6,625 | 3,152 | -52% | 6.45 | 4.20 | -35% |
| SqlCompilerBenchmarks.Ast(Bucket=4KB) | 983,295 | 983,280 | -0% | 2,545.12 | 1,429.84 | -44% |
| SqlCompilerBenchmarks.AstCached(Bucket=4KB) | 13,853 | 3,152 | -77% | 10.88 | 6.26 | -42% |
| SqlCompilerBenchmarks.Ast(Bucket=64KB) | 15,080,016 | 15,080,994 | +0% | 79,882.76 | 41,296.75 | -48% |
| SqlCompilerBenchmarks.AstCached(Bucket=64KB) | 270,447 | 136,885 | -49% | 137.42 | 100.99 | -27% |
| SqlParserCacheBenchmarks.ParseWarm | 25,321 | not touched | | 123.11 | | |
| SqlParserCacheBenchmarks.ParseCold | 3,997,180 | not touched | | 4,482.85 | | |
| VirtualFilterEnforcementBenchmark.IntegerKey_50kRows_10kKeys | 400,187 | not touched | | 1,733.81 | | |
| VirtualFilterEnforcementBenchmark.TextKey_50kRows_10kKeys | 400,224 | not touched | | 2,468.25 | | |
| VirtualFilterEnforcementBenchmark.CompositeKey_50kRows_10kKeys | 2,400,195 | not touched | | 5,892.43 | | |

Time deltas on rows whose code path did not change (`Ast` miss path, `*Uncached*`) are run-to-run noise of the 3-iteration job (JIT tiering and machine load); only the allocation column and the changed paths (`AstCached`, `AuditChain*`) are evidence.

Hot-spot attribution (stage probe, 0.5 KB statement): cache-hit compile 6.6 KB/op, of which `CompileCacheKey.Material` 3.8 KB (the key string); miss path 57 KB/op, `SqlAstBuilder` 17 KB, ANTLR parse on a separate thread (not counted per thread). The benchmark `AccessDecisionPointBenchmark` had a broken row-filter literal (`["EMEA"]` array for `EQ`) that made `Setup` throw; fixed to the scalar `"EMEA"`.
