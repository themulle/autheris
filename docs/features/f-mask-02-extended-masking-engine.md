# F-MASK-02: Extended Column Masking Engine (GEO_JITTER, PARTIAL_MASK, TOKENIZATION)

## Executive Summary

Autheris extends its format-preserving data privacy engine with advanced mathematical, geographic, and cryptographic masking capabilities (Requirements **R-53** and **B-06**).

These rules protect sensitive personal data and business identifiers from unauthorized exposure while preserving analytical utility, length characteristics, and database-level pushdown performance.

---

## Supported Masking Strategies

| Rule Name | Target Data Types | Description | Output Example |
|---|---|---|---|
| `GEO_JITTER` | Floats, Decimals, Geocoordinates | Rounds latitude/longitude to a configurable precision and injects deterministic pseudo-random Gaussian noise based on an HMAC seed. Prevents pinpointing home addresses while keeping city/regional analytics valid. | `52.520008, 13.404954` -> `52.5184, 13.4011` |
| `PARTIAL_MASK` | Strings, Identifiers, IBAN, Credit Cards | Unicode-Rune-safe partial string masking. Masks the center portion of the string while preserving configured leading and trailing characters. Avoids multi-byte UTF-8 corruption. | `DE89370400440532013000` -> `DE89****************3000` |
| `TOKENIZATION` | Strings, Numeric Keys | Cryptographically reversible or vault-referenced pseudonymization. Replaces sensitive identifiers with collision-free synthetic surrogate tokens. | `USR-984214` -> `TKN-4f8a9b2c` |
| `REDACT` (Typed) | All Types | Replaces sensitive fields with deterministic, type-safe neutral defaults: `0` for numbers, `false` for booleans, `1970-01-01` for dates, and `""` or `"[REDACTED]"` for strings. | `45,000.00` -> `0.00` |
| `HASH` | Strings, Binary | Irreversible SHA-256 HMAC pseudonymization using a salted master key. Useful for joinable anonymized identifiers. | `alice@example.com` -> `8f4b2...3a1e` |
| `NULLING` | Nullable Types | Replaces values with SQL `NULL`. | `'Secret'` -> `NULL` |
| `NOISE` | Numbers, Financial Figures | Laplace or Gaussian statistical perturbation for differential privacy. | `1,250` -> `1,241` |

---

## One Mask Semantic for SQL and In-Memory Sources

Columns masked in the database and columns masked in gateway memory (HTTP, lakehouse and other non-SQL sources) use the same typed `MaskSpec`:
`SqlDataMaskingProvider.BuildUnkeyedMaskSpec` defines the mask, the dialect generator renders it for SQL, and `MaskSpecEvaluator` evaluates it for in-memory values.
`ColumnMaskingProvider` only evaluates the spec for the key-less kinds; `MaskingInputCanonicalizer` stays the single canonicalizer of mask input.

| Rule | Result (both engines) |
|---|---|
| `NULLIFY` | `NULL` for every value kind |
| `REDACT` | text becomes the replacement constant (default `***`); numeric, temporal and boolean values become `NULL` |
| `PARTIAL_MASK` | prefix + one mask character per hidden character + suffix; a value not longer than prefix plus suffix (including the empty string) becomes five mask characters; a fixed-length partial mask is the five-character constant |
| `MASK_EMAIL` / `MASK_IBAN` | constants `***@***` and `****`; neither characters nor length are revealed |
| `GEO_JITTER` (round mode) | rounded to `Decimals`; the (0, 0) null island stays `0` |
| `HMAC` | 64 hex characters, deterministic; the key is derived per engine, so values of the two engines are not equal by design |
| `NULL` input | `NULL` for every rule |

The golden corpus `tests/Autheris.Tests.Unit/Masking/MaskSemanticsCorpus.cs` runs each row against both engines.

## In-Memory Row Filters

Sources that filter in gateway memory evaluate the typed row policy (`IRowPolicy`, backed by the `PolicyPredicate` tree) of the access decision with SQL three-valued logic:
a row passes only when the predicate is TRUE. Comparisons are type-strict (no `'007' = 7`), and policy values used as `LIKE` patterns keep their wildcards only when flagged as patterns.
The evaluator fails closed: a predicate node it does not implement, an unknown column, or a comparison between different value kinds denies every row; a row that lacks a column the predicate reads is denied.
A decision whose filter text no longer equals the projection of its typed policy denies every row. SQL text is parsed only for decisions that carry no typed policy (string-only legacy decisions of non-SQL sources), in an isolated legacy evaluator.

---

## AST Pushdown & Dual-Engine Execution

The extended masking engine operates in two execution modes:

1. **SQL AST Pushdown Mode:**
   - [`AstSecurityVisitor`](../../src/TrinoSqlEngine/AstSecurityVisitor.cs) transforms AST projection expressions directly into dialect-specific SQL functions:
     - **T-SQL:** `ROUND(...) + ((CHECKSUM(...) % 100) / 10000.0)` for `GEO_JITTER`; `LEFT(col, 4) + REPLICATE('*', LEN(col) - 8) + RIGHT(col, 4)` for `PARTIAL_MASK`.
     - **PostgreSQL:** `ROUND(...) + ...` and `overlay(...)` or `substr(...)`.
     - **DuckDB & SQLite:** Native mathematical and string concatenation functions.
   - Result: Masking executes directly inside the target database engine at bare-metal speed before data crosses the network.

2. **In-Memory Streaming Mode:**
   - [`ColumnMaskingProvider`](../../src/Autheris.Application/Services/ColumnMaskingProvider.cs) evaluates expressions directly on in-memory buffers (such as cached tables, Arrow record batches, or DuckDB OLAP readers) using zero-allocation `Span<char>` and `System.Text.Rune` primitives.

---

## Configuration Example

```yaml
models:
  - name: customer_orders
    columns:
      - name: credit_card_number
        meta:
          autheris:
            mask: PARTIAL_MASK
            mask_prefix: 4
            mask_suffix: 4
            mask_char: "*"
      - name: delivery_latitude
        meta:
          autheris:
            mask: GEO_JITTER
            jitter_radius_meters: 500
      - name: customer_id
        meta:
          autheris:
            mask: TOKENIZATION
```
