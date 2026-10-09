# Detaillierter Architektur- & Implementierungsplan: Erweiterte Maskierungsregeln & Geodaten-Schutz

**Thema:** Vollständige Spezifikation und Implementierung von Feature-Request R-53 (`GEO_JITTER`, `PARTIAL_MASK`, `TOKENIZATION`), dbt-Governance-Vokabular-Harmonisierung und typgerechtes `REDACT` (B-06).  
**Referenzen:** [Feature-Request Maskierungsregeln](2026-10-09-feature-request-maskierungsregeln.md), [Befunde PoC v1.1.5](2026-10-09-poc-befunde-v1-1-5.md)  
**Status:** Detaillierter Architekturplan / Bereit zur Umsetzung  

---

## 1. Ausgangslage & Zielarchitektur

In Enterprise-Umgebungen und im Liebherr-PoC (`POC_Backstage_citizen_dev`) reichen elementare Maskierungen (`NULLIFY`, kompletter Text-`REDACT`) nicht aus:
1. **Geodaten-Schutz (`GEO_JITTER`):** GPS-Positionen von Maschinen (`tem.gps_position.latitude`, `tem.gps_position.longitude`, `conf.geo_point`, `ud.customer_geo_point`) dürfen für unprivilegierte Benutzer (z. B. `philipp`) nicht metergenau einsehbar sein. Totalausblendung oder Ersetzung durch `0` oder `[GESCHÜTZT]` bricht jedoch Kartenkomponenten, Abstandsfilter und statistische Geo-Auswertungen.
2. **Teilmaskierung (`PARTIAL_MASK`):** Kundennummern (`KD-100234` -> `KD-*****`) oder Personennamen (`Mustermann` -> `M*****`) erfordern sichtbare Präfixe/Klassifizierungsmerkmale bei gleichzeitiger Unkenntlichmachung des Personenbezugs.
3. **Tokenisierung (`TOKENIZATION`):** Entwurf für konsistente, reversible oder pseudonyme Ersetzung über einen Token-Vault oder Format-Preserving Encryption.
4. **Typgerechte Redaktion (B-06):** Werden Zahlen oder Zeitstempel mit `REDACT` maskiert, darf die Antwort weder Text (`"[GESCHÜTZT]"`) noch `0` sein (da `0` eine gültige Koordinate oder ein Betrag ist), sondern muss typkonform `NULL` liefern.
5. **Parität aller Schnittstellen (R-25):** Maskierungsregeln müssen identische Ergebnisse liefern, unabhängig davon, ob die Abfrage über WebSQL (T-SQL/Postgres-Rewriting), Trino/DuckDB, GraphQL, OData oder Stored Procedures erfolgt.

---

## 2. Datenmodell & Konfigurationsschema

### 2.1 Modell-Erweiterung in `src/Autheris.Domain/Model/MaskingRule.cs`

```csharp
namespace Autheris.Domain.Model;

using System;
using System.Text.Json.Serialization;

public sealed record MaskingRule
{
    [JsonPropertyName("rule_type")]
    public required string RuleType { get; init; } // REDACT, NULLIFY, HMAC, MASK_EMAIL, MASK_PHONE, GEO_JITTER, PARTIAL_MASK, TOKENIZE

    [JsonPropertyName("replacement")]
    public string? Replacement { get; init; }

    [JsonPropertyName("mode")]
    public string? Mode { get; init; } // GEO_JITTER: "round" (Standard) oder "noise"

    [JsonPropertyName("decimals")]
    public int? Decimals { get; init; } = 2; // GEO_JITTER: Nachkommastellen (z. B. 2 = ca. 1.1 km)

    [JsonPropertyName("radius_meters")]
    public double? RadiusMeters { get; init; } = 500.0; // GEO_JITTER noise: Maximalradius in Metern

    [JsonPropertyName("keep_prefix")]
    public int? KeepPrefix { get; init; } = 1; // PARTIAL_MASK: Sichtbare Anfangszeichen

    [JsonPropertyName("keep_suffix")]
    public int? KeepSuffix { get; init; } = 0; // PARTIAL_MASK: Sichtbare Endzeichen

    [JsonPropertyName("mask_char")]
    public char? MaskChar { get; init; } = '*'; // PARTIAL_MASK: Maskierungszeichen

    [JsonPropertyName("fixed_length")]
    public bool? FixedLength { get; init; } = false; // PARTIAL_MASK: Verbirgt Originallänge

    [JsonPropertyName("token_domain")]
    public string? TokenDomain { get; init; } // TOKENIZE: z. B. "customer_id", "iban"
}
```

