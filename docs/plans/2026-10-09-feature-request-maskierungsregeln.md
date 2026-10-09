# Feature-Request R-53: Weitere Maskierungsregeln (`GEO_JITTER` und Standardvokabular der Governance)

**Stand:** 09.10.2026, Autheris v1.1.5. Quelle: PoC `POC_Backstage_citizen_dev`, Vokabular `dbt_sample/governance/policies/compliance_attributes.yml` (`masking_rules`, aus `lwecatalog`). Ergänzt [Befunde v1.1.5](2026-10-09-poc-befunde-v1-1-5.md) (B-01, B-02, B-06).

## Ausgangslage
Die Governance der Liebherr-Daten kennt neun standardisierte Maskierungsregeln. Autheris (`ColumnMaskingProvider.MaskValue`) kennt davon nur einen Teil:

| Regel im Vokabular | Bedeutung | In Autheris | Wirkung im PoC heute | Spalten im PoC |
|---|---|---|---|---|
| `none` | Klartext | (keine Regel) | – | 1 432 |
| `nulling` | durch NULL ersetzen | `NULLIFY` ✔ | NULL | 0 |
| `pseudonymize` | Hash mit geheimem Schlüssel | `HMAC` / `HMAC_SHA256` ✔ | Hash | 9 |
| `email_mask` | `j***n@domain.com` | `MASK_EMAIL` ✔ | maskiert | 1 |
| `phone_mask` | `+49 171 ******89` | `MASK_PHONE` ✔ | maskiert | 0 |
| `redact_complete` | `*****` | `REDACT` ✔ (Zahlen werden `0`, siehe B-06) | Text oder `0` | – |
| **`partial_mask`** | `Mustermann → M*****` | **fehlt** | fällt auf `REDACT` zurück | **29** |
| **`geo_jitter`** | Koordinaten verrauschen oder Nachkommastellen reduzieren | **fehlt** | fällt auf `REDACT` zurück | **15** |
| **`tokenization`** | Zufallstoken über Token-Vault (Entwurf) | **fehlt** | fällt auf `REDACT` zurück | 0 |

Unbekannte Typen führt Autheris als `REDACT` aus (`default`-Zweig, `rule.Replacement ?? "REDACTED"`). Das ist fail-closed, aber für die genannten 44 Spalten unbrauchbar: Aus „Teilmaske“ wird „alles weg“, aus „Geo-Verrauschen“ wird `0` oder `[GESCHÜTZT]`. Der PoC schickt deshalb alle drei als `NULLIFY` (`dbt_sample/tools/export_autheris.py`), ein ehrlicher, aber schlechter Ersatz.

## Anforderung 1: `GEO_JITTER` (Priorität Muss)
Für Breite/Länge (Zahlenspalten, im PoC `tem.gps_position`, `conf.geo_point`, `md.service_operation`, `tem.virtual_geo_fence`, `ud.customer_geo_point`) soll eine Regel `GEO_JITTER` die Position so verändern, dass Standorte nicht mehr exakt, Bewegungsmuster auf grober Ebene aber weiter auswertbar sind.
- **Parameter je Regel** (Spalte `pattern_or_format` oder neue Optionen): `mode` = `round` (Nachkommastellen reduzieren, z. B. `decimals: 2` ≈ 1,1 km) oder `noise` (Rauschen, `radius_m: 500`).
- **Rauschen deterministisch**, nicht zufällig je Abfrage: Wird bei jeder Abfrage neu gewürfelt, mittelt man die Werte heraus. Vorschlag: Versatz aus `HMAC(key, zeilenschlüssel | spalte)`, damit dieselbe Zeile immer denselben Versatz hat. Breite und Länge brauchen **denselben** Versatz (zusammen als Punkt verschieben), sonst entstehen Positionen abseits plausibler Fahrwege.
- **Gültigkeitsbereich** der Ergebnisse: Breite −90…90, Länge −180…180; `NULL` und `0` (Zeile ohne Fix) bleiben `NULL`/`0`, sonst entstehen Phantompositionen vor Afrika.
- **Typ bleibt Zahl** (kein Text wie `[GESCHÜTZT]`), damit Karten und Filter weiterlaufen (siehe B-06).
- **Nicht nötig:** Verwischen von Linien oder Geometrien (`tem.gps_track.track`); dafür reicht vorerst `NULLIFY`.

## Anforderung 2: `PARTIAL_MASK` (Priorität Muss)
29 Spalten, vor allem Namen und Nummern (`md.customer.customer_number`, `md.customer_csm_data.kvs_*name`). `Mustermann → M*****`.
- Parameter: `keep_prefix` (Standard 1), `keep_suffix` (Standard 0), `mask_char` (Standard `*`), feste oder echte Länge (`fixed_length: true` verrät die Länge nicht).
- Der Wert `[GESCHÜTZT]` darf **nicht** als Ersatz dienen, wenn die Spalte Text ist und die Regel Teilmaskierung heißt.
- `build_governance_db.py` im PoC mappt `partial_mask` schon auf `PARTIAL_MASK`; Autheris muss den Namen kennen, sonst wirkt der Eintrag als `REDACT`.

