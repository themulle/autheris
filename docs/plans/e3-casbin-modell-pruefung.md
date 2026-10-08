# E-3: Casbin-Modell per Probe prüfen statt per Textsuche

Stand: `cb16e6c`, Datei `src/Autheris.Application/Governance/CasbinEnforcementService.cs` (Casbin.NET 2.21.3).
Bezug: [status-und-umsetzungsplan-2026-10-07.md](status-und-umsetzungsplan-2026-10-07.md), Abschnitt 0, Befund E-3. Der Umbau erledigt den Teil „Modell beim Start prüfen“ von R-POL-5 mit.

## 1. Problem

Ob das Modell Wildcard-Mandanten (`*`) kennt, entscheidet heute eine Textsuche im Konstruktor (`:117-122`):

```csharp
_modelSupportsWildcardTenant = _modelText.Contains("p.tenant == \"*\"") ||
                               _modelText.Contains("p.tenant == '*\"") ||
                               _modelText.Contains("p.tenant == \"*'\"") ||
                               _modelText.Contains("p.tenant == '*'") ||
                               _modelText.Contains("p.tenant==\"*\"") ||
                               _modelText.Contains("p.tenant=='*'");
```

Das Flag wird an drei Stellen genutzt: `AddPolicy` (`:234`), `LoadPolicyFromText(TenantId, …)` (`:998`) und das globale `LoadPolicyFromText` (`:1156`). Ist es false, werden `*`-Regeln abgelehnt.

Schwächen:

| Fall | Beispiel | Ergebnis heute | Folge |
|---|---|---|---|
| Falsch negativ | `p.tenant  == "*"` (zwei Leerzeichen), `"*" == p.tenant`, `p.tenant ==\"*\"` | „kann keine Wildcards“ | `*`-Regeln werden abgelehnt (fail-closed, aber Betriebsstörung). |
| Falsch positiv | Der Ausdruck steht nur in einem Kommentar (`# p.tenant == "*"`) oder in einer anderen Sektion | „kann Wildcards“ | `*`-Regeln werden angenommen. Der Gateway-Matcher wertet sie aus, Casbin nicht. Globale Allows wirken nicht (fail-closed), globale Denies nur im Gateway-Teil. Ein Modell, das nicht macht, was es verspricht, bleibt unbemerkt. |
| Falsch positiv mit Rechtewirkung | `m = g(r.sub, p.sub) && r.tenant == p.tenant \|\| p.tenant == "*" && keyMatch2(r.obj, p.obj) && …` (Klammern fehlen) | „kann Wildcards“ | Wegen der Operator-Rangfolge (`&&` vor `\|\|`) prüft eine `*`-Regel weder Subjekt noch Rolle. Casbin sagt Allow für **jeden** Nutzer. Das Gateway verlangt zusätzlich einen Treffer im eigenen Matcher (UND-Verknüpfung, `:412-433`). Ein Fehler dort würde aber nicht mehr abgefangen. |
| Unsinnige Varianten | `'*"` und `"*'` | – | Nie gültig, verdeckt nur, dass die Prüfung geraten ist. |

Dazu kommt: Die Textsuche sagt nichts darüber, ob das Modell die übrigen Annahmen des Gateways erfüllt. Diese Annahmen sind:
- Mandanten-Trennung;
- Deny gewinnt;
- Rollen über `g`;
- `eval(p.sub_rule)` wird ausgewertet;
- Arität `r = sub, tenant, obj, act, ctx` und `p = sub, tenant, obj, act, sub_rule, eft`.

Ein abweichendes Modell fällt erst bei Requests auf oder gar nicht. Außerdem fällt der Konstruktor bei fehlender Modelldatei still auf das eingebaute Modell zurück (`:93-114`, Teil von R-POL-5).

## 2. Zielbild

Das Modell wird **einmal beim Laden** mit einer festen Reihe von Probe-Regeln auf einem eigenen, leeren Enforcer **ausgeführt**. Geprüft wird das **Verhalten**, nicht der Text.