---

## 3. Algorithmen & Mathematische Spezifikation

### 3.1 Regel: `GEO_JITTER`

#### 3.1.1 Modus `round` (Deterministische Rasterung)
- **Formel:**
  \[
  \text{lat}_{\text{masked}} = \text{ROUND}(\text{lat}, \text{decimals}), \quad \text{lon}_{\text{masked}} = \text{ROUND}(\text{lon}, \text{decimals})
  \]
  - `decimals = 2`: Auflösung ca. \(1{,}1\,\text{km}\) am Äquator
  - `decimals = 1`: Auflösung ca. \(11{,}1\,\text{km}\)
  - `decimals = 3`: Auflösung ca. \(110\,\text{m}\)
- **Integritätsregeln:**
  - `lat IS NULL` oder `lon IS NULL` $\longrightarrow$ `NULL`
  - `lat = 0.0 AND lon = 0.0` (fehlender GPS-Fix) $\longrightarrow$ `0.0` (kein Rauschen, verhindert Phantomstandorte im Atlantik)
  - Wertebereichsbegrenzung: \(-90.0 \le \text{lat} \le 90.0\) und \(-180.0 \le \text{lon} \le 180.0\)

#### 3.1.2 Modus `noise` (Deterministisches gekoppeltes Pseudo-Rauschen)
Um Ausmitteln durch Mehrfachabfragen (Averaging Attacks) zu verhindern, darf das Rauschen nicht bei jeder Abfrage zufällig gewählt werden. Es wird deterministisch über einen HMAC aus Zeilenschlüssel und Mandantengeheimnis erzeugt:
1. **Hashwert:** \(H = \text{HMAC-SHA256}(K_{\text{tenant}}, \text{Table} \parallel \text{PrimaryKey})\)
2. **Kopplung von Breite und Länge:**
   - Aus den Bytes 0..3 von \(H\) wird ein deterministischer Winkel \(\theta \in [0, 2\pi)\) abgeleitet:
     \[
     \theta = \frac{(H_{0..3} \pmod{36000})}{36000.0} \times 2\pi
     \]
   - Aus den Bytes 4..7 von \(H\) wird eine Distanz \(r \in [0, R_{\text{max}}]\) abgeleitet:
     \[
     r = \sqrt{\frac{H_{4..7} \pmod{10000}}{10000.0}} \times R_{\text{meters}}
     \]
   - Versatz in Grad (WGS84-Näherung):
     \[
     \Delta \text{lat} = \frac{r \cos(\theta)}{111139.0}, \quad \Delta \text{lon} = \frac{r \sin(\theta)}{111139.0 \times \cos(\text{lat}_{\text{rad}})}
     \]
3. **Ergebnis:** Beide Koordinaten werden als einheitlicher Vektor verschoben, wodurch topologische Plausibilität (z. B. Maschinen nicht im Meer) erhalten bleibt.

---

### 3.2 Regel: `PARTIAL_MASK`

- **Eingabe:** String \(S\), Parameter `keep_prefix` (\(p\)), `keep_suffix` (\(s\)), `mask_char` (\(c\)), `fixed_length` (\(f\)).
- **Unicode-Sicherheit:** Zeichenextraktion erfolgt über Unicode Runes / Graphem-Cluster, um Mehrbyte-Zeichen (Umlaute wie `Ä`, `ö`, Emojis) nicht fehlerhaft zu zerteilen.
- **Algorithmus:**
  1. Ist \(S\) `null`, ist die Ausgabe `null`.
  2. Ist \(S\) leer, ist die Ausgabe `""`.
  3. Länge in Runen \(L = \text{RuneCount}(S)\).
  4. Falls \(L \le p + s\):
     - Wenn \(f = \text{true}\): Ausgabe von \(5 \times c\) (z. B. `"*****"`).
     - Wenn \(f = \text{false}\): Ausgabe von \(L \times c\).
  5. Falls \(L > p + s\):
     - Präfix: \(P = S[0 \dots p-1]\)
     - Suffix: \(E = S[L-s \dots L-1]\) (falls \(s > 0\), sonst leer)
     - Maskenanzahl \(M = f \ ?\ 5 : (L - p - s)\)
     - Ausgabe: \(P + (M \times c) + E\)

