# Feature-Request R-52: Klartext je Person (Ausnahme von der Maskierung) über die API statt per SQLite

**Stand:** 09.10.2026, Autheris v1.1.5. Quelle: PoC `POC_Backstage_citizen_dev`. Ergänzt [Befunde v1.1.5](2026-10-09-poc-befunde-v1-1-5.md) (B-02, R-50).

## Anforderung
- **`david`** soll für alle Objekte, für die er freigegeben ist, **alle Felder im Klartext** sehen (Ausnahme von der Maskierung).
- **`philipp`** hat dieselbe Zeilenfreigabe (nur nicht ausgelieferte Krane), es sollen aber **die normalen Maskierungsregeln** gelten.
- Die Maskierungsregeln selbst (dbt-Governance, `POST /api/extensions/dbt/governance`) bleiben für alle anderen unverändert.

## Heutige Umsetzung im PoC (funktioniert, ist aber ein Umweg)
`autheris/scripts/grant_row_scoped_user.py` schreibt Einwilligungen direkt in die SQLite-Datei: je Tabelle eine `CONSENTS`-Zeile mit Zeilenfilter und `CONSENT_COLUMN_RULES` mit Zugriffsstufe Clear (2) für **alle** Spalten bei david, bei philipp Mask (1) für sensible Spalten. Geprüft (Stand 09.10.2026): gleiche Kranzahl (7 226), keine ausgelieferten Krane, david sieht `customer_number` und GPS-Positionen im Klartext, philipp `NULL`.

## Was fehlt
| Punkt | Folge |
|---|---|
| **Kein API-Weg**, der Einwilligungen mit Zeilenfilter (auch Unterabfrage über `md.crane`) und Spaltenstufen für eine Person in einem Zug anlegt | Das Skript schreibt in die Datenbank, der Container muss gestoppt sein |
| **Keine Audit-Einträge** (`CONSENT_GRANTED`) bei diesem Weg | Die Freigabe von Klartext für eine Person ist nicht nachvollziehbar |
| **Kein „Klartext für Person X“ als eigenes Konzept** | Bei jeder neuen Spalte oder Tabelle ist der Klartext nur durch Neuberechnung aller Einwilligungen sichergestellt |
| **Der dbt-Import kann die Regeln nicht deklarativ je Person abbilden** (R-50, `accessProfilesCount` bleibt 0) | `dbt_sample/governance/access` (Profile) wirkt nicht im Gateway |

## Vorschlag
1. **Zugriffsprofil mit Maskierungsmodus** `unmasked` (alle Spalten Clear) oder `default` (Regeln der Spalten) je Person oder Rolle, im Import `POST /api/extensions/dbt/governance` aus `access/` übernommen (löst R-50).
2. **Dasselbe Profil** trägt den Zeilenfilter (`target`-Prädikat der virtuellen Filter aus [Plan](2026-10-08-umsetzungsplan-virtuelle-filter.md)), sodass david und philipp sich nur im Maskierungsmodus unterscheiden.
3. **Audit**: Vergabe und Entzug als `CONSENT_GRANTED` / `CONSENT_REVOKED` mit Person, Modus und Profil. Klartext verlangt eine Begründung und eine Gültigkeitsdauer.
4. **Importmodus `replace`** für Maskierungsregeln (B-02), damit eine Lockerung im dbt wirkt.
5. Bis dahin: ein dokumentierter Admin-Endpunkt `POST /api/v1/consents/bulk` (Person, Tabellenliste, Zeilenfilter, Spaltenstufe).

## Abnahme
- Zwei Nutzer mit gleicher Zeilenfreigabe: Klartext bei A, Maskierung nach Regeln bei B, ohne Datenbankzugriff und ohne Containerstopp.
- Audit-Eintrag je Vergabe. Eine neue Tabelle im Katalog erscheint beim Klartext-Profil ohne Nacharbeit im Klartext.
- Prüfung im PoC: `scripts/verify-autheris.sh`, Abschnitt „david (Klartext) und philipp (maskiert)“.

## Grenzen
Die Aussage zum fehlenden API-Weg beruht darauf, dass ich im PoC keinen Endpunkt zum Anlegen von Einwilligungen mit Unterabfrage-Zeilenfilter gefunden habe; ich habe die API-Dokumentation und den Quelltext dazu nicht vollständig durchsucht.
