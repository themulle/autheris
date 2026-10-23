# PRD: Issue #281 - Runtime-managed, app-scoped trusted GraphQL documents

ID: `REQ-TD-281`. Phase 1 (product-manager). Source: GitHub issue #281.

## Problem
Talos static browser apps (citizen-built, four-eyes reviewed) should call `/graphql` directly with the viewer's Entra token. Without a per-app allow-list inside Autheris, app code could send arbitrary queries with the viewer's rights (confused deputy). Today only a global, file-based `TrustedDocumentsDirectory` exists, with no per-app scoping and no runtime management.

## Binding owner decisions
1. App identity = header `X-Autheris-App: <appId>/<version>`, bound to a per-app allowed origin. No app token (a secret in a static page is public).
2. Storage = governance database, new tables inside the versioned migrations (SQLite, PostgreSql, SqlServer). No upgrade/repair migrations.
3. Hash = SHA-256 over the exact document text. No normalization. The release pipeline computes it byte for byte.

## Requirements
- REQ-281-1 Model: app (`appId`, `AllowedOrigins`), app version (`appId`, `version`, status active/retired), document hash set (SHA-256 hex, per app/version). Tables added to the versioned governance migrations for all three providers; parity test across providers.
- REQ-281-2 Admin API `PUT /admin/trusted-documents/{appId}/{version}` (hash list; idempotent; replaces the set; carries `AllowedOrigins` for the app), `DELETE .../{appId}/{version}` (retire), `GET /admin/trusted-documents/{appId}` (list with status). Hash format validated (64 hex, normalized to lower case); set size and count bounded; invalid input returns 400.
- REQ-281-3 Admin authorization: dedicated scope `trusted-documents:write` (write) and a read scope for GET. Missing/insufficient scope returns 403. Fail-closed on unresolved identity. Admin routes are on the internal admin surface only, never via public ingress (same exposure mechanism as the existing admin API).
- REQ-281-4 Audit: every write/retire appends to the existing append-only audit chain with actor, appId, version, added and removed hashes (counts plus hashes, no document text).
- REQ-281-5 Gateway enforcement: when `X-Autheris-App` is present and `PersistedQueriesOnly` is on, a document is accepted only if its hash is in the active set of that app/version. Unknown app, unknown or retired version, or unknown hash is rejected with the existing persisted-query error shape (no oracle: identical message for all causes).
- REQ-281-6 Malformed header (not `appId/version`, oversize, illegal chars, multiple header values) is rejected fail-closed with the same error shape; never falls back to the global directory.
- REQ-281-7 Origin binding: request `Origin` must match the app's `AllowedOrigins` (exact scheme/host/port match). Mismatch or missing Origin for an app with configured origins is rejected with the same error shape. An app without any configured origin is rejected on registration unless the owner decides otherwise (see OQ-2).
- REQ-281-8 Runtime refresh: writes and retires take effect without restart (in-memory cache invalidated on write; in a multi-instance deployment via the existing cross-instance invalidation mechanism or a bounded TTL, see OQ-3). Cache miss reads the DB; DB failure rejects (fail-closed).
- REQ-281-9 Narrowing only: the decision runs before execution; consent, column masks and row rules still apply afterwards. The allow-list never widens access.
- REQ-281-10 Backwards compatibility: with no `X-Autheris-App` header the existing global `TrustedDocumentsDirectory` logic applies unchanged. With the feature disabled (new option off) behaviour is identical to today and admin routes are not mapped.
- REQ-281-11 Configuration: options under the existing gateway/security configuration section (feature switch, header name, max set size, cache TTL), validated at startup, safe defaults (off).
- REQ-281-12 Header is a client claim, not proof: protection is that sets are written only by the release service. Logs never include document text; hashes only.
- REQ-281-13 Reference docs: minimal updates to `docs/configuration-guide.md` (new options) and `docs/endpoints-and-testing.md` (admin routes), no progress notes.

## Acceptance criteria
1. Document registered for A/1 runs for A/1; rejected for A/2, for B/1 and without header (global rules then apply as today).
2. Register/retire takes effect without restart; audit entry holds actor and hash delta.
3. Admin routes unreachable via public ingress; unauthorized call returns 403.
4. Feature off: behaviour unchanged, global directory works, routes absent.
5. Origin mismatch rejected; retired version rejected; malformed header rejected; DB failure rejected.
6. Tests: middleware unit tests (match, mismatch, retired, missing/malformed header, origin mismatch, cache refresh), admin API integration test (all three providers where Testcontainers apply), audit entry test, migration parity test, 403 test.

## Out of scope
App tokens / client credentials, document normalization, automatic discovery of documents, UI for management, optional separate issue items from the issue text.

## Open questions (recommended default)
- OQ-1 Reference docs: issue requests both doc updates, lifecycle has no docs phase. Default: developer makes minimal updates to the two reference docs in the same branch.
- OQ-2 App without `AllowedOrigins`: default reject registration (origin binding mandatory) since decision 1 says "bound".
- OQ-3 Multi-instance cache coherence: default existing invalidation channel if present, otherwise short TTL (30 s) plus write-through on the writing instance; architect to confirm.
- OQ-4 Retired versions: default soft retire (row kept, status retired) for audit; re-PUT of a retired version is rejected (409) to avoid silent resurrection.
- OQ-5 Read scope name: default `trusted-documents:read`.
- OQ-6 Claim-based app identity (mentioned in the issue as an alternative): default not implemented; header only.