Ergebnis ist entweder:
- `CasbinModelCapabilities` (heute nur `SupportsWildcardTenant`), wenn alle Pflichteigenschaften erfüllt sind; oder
- eine `CasbinModelValidationException` mit der Liste der verletzten Eigenschaften. Sie bricht den Start ab.

Die Prüfung ist eine statische, seiteneffektfreie Funktion. Sie wird an drei Stellen genutzt:
1. in der Optionsvalidierung beim Start (fail-fast, bevor Requests kommen);
2. im Konstruktor von `CasbinEnforcementService`;
3. im `PolicySimulationService` (gleiches Modell, gleiche Prüfung).

## 3. Probe-Eigenschaften

Feste Probe-Werte ohne Punkte und ohne `:` oder `*`, weil `keyMatch2` Punkte als Regex-Platzhalter behandelt (M-18) und `:`/`*` Sonderbedeutung haben:

| Name | Wert |
|---|---|
| Subjekte | `probe_user`, `probe_other`, Rolle `probe_role` |
| Mandanten | `probe_a`, `probe_b` |
| Objekte | `probe_table`, `probe_other_table` |
| Aktion | `read` |

Jede Eigenschaft läuft auf einem **frischen** Enforcer aus demselben Modelltext:

| ID | Pflicht | Policy | Anfrage | Erwartet | Was sie absichert |
|---|---|---|---|---|---|
| M1 | ja | `p, probe_user, probe_a, probe_table, read, true, allow` | `probe_user, probe_a, probe_table, read` | true | Grundfunktion, Arität von `r` und `p` |
| M2 | ja | wie M1 | `probe_user, probe_b, probe_table, read` | false | Mandanten-Trennung |
| M3 | ja | wie M1 | `probe_other, probe_a, probe_table, read` | false | Subjekt wird geprüft |
| M4 | ja | wie M1 | `probe_user, probe_a, probe_other_table, read` | false | Objekt wird geprüft |
| M5 | ja | M1 plus `p, probe_user, probe_a, probe_table, read, true, deny` | wie M1 | false | Deny gewinnt (policy_effect) |
| M6 | ja | `p, probe_user, probe_a, probe_table, read, false, allow` | wie M1 | false | `eval(p.sub_rule)` wird ausgewertet |
| M7 | ja | `p, probe_role, probe_a, probe_table, read, true, allow` plus `g, probe_user, probe_role` | wie M1 | true | Rollen über `g` |
| M8 | ja | wie M7 | `probe_other, probe_a, probe_table, read` | false | Rolle gilt nicht für andere |
| W1 | Fähigkeit | `p, probe_user, *, probe_table, read, true, allow` | `probe_user, probe_b, probe_table, read` | true | **Wildcard-Mandant wird unterstützt** |
| W2 | ja, falls W1 | wie W1 | `probe_other, probe_b, probe_table, read` | false | `*` umgeht das Subjekt nicht (Klammerfehler) |
| W3 | ja, falls W1 | wie W1 | `probe_user, probe_b, probe_other_table, read` | false | `*` umgeht das Objekt nicht |
| W4 | ja, falls W1 | W1 plus `p, probe_user, probe_b, probe_table, read, true, deny` | `probe_user, probe_b, probe_table, read` | false | Mandanten-Deny gewinnt gegen `*`-Allow |
| W5 | ja, falls W1 | `p, probe_user, *, probe_table, read, true, deny` plus M1 | wie M1 | false | `*`-Deny gewinnt gegen Mandanten-Allow |

Auswertung:
- Wirft schon das Erzeugen des Enforcers, `AddPolicy` oder `Enforce`, ist das Modell **ungültig**. Ursachen sind Syntaxfehler, falsche Arität oder ein unbekannter Funktionsname.
- Scheitert eine Pflicht-Eigenschaft (M1–M8), ist das Modell **ungültig**.
- Liefert W1 false, ist das Modell gültig, aber **ohne Wildcard-Unterstützung**. `*`-Regeln werden wie heute abgelehnt.
- Liefert W1 true und scheitert eine von W2–W5, ist das Modell **ungültig**. Es gibt dann eine Wildcard, die mehr freigibt als vorgesehen.

