# ADR-007: Permissive Vereinigungssemantik für widersprüchliche Consents (F-CONS-07 D1)

## Status
Akzeptiert

## Kontext
Ein Benutzer kann über mehrere Pfade Berechtigungen erhalten: direkte Benutzerfreigabe, Mitgliedschaft in AD-Gruppen und fachliche Rollen. Es stellt sich die Frage, wie widersprüchliche Freigaben aufgelöst werden (z. B. Gruppe hat `salary = MASK`, Benutzer hat unbeschränkten Zugriff `salary = CLEAR`). Mögliche Modelle sind Spezifitätsvorrang (User > Gruppe > Rolle) oder Vereinigungssemantik (permissivstes Ergebnis).

## Entscheidung
Wir legen die **permissive Vereinigungssemantik nach F-CONS-07** verbindlich fest:
1. **Hartes DENY:** Existiert in der Menge $D$ der aktiven Deny-Consents ein Tabellen-DENY, wird der Zugriff verweigert. Existiert ein Spalten-DENY in $D$, wird die Spalte gesperrt, unabhängig von jedem ALLOW.
2. **Zero Trust:** Ist die Menge $A$ der aktiven Allow-Consents leer, wird der Zugriff verweigert.
3. **Spaltenstufe:** Ohne hartes DENY gilt das **Maximum über alle Consents in A** (`CLEAR (2) > MASK (1) > DENY (0)`). Ein Consent ohne explizite Spaltenregel liefert `CLEAR`.
4. **Zeilenfilter:** Bedingungen innerhalb eines Consents werden mit `AND` verknüpft, verschiedene Consents in $A$ mit `OR`. Besitzt mindestens ein Allow-Consent keinen Zeilenfilter, ist der Zugriff auf alle Zeilen freigegeben.

## Konsequenzen
### Positiv
- Deterministisches, intuitiv verständliches Modell für Data Owner und Benutzer.
- Kein unerwarteter Rechteverlust durch das Hinzufügen zu einer restriktiveren Gruppe.
- Vollständig formale Wahrheitstabelle mit 100% TDD- und Property-Based-Test-Abdeckung.

### Negativ
- Bei gleichzeitig abweichenden Zeilen- und Spaltenregeln auf derselben Tabelle droht Klartext-Auslieferung für maskierte Zeilen. Entscheidung D2 verhindert dies durch Validierung beim Erteilen bis Phase 4.
