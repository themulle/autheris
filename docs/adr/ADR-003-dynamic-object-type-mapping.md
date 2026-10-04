# ADR-003: Dynamisches Hot Chocolate Schema-Mapping für bis zu 50.000 Tabellen

## Status
Akzeptiert

## Kontext
Im Enterprise-Maßstab existieren bis zu 50.000 Datenbanktabellen. Ein statisches C#-Klassenmodell oder Code-First-Generierung zur Compile-Zeit ist ausgeschlossen (Kompilierzeit, Speicherverbrauch, Schema-Größe bei Introspection).

## Entscheidung
1. **Laufzeit-Metadatenkatalog:** Typen werden dynamisch über Hot Chocolate `ObjectTypeDescriptor` anhand von `TableMetadata` registriert.
2. **Speichereffizienter Dictionary-Resolver:** Tabellenzeilen werden als `IReadOnlyDictionary<string, object?>` gestreamt, ohne Laufzeit-IL-Code-Generierung.
3. **Domänen-Segmentierung (Namespaces):** Abfragen werden nach Fachdomänen unterteilt (`query { finance { invoices { ... } } }`), um Namenskollisionen zu vermeiden und Schema-Subgraphen schlank zu halten.
4. **Introspection-Schutz:** Schema-Introspection ist in Produktion deaktiviert (NF-SEC-02).

## Konsequenzen
### Positiv
- Skalierbarkeit für zehntausende dynamische Tabellen.
- Keine Notwendigkeit für App-Neustarts bei Metadaten-Hinzufügungen.
- Minimale RAM-Auslastung pro Zeile.

### Negativ
- Fehlende statische Compile-Zeit-Typprüfung für Entity-Modelle im Datenpfad (kompensiert durch Metadaten-Validierung).