Mit diesen Eigenschaften fallen auch die Fälle aus Abschnitt 1 auf: Ein Kommentar allein ergibt W1 = false, ein Klammerfehler scheitert an W2.

## 4. Umsetzung

### 4.1 Neue Datei `src/Autheris.Application/Governance/CasbinModelContract.cs`

```csharp
namespace Autheris.Application.Governance;

public sealed record CasbinModelCapabilities(bool SupportsWildcardTenant);

public sealed class CasbinModelValidationException : Exception
{
    public IReadOnlyList<string> Violations { get; }
    public CasbinModelValidationException(IReadOnlyList<string> violations, Exception? inner = null)
        : base("The Casbin model does not satisfy the gateway contract: " + string.Join("; ", violations), inner)
        => Violations = violations;
}

/// <summary>
/// E-3: verifies a Casbin model by behaviour (probe policies on a throw-away enforcer), not by text search.
/// Pure and side-effect free; safe to call at startup, in the enforcement service and in the policy simulation.
/// </summary>
public static class CasbinModelContract
{
    private const string UserA = "probe_user", UserB = "probe_other", Role = "probe_role";
    private const string TenantA = "probe_a", TenantB = "probe_b";
    private const string Obj = "probe_table", OtherObj = "probe_other_table", Act = "read";

    private sealed record Probe(string Id, bool Mandatory, string[][] Policies, string[][] Grouping, string[] Request, bool Expected);

    public static CasbinModelCapabilities Verify(string modelText)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelText);
        var violations = new List<string>();

        // M1–M8: mandatory
        foreach (var probe in MandatoryProbes)
        {
            Check(modelText, probe, violations);
        }

        // W1: capability
        var supportsWildcard = Run(modelText, WildcardCapabilityProbe, violations) == true;
        if (supportsWildcard)
        {
            foreach (var probe in WildcardSafetyProbes)   // W2–W5
            {
                Check(modelText, probe, violations);
            }
        }

        if (violations.Count > 0)
        {
            throw new CasbinModelValidationException(violations);
        }

        return new CasbinModelCapabilities(supportsWildcard);
    }

    private static void Check(string modelText, Probe probe, List<string> violations)
    {
        var result = Run(modelText, probe, violations);
        if (result is not null && result != probe.Expected)
        {
            violations.Add($"{probe.Id}: expected {probe.Expected} for ({string.Join(", ", probe.Request)}), got {result}");
        }
    }

    /// <returns>Enforce result, or null if the model could not be built or evaluated (violation recorded).</returns>
    private static bool? Run(string modelText, Probe probe, List<string> violations)
    {
        try
        {
            var enforcer = new Enforcer(DefaultModel.CreateFromText(modelText));
            foreach (var p in probe.Policies) enforcer.AddPolicy(p);
            foreach (var g in probe.Grouping) enforcer.AddGroupingPolicy(g);
            return enforcer.Enforce(probe.Request[0], probe.Request[1], probe.Request[2], probe.Request[3], ProbeContext);
        }
        catch (Exception ex)
        {
            violations.Add($"{probe.Id}: model could not be evaluated ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    // r.ctx as the gateway passes it (SecurityEvaluationContext); the probe sub_rules are the literals "true"/"false".
    private static readonly SecurityEvaluationContext ProbeContext = new(
        UserSid: /* valid probe SID, same format as in tests */,
        GroupSids: [],
        Tenant: new TenantId(TenantA),
        TargetTable: new TableIdentifier("probe", "probe", Obj),
        RequestedColumns: [],
        ClientIp: System.Net.IPAddress.Loopback,
        Timestamp: DateTimeOffset.UnixEpoch,
        PurposeId: null);

    private static readonly Probe[] MandatoryProbes = [ /* M1–M8 as in the table */ ];
    private static readonly Probe WildcardCapabilityProbe = /* W1 */;
    private static readonly Probe[] WildcardSafetyProbes = [ /* W2–W5 */ ];
}
```

