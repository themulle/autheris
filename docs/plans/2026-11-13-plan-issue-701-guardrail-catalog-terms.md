# PLAN-OI-701: MCP input guard rejects ordinary catalog search terms

- Date: 2026-11-13 · Base: `origin/main` @ `54c54b80` · Issue: [#701](https://github.com/themulle/autheris/issues/701)
- Origin plan: [`00-master-plan-overview.md`](00-master-plan-overview.md) (rank 23).
- Freeze (PRD 5.1): bug fix of an existing heuristic; no new parameter, field, endpoint, option or switch. No `freeze-exception` needed.
- Size: 1 regex line in `src/` (well below the 400-line limit).

## Phase 1: Requirement

- **Problem:** `search_catalog` with `query: "Leakage Prüfung"` or `"export allocation"` is denied with
  `PrivilegeCoercion`. Each denial writes an audit `DENY` and publishes a `SecurityAlertEvent` (severity HIGH), so
  ordinary catalog searches produce security noise and the client loses the call.
- **Root cause:** the exfiltration / coercion pattern in `SemanticPromptGuardrail.ExfiltrationCoercionRegex` has no word
  boundaries and contains the bare word `leak`: `leak` matches "Leakage", "leak test", "oil leak"; `export\s+all`
  matches "export allocation"; `drop\s+table` matches "drop tablespace".
- **Requirement REQ-701-1:** ordinary German and English catalog vocabulary passes the input guard for every argument
  (`query`, `search`, `use_case`).
- **Requirement REQ-701-2:** the coercion phrases stay blocked (`exfiltrate`, `export all`, `dump database`,
  `drop table`, `grant admin`, `elevate privileges`, `disable guardrails`); the bare word `leak` is replaced by the
  phrases `leak all`, `leak every`, `leak the entire|whole|full`.
- **Anti-patterns rejected:** an allow-list of terms that bypasses the guard (would become a bypass vector); exempting
  search arguments from the guard as a whole (a policy change for Phase 3 of a separate item, not a bug fix).

## Phase 2: Design

- Wrap the alternation in `\b...\b` (Unicode word boundaries of .NET; umlauts are word characters).
- Inflected forms that the old substring match caught stay caught explicitly: `exfiltrat(?:e[sd]?|ing|ion)`,
  `databases?`, `tables?`, `admin\w*` (keeps "grant administrator"), `privileges?`, `guardrails?`.
- `dump\s+(?:the\s+)?databases?` additionally covers "dump the database" (small hardening, same attack class).
- The output path (`forOutput`) is unchanged: the coercion pattern does not gate tool output.

## Phase 3: Security review

| Check | Result |
|---|---|
| Fail-closed | Unchanged: no new allow path; a match timeout behaves as before (not caught in the guard, so the call fails). |
| Weakening | Only substring matches inside longer words and the bare word `leak` stop matching. `leak` alone is not an instruction; the instruction forms (`leak all ...`) stay blocked. The guard remains a heuristic, not a security boundary (SEC M-29); SQL DDL is still rejected by the SQL engine for `query_sql`. |
| Evasion | Word boundaries do not open a new evasion: NFKC normalization, zero-width stripping, JSON-unescape and base64 decoding run before the match as before. Joining a phrase to a word (`dropTABLEx`) was not a realistic attack form against the old pattern either. |
| ReDoS | Alternation of literals with bounded `\s+`; 200 ms match timeout kept. |
| Tests | `McpPromptGuardrailCatalogTermTests` (`[Trait("Category","Security")]`): 15 ordinary terms pass in three argument names; 15 coercion phrases stay `PrivilegeCoercion`. Existing injection tests stay green. |

Open for a later item (not this fix): whether search-only arguments (`query`, `search`, `use_case`) should get a milder
policy than instruction-like arguments. Phrases such as "drop table height" (a measurement name) are still denied.

## Phase 4 and 5

- Branch `fix/701-guardrail-word-boundaries`; red test first, then the regex change.
