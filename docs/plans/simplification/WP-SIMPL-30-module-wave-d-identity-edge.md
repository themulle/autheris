# WP-SIMPL-30 Module wave D: identity and edge modules (SCIM, TOTP, Kerberos/NTLM, Envoy)
Status: Cancelled (OD-21, OD-24, OD-27)
Parent: [plan](../2026-11-05-plan-project-simplification.md#wp-simpl-30-module-wave-d-identity-and-edge-modules) | Issues: #515 | Owner decisions: OD-21 (authentication and authorization are core; this WP is cancelled), OD-24 (OData core; the module pilot is WP-SIMPL-27), OD-27 (on-premises AD groups via core SCIM), OD-9 | Security conditions: none (nothing is implemented)

> **Cancelled by owner decision OD-21 (2026-11-05):** SCIM, TOTP, Negotiate (Kerberos/NTLM) and Envoy ext_authz stay in core and are NOT moved to modules. Do not implement anything from this WP. The file is kept so that existing links resolve; the former module-extraction body was removed so it cannot be implemented by mistake.

## 1. Goal and outcome
None. No module is created for SCIM, TOTP, Negotiate or Envoy ext_authz; `Microsoft.AspNetCore.Authentication.Negotiate` stays in `Autheris.Api`.

## 2. Where the former scope went
| Former item | Now | Owner |
|---|---|---|
| SCIM endpoints, SCIM store, IP allowlist | Core; SCIM is the path for on-premises AD users, groups and memberships (OD-27) | [WP-SIMPL-43](WP-SIMPL-43-authentication-authorization-core.md) |
| TOTP local authentication | Core (OD-21) | [WP-SIMPL-43](WP-SIMPL-43-authentication-authorization-core.md) |
| Negotiate (Kerberos/NTLM) and its package | Core (OD-21) | [WP-SIMPL-43](WP-SIMPL-43-authentication-authorization-core.md) |
| Envoy ext_authz endpoints and service | Core decision endpoint; decides through `IAccessDecisionPoint` (OD-21, OD-30) | [WP-SIMPL-43](WP-SIMPL-43-authentication-authorization-core.md) step 11, [WP-SIMPL-46](WP-SIMPL-46-unified-authorization-model.md) |
| Runtime-vs-fallback module loading decision | Taken in the Lakehouse pilot (OD-24, OD-37) | [WP-SIMPL-27](WP-SIMPL-27-module-lakehouse.md) |
| Removal of the `Autheris.Api` -> `Autheris.Extensions` project reference | Last module WP | [WP-SIMPL-29](WP-SIMPL-29-module-wave-c-events-lineage.md) |
| Procedures, governed aggregates (borderline modules) | Core (OD-18, OD-11) | [WP-SIMPL-39](WP-SIMPL-39-procedures-clean.md), [WP-SIMPL-18](WP-SIMPL-18-borderline-triage-execution.md) |
| `AuthSchemeRemovalTests`, default/fallback policy invariants | Not needed (no auth scheme leaves core) | - |

## 3. Dependencies
None. No other WP depends on this WP; references to "WP-SIMPL-26..30" in other specs mean WP-SIMPL-27..29.

## Decisions taken in this spec
- The body is reduced to the disposition table above (cancelled, OD-21).
