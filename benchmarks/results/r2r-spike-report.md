# ReadyToRun Empirical Impact Measurement Report (PLAN-OPS-174)

**Date:** 2026-10-18 17:46:05 UTC  
**Architecture:** Linux x64 (.NET 10.0, C# 13)  
**Sample Sizes:** 10 Cold Restarts | 1000 Warm Queries  

## 1. Executive Summary & Decision Gates (Resolving #150 OQ-9)

| Gate | Criterion | Target Threshold | Measured Empirical Delta | Status |
|---|---|---|---|---|
| **Gate 1** | Cold-Start Ready Reduction (P50) | &ge; 25.0% | **-23.3%** (4523.6 ms &rarr; 5576.9 ms) | **FAIL** |
| **Gate 2** | Warm Query P50 Non-Regression | &le; +5.0% (ceiling +10%) | **+154.1%** (1.17 ms &rarr; 2.97 ms) | **FAIL** |
| **Gate 3** | Warm Query Tail Latency (P99) | &le; +10.0% | **+195.6%** (3.47 ms &rarr; 10.24 ms) | **FAIL** |
| **Gate 4** | Container Image Footprint Penalty | &le; +60 MB uncompressed | **+68.0 MB** (+30.3 MB compressed) | **FAIL** |
| **Gate 5** | SC-04 Metadata Security Check | 100% PASS (0 TestAuth) | **PASS** | **PASS** |

**Final Recommendation:** **NO-GO (Reject R2R / Operational Trade-off)**

---

## 2. Footprint & Binary Analysis

| Component | Non-R2R (Baseline) | R2R (Candidate) | Absolute Delta | Relative Delta |
|---|---|---|---|---|
| `Autheris.Api.dll` | 3,375 KB | 5,261 KB | +1,885 KB | 55.9% |
| Publish Directory | 150 MB | 218 MB | +67 MB | 45.2% |
| Docker Uncompressed | 373 MB | 441 MB | +68.0 MB | - |
| Docker Compressed | 139 MB | 170 MB | +30.3 MB | - |

---

## 3. Latency Distribution & Cold Start

| Metric | Non-R2R Baseline | R2R Candidate | Delta (%) |
|---|---|---|---|
| **Cold Start P50 (Median)** | 4523.6 ms | 5576.9 ms | -23.3% |
| **Cold Start Mean** | 5979.2 ms | 6193.5 ms | -3.6% |
| **Warm Query P50** | 1.17 ms | 2.97 ms | +154.1% |
| **Warm Query P90** | 2.08 ms | 4.21 ms | +102.6% |
| **Warm Query P99** | 3.47 ms | 10.24 ms | +195.6% |
