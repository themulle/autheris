# Candidate defect: very wide joins emitted as derived tables do not finish on SQLite

This is an issue draft for a candidate product defect found by the sqllogictest corpus (REQ-SIMTEST P1). It is not a KnownGap: a `Hang`
can never be one (SEC-SIM-17). Until it is fixed or owner-quarantined with an issue number, the `sim-corpus` job stays red.

Tracked in GitHub issue #339.

Kind: Hang
Error class: Hang:Execute:

## Observation

- Corpus: sqllogictest `test/select5.test` (pinned commit in `Corpus/sqllogictest.manifest.json`), the `join-<n>-<m>` queries (30 to 64 comma-joined tables, equality chain, one literal filter).
- Seed 20261024, sample 5000: 215 of the 5000 sampled queries hit the 5 s execution limit; all are in `select5.test`. 7700 other comparisons were equal, 37 were documented AST-depth rejections, 0 wrong results, 0 policy leaks, 0 crashes.
- The reference (raw SQLite on the original SQL) answers the same queries in under 10 ms.
- Compile succeeds (the emitted SQL is about 4.3 KB). Only execution does not finish.

## Emitted SQL shape

Every table of the comma join is emitted as a projected derived table, and the joins become `CROSS JOIN`:

```
SELECT "x63", "x13", ... FROM (SELECT "a50" AS "a50", "b50" AS "b50", "x50" AS "x50" FROM "main"."t50") AS "t50"
CROSS JOIN (SELECT "a53" AS "a53", ... FROM "main"."t53") AS "t53" CROSS JOIN ... WHERE "b21" = "a6" AND ... AND "a36" = ?1 AND ...
```

Measured with Python `sqlite3` on the select5 data: original SQL 0.0 s; the emitted SQL (parameter `?1` = 9) was interrupted after 60 s.
A simpler chain join over 24 own tables did not reproduce it, so the trigger is the random equality graph of the wide join, not the wrapping alone.

The corpus runner opens a circuit breaker after 40 hangs of the same error class in one file and skips the rest of that file (the Hang line keeps the count, a `CircuitOpen` note records the skipped queries), so the job stays red but cannot time out.

## Expected

The compiled statement runs in a time comparable to the original (no planner blow-up), or the compiler rejects a join wider than a documented limit with a governed error.

## Repro

```
bash scripts/sim/fetch-sqllogictest.sh tests/Autheris.Tests.Simulation/Corpus/sqllogictest.manifest.json "$TMPDIR/slt"
AUTHERIS_SIM_RUN=1 AUTHERIS_SIM_CORPUS_DIR="$TMPDIR/slt" AUTHERIS_SIM_REPORT_DIR="$TMPDIR/rep" AUTHERIS_SIM_SEED=20261024 AUTHERIS_SIM_SAMPLE=5000 \
  dotnet test tests/Autheris.Tests.Simulation -c Release --filter FullyQualifiedName~SltCorpusTests.Corpus_SampledQueries
```

The first hanging query id is `6f101813905c` (test/select5.test, scenario A); the red message names the first file and line of each error class.
