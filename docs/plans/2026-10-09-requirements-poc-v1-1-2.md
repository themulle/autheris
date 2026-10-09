# Offene Anforderungen aus dem PoC „Citizen Dev“ an Autheris

**Stand:** 09.10.2026  
**Quelle der Anforderungen:** PoC `POC_Backstage_citizen_dev` (Backstage, Talos-Datenadapter, Excel/Power Query, MCP-Agenten, dbt-Governance `dbt_sample`).  
**Status:** Alle Muss- und Soll-Anforderungen (R-01 bis R-43, B-01 bis B-06, R-50 bis R-53 sowie Befunde 1.1 und 1.2) sind implementiert, verifiziert und aus den offenen Plänen entfernt.

---

## Verbleibende offene Anforderungen (Soll / Nachgelagert)

| ID | Bereich | Anforderung | Prio | Status |
|---|---|---|---|---|
| **3.1** | Arrow & OLAP | Arrow-Export und OLAP: 403 mangels ReBAC-Beziehungen; Iceberg ohne Tabellen | Soll (nur, wenn diese Wege bewertet werden) | offen |
| **3.2** | MCP | MCP: OAuth-Discovery 401 ohne Auth, CORS im Dev-Betrieb, JSON-RPC-Batches 400 | Soll (Staging/Produktion) | offen |

---

## Nächste Schritte
- Bewertung, ob ReBAC-Beziehungen für den Arrow-Export im PoC zwingend erforderlich sind.
- Härtung der MCP-Schnittstelle für Staging/Produktion (OAuth-Discovery und Batch-Verarbeitung).
