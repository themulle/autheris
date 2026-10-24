# Candidate defect: a numeric literal above the decimal range makes the compile throw an unhandled OverflowException

Issue draft for a candidate product defect found by the parser fuzz mutation smoke (REQ-SIMTEST P3). It is not a KnownGap: a crash can never be one
(SEC-SIM-17). Until it is fixed or owner-quarantined with an issue number (quarantine.json, owner approval), the `sim-parser-fuzz` jobs stay red.
Tracked in GitHub issue #343.

Kind: Crash
Error class: `Crash:System.OverflowException` (not a documented `TrinoSqlEngine` rejection)

## Observation

`FastSqlEngine.Compile` (governed compile, PostgreSql dialect, fuzz context `FuzzCompileContext`) throws `System.OverflowException` for a numeric literal
that does not fit a `decimal`. `FastSqlEngine.Parse` accepts the same text. The exception escapes from
`SqlDialectGeneratorBase.FormatBoundLiteral` (`src/TrinoSqlEngine/Ast/Generators/SqlDialectGeneratorBase.Functions.cs`, `case LiteralType.Decimal`,
`double dbl => (decimal)dbl`). Found 16 times in 503,000 mutated inputs (seeds 1 to 6 of `--mutate`), always the same root cause.

Minimal inputs (each is the whole statement text, UTF-8, no trailing newline):

```
SELECT 1E31
SELECT 1e999
SELECT 1.5E400
SELECT 567890123456789018901234567890
```

Boundary: `SELECT 12345678901234567890` (20 digits) is accepted; a literal around 7.9e28 and above (decimal max) crashes. The cast of a `double` outside the decimal
range is the suspected cause (an integer literal with more than about 28 digits is carried as a double).

## Expected

A typed, governed rejection (for example `SqlLimitExceededException` or `AstBuildException`, the same class of refusal as other out-of-range literals) so the
caller answers a client error, not an unhandled exception.

## Actual

`System.OverflowException` ("Value was either too large or too small for a Decimal.") escapes `Compile`. On the API path this is an unhandled server error for
user-supplied SQL (not verified through the API in this run).

## Repro

```
printf 'SELECT 1E31' > /tmp/overflow.sql
dotnet run --project tests/Autheris.Tests.Fuzz -c Release -- --replay /tmp/overflow.sql
```

Prints `Crash ... System.OverflowException ... FormatBoundLiteral` and exits 1. The fix lands together with the input committed under
`tests/Autheris.Tests.Fuzz/findings/crashes/` (sha256 `65486ba28716279886fe7650898e2fe282966e7f78487eee921d90944dafd891` for `SELECT 1E31` without a trailing newline,
verify with `sha256sum`).
