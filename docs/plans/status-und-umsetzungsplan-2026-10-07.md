# Status und Umsetzungsplan (Stand 1133f90, 2026-10-08)

Branch `feat/ast-target-dialect-generator`. Dieses Dokument listet nur noch **offene** Punkte. Erledigte Befunde
stehen in der Git-History (Commit-Messages nennen die IDs). Die IDs stammen aus
[security-review-2026-10-07.md](security-review-2026-10-07.md) (SQL-, SQL2-, POL-, GQL-, MCP-, API-, EXT-, INF-, DEP-)
und [review-c4f2c32.md](review-c4f2c32.md) (R-…); F- und E- aus den Casbin-Nachreviews.

Regeln: Ein Thema pro Commit. TDD: Jeder Sicherheitstest muss ohne Fix rot sein. Vor dem Push grüner
`dotnet build` und `dotnet test` (Integrationstests unter podman mit `TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal`).

## 1. Außerhalb dieses Repos

| ID | Punkt |
|---|---|
| DEP-1/2/3 | PoC-Repo (`talos/POC_Backstage_citizen_dev`), mit dem Owner abstimmen: Passwörter und HMAC-Schlüssel rotieren, Defaults aus `compose.yaml` entfernen (`${VAR:?}`), Ports nur an 127.0.0.1, Test-DB oder Read-only-Replikat statt LWETEM_PROD. |
| DEP-5-Rest | Altes ghcr-Paket (`…/gql`) manuell löschen; das Image heißt jetzt `ghcr.io/themulle/autheris`. |
| CI | PR nach `main` öffnen, damit die CI läuft (läuft nur auf `main`/PRs). |