## Anforderung 3: `TOKENIZATION` (Priorität Soll, Entwurf)
Ersetzen durch einen Zufallstoken über einen Token-Vault (laut Vokabular „Entwurf; Token-Service offen“). Erst nötig, wenn ein Token-Dienst feststeht. Bis dahin sollte der Import die Regel **ablehnen oder mit Warnung auf `HMAC` abbilden**, nicht still zu `REDACT` machen.

## Weitere Regeln, die sinnvoll wären (Vorschlag, nicht aus dem PoC-Bedarf)
| Regel | Zweck | Beispiel |
|---|---|---|
| `DATE_GENERALIZE` | Zeitstempel auf Tag, Monat oder Jahr kürzen (`granularity`) | `2026-05-27T09:36:02` → `2026-05` |
| `NUMERIC_BUCKET` | Zahlen runden oder in Klassen einteilen (`step`) | Geschwindigkeit `47,3` → `45–50` |
| `HASH_PREFIX` / `LAST_N` | letzte oder erste n Zeichen sichtbar (Kennungen, Seriennummern) | `…2077` |
| `FORMAT_PRESERVING` | Ersatzwert gleicher Form (Länge, Ziffern/Buchstaben), z. B. für Testdaten | `AB12345` → `QX90817` |
| `REDACT` typgerecht | `NULL` statt `0` bei Zahlen und Zeiten, damit Maskiertes nicht wie ein echter Wert aussieht (B-06) | – |

## Querschnittsanforderungen
1. **Eine Maskenform je Regel, gleich in allen Wegen** (WebSQL, GraphQL, OData, Prozeduren, MCP), vgl. R-25. Neue Regeln brauchen SQL-Ausdrücke je Dialekt (SQL Server, PostgreSQL, SQLite) **und** die Variante im Speicher (`ColumnMaskingProvider`); die Ergebnisse sollen dieselben sein.
2. **Filter und Sortierung auf maskierten Spalten** bleiben gesperrt (403, wie heute); für `GEO_JITTER` mit `round` wäre ein Filter auf die gerundeten Werte denkbar, ist aber nicht gefordert.
3. **Unbekannte Regelnamen** beim Governance-Import ablehnen oder warnen (B-01), nicht still `REDACT` anwenden. Kennt Autheris die Regel nicht, soll die Antwort das ausdrücklich sagen.
4. **Namen**: Autheris nutzt `GROSSBUCHSTABEN_MIT_UNTERSTRICH`, das Vokabular `kleinbuchstaben`. Der Import soll die Abbildung selbst machen (`geo_jitter → GEO_JITTER`, `partial_mask → PARTIAL_MASK`, `email_mask → MASK_EMAIL`, `phone_mask → MASK_PHONE`, `nulling → NULLIFY`, `pseudonymize → HMAC`, `redact_complete → REDACT`, `tokenization → TOKENIZE`), damit der PoC nicht mehr übersetzt.
5. **Schutz vor Rückrechnen**: Rauschen und Rundung reichen nicht gegen eine Person mit viel Hintergrundwissen; die Maskierung ersetzt keine Zugriffsfreigabe (Zeilenfilter und Einwilligung bleiben die Grenze).

## Abnahme
- Tests je Regel mit Randfällen (`NULL`, `0`, Gültigkeitsbereich bei Koordinaten, Umlaute, Länge 1 bei `PARTIAL_MASK`).
- Dieselben Eingaben liefern in WebSQL, GraphQL, OData und Prozeduren dasselbe Ergebnis.
- PoC: `scripts/verify-autheris.sh` bekommt Prüfungen für `philipp` (Teilmaske bei `customer_number`, gerundete Koordinaten bei `gps_position`); der Export (`export_autheris.py`) schickt wieder die Originalnamen statt `NULLIFY`.

## Grenzen
- Die Zeile „fällt auf `REDACT` zurück“ ergibt sich aus dem Quelltext von `ColumnMaskingProvider.MaskValue` (`default`-Zweig, Stand `main` nach v1.1.5); ich habe `PARTIAL_MASK`, `GEO_JITTER` und `TOKENIZE` nicht gegen den laufenden Container einzeln ausprobiert.
- Die SQL-seitigen Ausdrücke (`GovernedSqlExecutionService`) habe ich nicht vollständig gelesen; ob dort weitere Typen behandelt werden, ist offen.
- Die Spaltenzahlen stammen aus `dbt_sample/target/governance` (Stand 09.10.2026).