---

### 3.3 Regel: `TOKENIZATION` (Entwurf & KMS-Lookup)

- Für den Übergangsbetrieb (vor Anbindung eines externen Token-Vaults wie Protegrity oder HashiCorp Vault):
  - Bereitstellung einer deterministischen HMAC-Tokenisierung (`HMAC_SHA256` mit Format-Präfix).
  - Schema: `TOK_<Domain>_<First8HexOfHmac>` (z. B. `TOK_CUST_7a8b9c1d`).
  - Im dbt-Import: Erlaubt, ordnet sich sicherheitsseitig auf Stufe `HMAC` ein.

---

### 3.4 Typgerechtes `REDACT` (B-06)

| Spaltendatentyp | Bisheriges Verhalten | Neues Verhalten (B-06) | Rationale |
|---|---|---|---|
| `VARCHAR`, `NVARCHAR`, `TEXT` | `"[REDACTED]"` | `"[REDACTED]"` | Textspalten behalten lesbaren Maskierungshinweis |
| `INT`, `BIGINT`, `SMALLINT` | `0` | `NULL` | `0` ist ein gültiger Zähler/Kennzahl; `NULL` verhindert Fehlberechnungen |
| `DECIMAL`, `FLOAT`, `DOUBLE` | `0.0` oder `[GESCHÜTZT]` | `NULL` | Verhindert ungültige Geopositionen oder Salden |
| `DATE`, `DATETIME`, `TIMESTAMP` | `1970-01-01` oder String | `NULL` | Verhindert Fehlinterpretationen von Zeitreihen |
| `BOOLEAN` | `false` | `NULL` | `false` verfälscht logische Flags; 3-wertige SQL-Logik erfordert `NULL` |

---

## 4. SQL-Generierung je Zieldialekt

Die Maskierungsregeln werden im `AstSecurityVisitor` und in den Zieldialekt-Generatoren in native SQL-Ausdrücke übersetzt:

### 4.1 SQL Server (T-SQL)
```sql
-- GEO_JITTER (round)
CASE 
    WHEN [latitude] IS NULL OR [latitude] = 0.0 THEN [latitude]
    ELSE ROUND([latitude], 2)
END AS [latitude]

-- PARTIAL_MASK (keep_prefix=1, keep_suffix=0, fixed_length=true)
CASE 
    WHEN [customer_number] IS NULL THEN NULL
    WHEN LEN([customer_number]) <= 1 THEN '*****'
    ELSE CONCAT(LEFT([customer_number], 1), '*****')
END AS [customer_number]

-- REDACT auf numerischen Typen
CAST(NULL AS DECIMAL(18, 4)) AS [amount]
```

### 4.2 PostgreSQL / DuckDB / Trino
```sql
-- GEO_JITTER (round)
CASE 
    WHEN "latitude" IS NULL OR "latitude" = 0.0 THEN "latitude"
    ELSE ROUND("latitude"::numeric, 2)::double precision
END AS "latitude"

-- PARTIAL_MASK
CASE 
    WHEN "customer_number" IS NULL THEN NULL
    WHEN LENGTH("customer_number") <= 1 THEN '*****'
    ELSE CONCAT(SUBSTRING("customer_number" FROM 1 FOR 1), '*****')
END AS "customer_number"

-- REDACT auf numerischen Typen
CAST(NULL AS NUMERIC) AS "amount"
```

### 4.3 SQLite
```sql
-- GEO_JITTER (round)
CASE 
    WHEN "latitude" IS NULL OR "latitude" = 0.0 THEN "latitude"
    ELSE ROUND("latitude", 2)
END AS "latitude"

-- PARTIAL_MASK
CASE 
    WHEN "customer_number" IS NULL THEN NULL
    WHEN LENGTH("customer_number") <= 1 THEN '*****'
    ELSE SUBSTR("customer_number", 1, 1) || '*****'
END AS "customer_number"
```