Hinweise zur Umsetzung:
- `AddPolicy(params string[])` und `AddGroupingPolicy(params string[])` gibt es in Casbin.NET 2.x. `Enforce(params object[])` nimmt die fünf Request-Werte. Die genauen Überladungen beim Bauen prüfen. Aufrufe und Arität entsprechen denen des Service (`:257`, `:429`).
- Die Probe-SID nach demselben Muster bauen wie die Unit-Tests (`new Sid("S-1-5-21-…")`), damit die Sid-Validierung nicht greift.
- Die Probe-Policies tragen genau sechs Werte (`sub, tenant, obj, act, sub_rule, eft`), wie `AddPolicy` im Service. Ein Modell mit anderer `p`-Arität scheitert an M1 und ist damit ungültig. Das ist gewollt, weil der Service immer sechs Werte schreibt.
- Laufzeit: 13 Enforcer mit je einer bis zwei Regeln, im Millisekundenbereich, einmal pro Modell. Das Ergebnis pro Modelltext zu cachen ist nicht nötig.

### 4.2 `CasbinEnforcementService`

1. Konstruktor:
   - **Datei-Pflicht:** Ist `modelConfigPath` gesetzt, aber die Datei fehlt oder ist leer, wird `FileNotFoundException` bzw. `CasbinModelValidationException` geworfen statt still auf das eingebaute Modell zu wechseln (R-POL-5). Das eingebaute Modell gilt nur, wenn **kein** Pfad gesetzt ist.
   - **Prüfung:** `var capabilities = CasbinModelContract.Verify(_modelText);`
   - **Flag aus der Probe:** `_modelSupportsWildcardTenant = capabilities.SupportsWildcardTenant;`
   - Die sechs `Contains`-Zeilen (`:117-122`) entfallen.
2. Das eingebaute Modell als `internal const string DefaultModelText` auslagern, damit `PolicySimulationService` und Tests es wiederverwenden.
3. Meldungen an den drei Nutzungsstellen (`:234`, `:998`, `:1156`) vereinheitlichen. Die Meldung nennt die Probe W1 statt eines Textmusters: „Das Casbin-Modell unterstützt keine Wildcard-Mandanten (Probe W1). `*`-Regeln sind nicht erlaubt.“
4. Beim Start einmal loggen (Information): Modellquelle (Datei oder eingebaut) und `SupportsWildcardTenant`.

### 4.3 Startvalidierung in `GatewayServiceCollectionExtensions`

Der Block „POL-1: If Casbin is enabled, ModelPath and PolicyPath must be configured …“ (`:996-1023`) prüft heute nur Existenz und Länge. Ergänzen:

```csharp
try
{
    CasbinModelContract.Verify(File.ReadAllText(options.Casbin.ModelPath));
}
catch (CasbinModelValidationException ex)
{
    throw new ValidationException($"Casbin-Modell '{options.Casbin.ModelPath}' erfüllt den Gateway-Vertrag nicht: {string.Join("; ", ex.Violations)}");
}
```

Damit bricht ein ungeeignetes Modell den Start ab, bevor der erste Request kommt. Heute wird der Service lazy erzeugt, eine Ausnahme im Konstruktor käme erst beim ersten Request. Die Policy-Datei probeweise zu parsen (zweiter Teil von R-POL-5) ist ein eigener Schritt. Er lässt sich hier anschließen, sobald `ParsePolicyText` aus dem E-2-Umbau existiert.

### 4.4 `PolicySimulationService`

`PolicySimulationService.cs:35` hat eine eigene Kopie des Modelltexts. Umstellen auf `CasbinEnforcementService.DefaultModelText` bzw. dieselbe Modellquelle wie der Service, und `CasbinModelContract.Verify` aufrufen. So können Simulation und Durchsetzung nicht wieder auseinanderlaufen (Rest von C-2).

### 4.5 Ausgeliefertes Modell

`src/Autheris.Application/Governance/rbac_with_abac.conf` erfüllt alle Eigenschaften und unterstützt Wildcards. Es muss nicht geändert werden. Prüfen, ob das Modell im Container-Image an dem Pfad liegt, den `Casbin:ModelPath` in Produktion verwenden soll. Gegebenenfalls in der Doku nennen.

## 5. Tests (zuerst schreiben)

Neue Datei `tests/Autheris.Tests.Unit/Governance/CasbinModelContractTests.cs`:

