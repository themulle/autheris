# Plan: triage of the scheduled workflow failures of 2026-11-19 (Issues #910, #911, #912)

Phase 2 (solution architect), 2026-11-19. Source: the issues filed automatically by the scheduled workflows. The evidence comes from `gh run view` on the failed runs. Implementation starts after the v1.9.0 tag. Workflow files are not changed by this plan.

## 1. Evidence

| Issue | Workflow / failed step | Run | Later runs | Classification |
|---|---|---|---|---|
| [#910](https://github.com/themulle/autheris/issues/910) | Simulation nightly / `sim-corpus`: steps "Corpus tests" and "Corpus report (baseline, fail-closed)" | 44511712217 (11:06) | Scheduled runs at 11:57 and 13:43 on 2026-11-19 **succeeded** | Transient. The log shows no stable failing case. |
| [#911](https://github.com/themulle/autheris/issues/911) | Footprint & CI Duration Budget Nightly / "Check results against budgets" | 44519496502 | 2026-11-18 succeeded | **Real budget breach:** `coldStartP50Seconds = 4.52 s` (budget 3.0 s, limit 3.3 s) and `coreImageUncompressedMB = 410.28 MB` (budget 325 MB, limit 357.5 MB). `warmP99` passes (1.26 ms). Base image: `mcr.microsoft.com/dotnet/aspnet:11.0.0-resolute`. |
| [#912](https://github.com/themulle/autheris/issues/912) | SQL Compiler Gate (nightly, cutover, G9) / "G8 thresholds" | 44502573587 (scheduled), and again on dispatch 17:11 | A dispatch run was still in progress at 18:32 | Repeatable. The threshold value is not in the failed-job log (the build output hides it). It must come from the G8 step summary or artifact. |

The budgets come from OD-37 (325 MB / 3.0 s, kept by owner decision, see the handover doc section 4, #855).

## 2. Decisions

1. D-NF-1: #910: no code change. Close it after one more green scheduled run (already true), with a comment linking the green runs.
2. D-NF-2: #911: find the cause before changing any budget. Compare the footprint artifacts of 2026-11-18 (green) and 2026-11-19 (red): image layer sizes (`podman history`) and the cold start breakdown.
   - Suspects:
     - an image flavor change (#885, core and full flavors);
     - a new dependency in the core image;
     - a change in the ReadyToRun or trimming settings.
   - The budget is only raised by an explicit owner decision recorded in the master plan.
3. D-NF-3: #912: read the G8 metric from the run summary or artifact first. If the metric moved because of a 1.9.0 change (new statements in the corpus), update the threshold baseline in the same PR as its cause, with justification. If the compiler regressed, fix it in the engine.

## 3. Work packages

**WP-NF-1 (XS): close #910.** No code. Add a comment linking the two green runs.

**WP-NF-2 (S): footprint regression analysis and fix (#911)**
- Download `footprint-budget-results` (artifact 14527783194) and the previous green artifact. Diff the image size per layer and the cold start p50.
- Write the result into #911. If the cause is a removable dependency or setting, fix it in the owning project. The likely files are the `src/Autheris.Api` csproj or the publish settings; Dockerfiles may be changed only with owner approval, which is outside this plan.
- Verify with one `workflow_dispatch` of the footprint workflow.

**WP-NF-3 (S): G8 threshold triage (#912)**
- Read the G8 output of runs 44502573587 and 44588863472 (`gh run view --log` for the G8 step only, or the job summary).
- Classify the result as a corpus change or a compiler regression, and fix it at the source.
- Tests: the gate itself (`tests/Autheris.Tests.SqlCompilerGate`), run through `workflow_dispatch`.

Order: independent. No DDL.

## 4. Integration test impact

None. These are CI-only gates, verified with `workflow_dispatch` on GitHub Actions.

## 5. Phase 3

**Not required.**

## 6. Owner questions (recommended defaults)

1. Q1: If the 410 MB core image comes from a deliberate 1.9.0 change, should the OD-37 budget be raised? Default: **no**. Find the cause first. A raise needs an explicit owner decision with the measured value.
2. Q2: Should a cold start of 4.5 s on the hosted runner block a release? Default: **no for v1.9.0** (the nightly gate is not a release gate). Fix it in 1.9.x.
