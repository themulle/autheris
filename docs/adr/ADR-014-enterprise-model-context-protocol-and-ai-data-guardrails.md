# ADR-014: Enterprise Model Context Protocol (MCP) Server & AI Data Guardrails

## Status
Akzeptiert

## Kontext
Unternehmen stehen vor der Herausforderung, generative KI-Modelle und autonome Agenten (Anthropic Claude, OpenAI, AutoGen, LangChain) an relationale und analytische Datenbestände anzubinden.
- **Problem**: Direkte relationale Datenbankabfragen oder ungeschützte REST/GraphQL-APIs bergen massive Risiken von PII- und DSGVO-Art.-9-Datenlecks, Prompt Injections und ausufernden LLM-Token-Kosten durch überdimensionierte Payloads.
- **Marktlücke**: Weder Apollo GraphOS noch Hasura DDN bieten native Model Context Protocol (MCP) Server mit integrierten Zero-Trust-Guardrails für KI-Agenten.

## Entscheidung
Wir etablieren einen nativen, standardkonformen **Model Context Protocol (MCP) Server** (Spezifikation 2024-11-05) direkt im `Autheris`:
1. **Transport & Protokoll**:
   - `GET /mcp/sse`: Server-Sent Events (SSE) Handshake zur Etablierung langlebiger Client-Sessions (`McpSessionContext`).
   - `POST /mcp/message`: Empfang und Verarbeitung von JSON-RPC 2.0 Payloads (`initialize`, `tools/list`, `tools/call`, `ping`).
2. **Dynamische Tool-Exposition**:
   - Freigegebene GraphQL Persisted Queries werden über `IMcpToolRegistry` automatisch in typisierte MCP-Tools mit formalem JSON-Schema übersetzt.
3. **AI Data Guardrail Engine (`IAiDataGuardrailService`)**:
   - **Automatisches PII- & DSGVO-Art.-9-Scrubbing**: E-Mail-Adressen, IBANs und sensible Gesundheits-/Biometriedaten werden automatisiert maskiert (`u***@domain.com`, `****1234`, `[REDACTED-GDPR-ART9]`), *bevor* die Daten an das LLM-Kontextfenster gestreamt werden.
   - **Token-Budgeting**: Berechnet geschätzte Token-Größen und trunkiert Ergebnisse kontrolliert bei Überschreitung von `MaxTokensPerCall`.
   - **Sicherheits-Insecure-Modi**: `danger_bypass_mcp_auth` (in Produktion verboten) und `warn_allow_unmasked_ai_access` für Dev/Sandbox-Tests.

## Konsequenzen
### Positiv
- Klare Marktdifferenzierung als erstes Enterprise GraphQL Gateway mit integrierter KI-Agent-Absicherung.
- Verhinderung von DSGVO-Verstößen beim Einsatz generativer KI im Unternehmenskontext.
- Standardisierte Anbindung ohne proprietäre SDKs über die offene MCP-Spezifikation.

### Negativ / Risiken
- SSE-Streams erfordern langlebige HTTP-Verbindungen. Gegenmaßnahme: Saubere Session-Timeouts, Keepalive-Pings alle 15 Sekunden und explizite Teardown-Endpunkte (`DELETE /mcp/session/{id}`).