---

## 5. In-Memory Maskierung (`ColumnMaskingProvider.cs`)

Für OData, GraphQL-In-Memory-Auflösung und Stored-Procedure-Ergebnisse implementiert `ColumnMaskingProvider.MaskValue` dieselbe Logik in C#:

```csharp
public static object? MaskValue(object? value, MaskingRule rule, string? dataType = null)
{
    if (value is null or DBNull) return null;

    var ruleType = rule.RuleType.ToUpperInvariant();
    return ruleType switch
    {
        "NULLIFY" => null,
        "GEO_JITTER" => MaskGeoJitter(value, rule),
        "PARTIAL_MASK" => MaskPartial(value.ToString() ?? "", rule),
        "TOKENIZE" or "TOKENIZATION" => MaskTokenize(value.ToString() ?? "", rule),
        "REDACT" => MaskRedactTyped(value, rule, dataType),
        "MASK_EMAIL" => MaskEmail(value.ToString() ?? ""),
        "MASK_PHONE" => MaskPhone(value.ToString() ?? ""),
        "HMAC" or "HMAC_SHA256" => MaskHmac(value.ToString() ?? "", rule),
        _ => MaskRedactTyped(value, rule, dataType)
    };
}

private static object? MaskGeoJitter(object value, MaskingRule rule)
{
    if (!double.TryParse(value.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
        return value;

    if (Math.Abs(d) < 1e-9) return 0.0; // GPS 0,0 bleibt 0

    var decimals = rule.Decimals ?? 2;
    decimals = Math.Clamp(decimals, 0, 6);
    return Math.Round(d, decimals, MidpointRounding.AwayFromZero);
}

private static string MaskPartial(string s, MaskingRule rule)
{
    if (string.IsNullOrEmpty(s)) return s;

    var prefixLen = Math.Max(0, rule.KeepPrefix ?? 1);
    var suffixLen = Math.Max(0, rule.KeepSuffix ?? 0);
    var maskChar = rule.MaskChar ?? '*';
    var fixedLen = rule.FixedLength ?? false;

    var runes = s.EnumerateRunes().ToArray();
    if (runes.Length <= prefixLen + suffixLen)
    {
        return fixedLen ? new string(maskChar, 5) : new string(maskChar, runes.Length);
    }

    var prefix = string.Concat(runes.Take(prefixLen));
    var suffix = suffixLen > 0 ? string.Concat(runes.TakeLast(suffixLen)) : "";
    var maskCount = fixedLen ? 5 : (runes.Length - prefixLen - suffixLen);

    return $"{prefix}{new string(maskChar, maskCount)}{suffix}";
}
```

---

## 6. Test- & Validierungsmatrix

| Testfall | Typ | Eingabedaten | Erwartetes Ergebnis |
|---|---|---|---|
| `GEO_JITTER` Round | Unit | `lat = 48.298197967`, `decimals = 2` | `48.30` |
| `GEO_JITTER` Round | Unit | `lon = 9.700374517`, `decimals = 2` | `9.70` |
| `GEO_JITTER` Zero Fix | Unit | `lat = 0.0`, `lon = 0.0` | `0.0` (Kein Rauschen / kein Versatz in den Atlantik) |
| `GEO_JITTER` Null Fix | Unit | `lat = NULL` | `NULL` |
| `PARTIAL_MASK` Name | Unit | `"Mustermann"`, `p=1, s=0, fixed=false` | `"M*********"` |
| `PARTIAL_MASK` Kundennummer | Unit | `"KD-100234"`, `p=3, s=0, fixed=true` | `"KD-*****"` |
| `PARTIAL_MASK` Unicode | Unit | `"Österreicher"`, `p=1, s=0, fixed=false` | `"Ö***********"` |
| `PARTIAL_MASK` Short | Unit | `"AB"`, `p=2, s=1, fixed=true` | `"*****"` |
| `REDACT` Decimal (B-06) | Unit | `12500.50m` (Gehalt/Preis) | `NULL` (nicht `0` oder `[GESCHÜTZT]`) |
| `REDACT` Datetime (B-06) | Unit | `2026-10-09T10:00:00Z` | `NULL` |
