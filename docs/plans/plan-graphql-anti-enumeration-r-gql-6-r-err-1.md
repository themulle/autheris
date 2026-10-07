# Analyse & Erkenntnisse: GraphQL Anti-Enumeration & Validierungsfehler in HotChocolate 16 (R-GQL-6 & R-ERR-1)

## 1. Problemstellung & Ausgangslage
- **Befund R-GQL-6**: Katalog aufzählbar durch inkonsistente Fehlerbilder:
  - Ein unbekanntes Feld / eine unbekannte Tabelle erzeugte einen GraphQL-Validierungsfehler.
  - Eine gesperrte Spalte erzeugte `INVALID_QUERY`.
  - Eine gesperrte Tabelle erzeugte `ACCESS_DENIED`.
  - Zudem war ungeprüft, ob `GET /graphql?sdl` trotz `DisableIntrospection()` das SDL liefert.
- **Befund R-ERR-1**: `INVALID_QUERY` steht auf der Whitelist in `ErrorSanitizingFilter`, wodurch Spalten- und Tabellennamen in Produktion an den Client zurückgespiegelt werden (Enumeration).

---

## 2. Zentrale architektonische Erkenntnisse zu HotChocolate 16

### Erkenntnis 1: `IErrorFilter` greift in HotChocolate 16 NICHT bei Validierungsfehlern
- **Pipeline-Trennung in HotChocolate 16**: In HotChocolate 16 ist die Request-Pipeline strikt in Validierung (`DocumentValidator` / `ValidationStep`) und Ausführung (`ExecutionStep`) unterteilt.
- Validierungsfehler (wie `The field \`non_existent_table\` does not exist on the type \`Query\`.`) werden direkt durch den `DocumentValidator` erzeugt und in das `OperationResult` geschrieben.
- **`IErrorFilter` (und damit `ErrorSanitizingFilter`) wird ausschließlich in der Execution-Phase (bei Resolver-Fehlern / Laufzeit-Exceptions) aufgerufen.**
- Selbst wenn ein `IErrorFilter` eine Exception wirft, wird er bei Validierungsfehlern gar nicht erst angesprungen!
- Validierungsfehler besitzen in HotChocolate 16 standardmäßig `error.Code == null` und enthalten stattdessen strukturierte Extensions (`type: "Query"`, `field: "non_existent_table"`, `responseName: "..."`, `specifiedBy: "https://spec.graphql.org/September2025/#sec-Field-Selections"`).

### Erkenntnis 2: Was zur vollständigen Behebung von R-GQL-6 in HotChocolate 16 nötig wäre
- Ein `IErrorFilter` reicht architektonisch nicht aus, um Dokument-Validierungsfehler abzufangen oder zu maskieren.
- Hierfür wäre erforderlich:
  1. Entweder eine eigene **Request-Middleware** im HotChocolate-Request-Pipeline-Stack (`.Use(...)`), die nach `_next(context)` das `IExecutionResult` bzw. `OperationResult` inspiziert und `result.Errors` sanitisiert;
  2. Oder ein Custom `IDocumentValidatorRule` / Custom `DocumentValidator`, der unbekannte Felder unterdrückt bzw. generisch formuliert;
  3. Oder Persisted Queries Only (`PersistedQueriesOnly = true`), wodurch beliebige Ad-hoc-Queries in Produktion ohnehin blockiert werden (ADR-konform).

### Erkenntnis 3: `EnableSchemaRequests`
- In HotChocolate 16 wird SDL-Download via `opt.EnableSchemaRequests` auf `GraphQLServerOptions` gesteuert.
- Standardmäßig erlaubt HotChocolate `GET /graphql?sdl`.
- Das Setzen von `EnableSchemaRequests = false` im Endpoint-Setup verhindert den SDL-Download erfolgreich.

---

## 3. Status
Entsprechend Benutzeranweisung wurden die unfertigen Code-Änderungen an `ErrorSanitizingFilter` und `CatalogGraphQlSchemaTests` zurückgerollt. Die Erkenntnisse sind hier und im Gesamtstatusplan dokumentiert. R-GQL-6 und R-ERR-1 verbleiben als offen/dokumentiert, bis eine vollständige Middleware-basierte Lösung oder Persisted-Queries-Erzwingung umgesetzt wird.
