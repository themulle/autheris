# KnownGap SIM-GAP-101: masked columns are nullable in responses but not in the OData OpenAPI schema

Issue draft for a documented schema-conformance class found by the Schemathesis API fuzz (REQ-SIMTEST P2). It is a `SchemaNonConformance`,
not a security class: the masking itself works (the canary of the masked column never appears in a response).

Kind: SchemaNonConformance
Error class: `ST108 Response violates schema | null is not of type '?'`

## Observation

- `GET /odata/v4/default/main/sim_accounts` (a table whose `secret_note` column has a `NULLIFY` masking rule) answers 200 with `"secret_note": null`.
- The generated OpenAPI document (`GET /odata/v4/$openapi`) declares the property as `{"type": "string"}` without `nullable` (OpenAPI 3.1: `["string", "null"]`), so Schemathesis reports ST108 "Response violates schema".
- Seed 20261024; first seen in the read phase against `Autheris.Sim.ApiHost`.

## Expected

The dynamic OpenAPI generator marks columns that can be masked to null (and nullable columns) as nullable, so a conforming client does not reject masked responses.

## Repro

```
dotnet build tests/Autheris.Sim.ApiHost -c Release -p:AutherisIncludeTestAuth=true
dotnet tests/Autheris.Sim.ApiHost/bin/Release/net10.0/Autheris.Sim.ApiHost.dll --port 5071 --data-dir "$TMPDIR/sim-data" --ready-file "$TMPDIR/ready" --profile read
# then, with the Schemathesis CLI pinned in scripts/sim/run-api-fuzz.sh:
st run <odata schema file> --url http://127.0.0.1:5071/odata/v4 --seed 20261024 --include-path /default/main/sim_accounts --include-method GET
```

Remove the entry from `ApiFuzz/api-known-gaps.json` when the schema is fixed (strict xfail).
