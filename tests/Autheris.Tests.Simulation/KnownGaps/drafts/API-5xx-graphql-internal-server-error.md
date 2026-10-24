# Candidate defect: GraphQL answers INTERNAL_SERVER_ERROR (an unhandled exception behind a masked error) for valid, argument-light queries

Issue draft for a candidate product defect found by the Schemathesis API fuzz in GraphQL mode (REQ-SIMTEST P2). It is not a KnownGap: an unhandled
server error can never be one (SEC-SIM-18). Until it is fixed or owner-quarantined with an issue number, the `sim-api-fuzz` job stays red.

Kind: ServerError
Error class: `ServerError:GraphQL:200:INTERNAL_SERVER_ERROR` (HTTP 200 with an `errors` entry whose `extensions.code` is `INTERNAL_SERVER_ERROR`); also seen with HTTP 400.

## Observation

On the sim host (seed 20261024, read and write profile) 234 of about 500 GraphQL requests were answered with `An internal server error occurred.` The product masks the
exception message, so the root cause was not triaged. Hand-reproduced shapes (authenticated, the sim host's default catalog and SQLite data source):

1. Catalog entity fields of tables that have no backing table on the data source or are governance system tables, with any arguments, even `first: 1`:
   `{ finance_dbo_finance_items(first: 1, offset: 0) { sensitive_note_2 } }`, `{ governance_system_policies(first: 1, offset: 0) { classification_tags } }`
   (212 of 234). The same request on the seeded table `default_main_sim_accounts` works. The OData route for the same catalog entries answers 403 `SQL_POLICY_DENIED`, so GraphQL is the only channel that surfaces an internal error.
2. Query fields called with empty-string arguments:
   `{ table(domain: "", name: "", schema: "", first: 0, after: 0) { jsonRows } }`, `{ myAccess(table: "") { consentDescriptionOrigin } }`,
   `myActiveConsents(table: "", ...)`, `tableConsumers(domain: "", ...)`, `gdprDataDisclosureReport(...)`, `exportGdprDataDisclosureReportPdfBase64(...)`,
   `{ finance { invoices(first: 0, after: 0) { jsonRows } } }`, `{ hr { employees(first: 0, after: 0) { jsonRows } } }`.
3. Mutations with unknown identifiers or empty text (write profile): `rejectConsentRequest`, `requestTableAccess`, `revokeConsent`, `syncDataCatalog(dryRun: false)`,
   `calculateConsentRevocationImpact` with a random GUID (the last one answers `NOT_FOUND` for some ids and `INTERNAL_SERVER_ERROR` for others).

Governed client errors in the same run carry stable codes and are not part of this defect: `AGGREGATION_ARGUMENT_INVALID`, `QUERY_TOO_COMPLEX`,
`GRAPHQL_VALIDATION_FAILED`, `INVALID_QUERY`, `GRAPHQL_VARIABLE_INVALID`, `NOT_FOUND`.

## Expected

Every resolver maps unknown tables, missing backing tables, empty arguments and unknown ids to a governed error code (`NOT_FOUND`, `INVALID_QUERY`, ...) and never
to `INTERNAL_SERVER_ERROR`.

## Repro

```
dotnet build tests/Autheris.Sim.ApiHost -c Release -p:AutherisIncludeTestAuth=true
dotnet tests/Autheris.Sim.ApiHost/bin/Release/net10.0/Autheris.Sim.ApiHost.dll --port 5073 --data-dir "$TMPDIR/sim-data" --ready-file "$TMPDIR/ready" --profile read
# POST {"query":"{ finance_dbo_finance_items(first: 1, offset: 0) { sensitive_note_2 } }"} to http://127.0.0.1:5073/graphql
# authenticated with the sim host's reader test-auth identity (documented in scripts/sim/run-api-fuzz.sh)
```

Schemathesis form: `st run http://127.0.0.1:5073/graphql --seed 20261024 --max-examples 5 --checks not_a_server_error`
