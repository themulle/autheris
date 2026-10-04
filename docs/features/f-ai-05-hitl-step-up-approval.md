# F-AI-05: Human-in-the-Loop Step-Up Approval\n\n**Status:** [Done] (100% GA – Wave 3)  \n**Komponenten:** [`HitLStepUpApprovalService.cs`](file:///root/lis-git/gql/gql/src/Autheris.Application/Mcp/Services/HitLStepUpApprovalService.cs), `HitLEndpoints.cs` (`/api/governance/hitl/approve`, `/reject`)\n\n---\n\n## 1. Übersicht & Problemstellung
Benötigt ein Agent Zugriff auf besonders sensible Daten (DSGVO Art. 9, unmaskierte PII), soll er weder blind zugreifen dürfen noch starr mit `403` abbrechen.

## 2. Architektur & Umsetzung
- Pausiert die Ausführung im MCP-Protokoll.
- Löst einen interaktiven 4-Augen-Freigabeprozess über ITSM (ServiceNow/Slack) an.
- Anti-Self-Approval, striktes Timeout mit Fail-Closed und automatische Redis-Epochen-Invalidierung.

## 3. Business Value
- Ermöglicht kontrollierte Sonderfreigaben für autonome Workflows unter Einhaltung strengster Compliance.\n