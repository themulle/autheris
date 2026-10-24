# Candidate defect: three governance POST endpoints answer 500 INTERNAL_ERROR for an empty JSON object

Issue draft for a candidate product defect found by the Schemathesis API fuzz (REQ-SIMTEST P2, write phase). It is not a KnownGap: a 5xx can
never be one (SEC-SIM-18). Until it is fixed or owner-quarantined with an issue number, the `sim-api-fuzz` job stays red.

Tracked in GitHub issue #340.

Kind: ServerError
Error class: `ServerError:500:INTERNAL_ERROR`

## Observation

Authenticated as a governance administrator on the throwaway sim host (`Autheris.Sim.ApiHost`, profile `write`), a request with
`Content-Type: application/json` and the body `{}` returns HTTP 500 with the sanitized body `code: INTERNAL_ERROR`, `title: The request could not be processed.`:

- `POST /api/governance/policy-simulation/replay`
- `POST /api/governance/sunsetting/evaluate`
- `POST /api/governance/sunsetting/rules`

The three paths answer 400 for other malformed bodies (for example missing required members of a non-empty object), so `{}` appears to reach a code path
that dereferences a missing member instead of returning a validation error. Reproduced by hand (not only by Schemathesis) on seed 20261024, 7367 requests in the run.

## Expected

A governed 400 (validation error with a stable `code`), never a 5xx, for every request body shape.

## Repro

```
dotnet build tests/Autheris.Sim.ApiHost -c Release -p:AutherisIncludeTestAuth=true
dotnet tests/Autheris.Sim.ApiHost/bin/Release/net10.0/Autheris.Sim.ApiHost.dll --port 5073 --data-dir "$TMPDIR/sim-data" --ready-file "$TMPDIR/ready" --profile write
# then POST the body {} (Content-Type: application/json) to each path above, authenticated with the sim host's governance-admin test-auth identity
# (the identity is documented in scripts/sim/run-api-fuzz.sh; no credentials are needed beyond it).
```

Schemathesis form: `st run tests/Autheris.Tests.Simulation/ApiFuzz/schemas/governance-write.openapi.json --url http://127.0.0.1:5073 --seed 20261024 --include-path /api/governance/sunsetting/rules --include-method POST`