| Test | Modell | Erwartung | Heute |
|---|---|---|---|
| Eingebautes Modell | `DefaultModelText` | gültig, Wildcard = true | – |
| Ausgeliefertes Modell | Inhalt von `rbac_with_abac.conf` | gültig, Wildcard = true | – |
| Ohne Wildcard-Klausel | Matcher mit `r.tenant == p.tenant` | gültig, Wildcard = false | gleich |
| Andere Schreibweise | `("*" == p.tenant \|\| r.tenant == p.tenant)`, zusätzliche Leerzeichen | gültig, Wildcard = true | **rot** (falsch negativ) |
| Nur Kommentar | Matcher ohne Wildcard, darüber `# p.tenant == "*"` | gültig, Wildcard = false | **rot** (falsch positiv) |
| Klammerfehler | `g(r.sub, p.sub) && r.tenant == p.tenant \|\| p.tenant == "*" && keyMatch2(r.obj, p.obj) && …` | ungültig, Verletzung W2 | **rot** (wird angenommen) |
| Keine Mandanten-Prüfung | Matcher ohne `tenant` | ungültig, M2 | **rot** |
| Allow-Override | `e = some(where (p.eft == allow))` | ungültig, M5 | **rot** |
| Ohne `eval(p.sub_rule)` | Matcher ohne `eval` | ungültig, M6 | **rot** |
| Ohne Rollen | `g(r.sub, p.sub)` durch `r.sub == p.sub` ersetzt | ungültig, M7 | **rot** |
| Falsche Arität | `p = sub, obj, act` | ungültig, M1 (Evaluationsfehler) | **rot** |
| Syntaxfehler | defekter Matcher | ungültig | **rot** |

Ergänzend:
- **Service:** Mit einem Modell ohne Wildcard-Klausel lehnt `AddPolicy(new TenantId("*"), …)` bzw. das Laden einer `*`-Zeile mit Hinweis auf W1 ab. Mit einem ungültigen Modell wirft der Konstruktor `CasbinModelValidationException`.
- **Service:** Gesetzter, aber fehlender `modelConfigPath` wirft (heute: stiller Rückfall).
- **Startvalidierung:** Ungültiges Modell in `Casbin:ModelPath` mit `Enabled=true` ergibt `ValidationException` beim Start (`ValidateOnStart`).
- **Simulation:** Simulation und Durchsetzung liefern für dieselben Regeln dieselbe Entscheidung, inklusive `*`-Regel.

## 6. Reihenfolge und Aufwand

1. `CasbinModelContractTests` schreiben (rot, weil die Klasse fehlt). Die Service-Tests aus Abschnitt 5 schreiben und auf `cb16e6c` rot sehen.
2. `CasbinModelContract` implementieren.
3. Konstruktor umstellen, `DefaultModelText` auslagern, Textsuche löschen, Rückfall bei fehlender Datei entfernen.
4. Startvalidierung ergänzen.
5. `PolicySimulationService` umstellen.
6. `dotnet build`, `dotnet test`. Commit: „fix(governance): verify Casbin model by probe evaluation instead of text search (E-3, R-POL-5 model part)“.

Aufwand: etwa ein halber Tag.

Mit E-2 verträgt sich der Umbau. E-2 baut Enforcer nur noch in `CreateEnforcer`, und E-3 liefert das Flag, das `Publish`/`validate` für `*`-Regeln braucht. Die Reihenfolge ist egal. E-3 zuerst ist kleiner und reduziert das Risiko für E-2.

## 7. Abnahme

- Keine Textsuche mehr auf dem Modell (`_modelText.Contains` kommt in der Datei nicht mehr vor).
- Ein ungültiges Modell bricht den Start ab, mit Meldung der verletzten Eigenschaften.
- Ein gesetzter, aber fehlender Modellpfad bricht den Start ab.
- Alle Tests aus Abschnitt 5 grün; die als „rot“ markierten waren vor dem Umbau rot.
- `docs/configuration-guide.md` (Abschnitt Casbin) beschreibt die Eigenschaften M1–M8 und W1–W5 als Vertrag für eigene Modelle.
