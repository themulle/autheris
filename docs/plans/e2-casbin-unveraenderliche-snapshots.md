# E-2: Casbin-Snapshots wirklich unveränderlich machen

Stand: `cb16e6c`, Datei `src/Autheris.Application/Governance/CasbinEnforcementService.cs` (Casbin.NET 2.21.3).
Bezug: [status-und-umsetzungsplan-2026-10-07.md](status-und-umsetzungsplan-2026-10-07.md), Abschnitt 0, Befund E-2. E-1 lässt sich im selben Umbau mit erledigen.

## 1. Problem

Mit `cb16e6c` liegt der Policy-Zustand in einem `PolicySnapshot`. Der Snapshot wird unter `_syncLock` gebaut und per `Interlocked.Exchange` veröffentlicht. Er enthält aber **dieselben `Enforcer`-Instanzen** wie der vorherige, und diese werden nach der Veröffentlichung weiter verändert. Atomar ist damit nur der Austausch der Dictionaries, nicht der Inhalt.

Konkrete Stellen:

| Stelle | Was passiert | Folge |
|---|---|---|
| `AddPolicy` (ab `:242`) | `nextEnforcers` ist eine flache Kopie. `enforcer.AddPolicy(...)` ändert den Enforcer, der im **laufenden** Snapshot steckt, bevor getauscht wird. | Laufende `Enforce`-Aufrufe sehen eine halb geänderte Policy. Der Casbin-`Enforcer` ist für gleichzeitiges Lesen und Schreiben nicht ausgelegt. |
| `AddRoleForUser` (ab `:287`) | Gleiches Muster mit `AddGroupingPolicy`. | Wie oben, für Rollen. |
| Globaler Reload, Block „apply them to preserved per-tenant enforcers“ (ab `:1270`) | Die Enforcer der Mandanten-Dateien werden aus dem aktuellen Snapshot übernommen, und die neuen `*`-Regeln werden **zusätzlich** hineingeschrieben. | a) Bei jedem Reload kommen die `*`-Regeln erneut dazu, ohne dass alte entfernt werden. b) Eine global **entfernte** `*`-Allow-Regel bleibt im Enforcer des Mandanten und wird in `enforcer.Enforce(...)` (`:429`) weiter als Allow gewertet. c) Die Änderung trifft den Enforcer des laufenden Snapshots. |
| Globaler Reload, `nextEnforcers[tName] = enforcer` nach dem Erhalten der Mandanten-Dateien (ab `:1265`) | Steht ein Mandant mit eigener Datei auch in der globalen Datei, ersetzt der globale Enforcer den der Mandanten-Datei. | Die Regeln der Mandanten-Datei sind bis zum nächsten Reload dieser Datei weg. |
| Laden pro Mandant (`LoadPolicyFromText(TenantId, …)`, ab `:1040`) | Die `*`-Regeln werden aus dem aktuellen Snapshot einmalig kopiert; globale `g`-Regeln werden **nicht** übernommen. | Spätere globale Änderungen an `*`-Regeln und Rollen erreichen diesen Mandanten nicht oder nur additiv (siehe oben). |

Zur Wirkung: Die Gateway-Seite (`IsAllowRuleMatch`) liest die Regeln aus `snapshot.Rules` und ist damit aktuell. Die Entscheidung ist aber ein UND aus Gateway-Matcher und `enforcer.Enforce(...)` (`:412-433`). Ein veralteter Allow im Enforcer allein öffnet also nichts.

Er wird zum Problem, sobald der Gateway-Matcher ebenfalls passt. Ein Beispiel: Eine globale `*`-Allow-Regel wird durch eine engere ersetzt, und der Mandant hat eine eigene Allow-Regel. Dann bewertet Casbin den Zugriff mit einem Regelstand, den es nicht mehr gibt. Dazu kommen Duplikate und Datenrennen, und `HasRoleForUser` (`:517-520`) arbeitet mit veralteten Rollen. Deshalb ist der Befund mittel.

## 2. Zielbild

**Grundsatz:** Ein veröffentlichter Snapshot und alle Objekte darin werden **nie wieder verändert**. Jede Änderung baut aus den Quelldaten einen komplett neuen Snapshot mit neuen `Enforcer`-Instanzen.

Dafür wird der Zustand in zwei Ebenen geteilt:

1. **Quellen (`PolicySources`)**: unveränderliche Rohdaten, getrennt nach Herkunft:
   - globale Datei: `p`-Regeln je Mandant (inklusive `*`) und globale `g`-Regeln;
   - Mandanten-Dateien: `p`- und `g`-Regeln je Mandant;
   - programmatisch: über `AddPolicy`/`AddRlsPolicy`/`AddRoleForUser` hinzugefügte Regeln je Mandant.
2. **Snapshot (`PolicySnapshot`)**: wird nur aus den Quellen berechnet. Er enthält je Mandant einen frisch gebauten Enforcer und die Regel-Metadaten, dazu die Epoch.

Jede schreibende Operation folgt demselben Ablauf:

```
lock (_syncLock)
    neueQuellen = änderung(_sources)
    prüfen(alteQuellen, neueQuellen)      // E-1, Fail-closed-Regeln
    neuerSnapshot = Build(neueQuellen, epoch + 1)
    _sources = neueQuellen
    Volatile.Write(ref _currentSnapshot, neuerSnapshot)
    _decisionCache.Clear()
Events außerhalb des Locks auslösen
```

Lesende Operationen (`EvaluatePolicyAsync`, `HasPolicies`, `CurrentEpoch`) lesen den Snapshot **einmal** per `Volatile.Read` und arbeiten nur mit diesem Objekt. Das ist heute schon so (`:318`).

## 3. Festzulegende Semantik

Diese Punkte müssen vor dem Umbau entschieden werden. Empfehlung jeweils fett.

| Frage | Heute | Empfehlung |
|---|---|---|
| Mandant steht in globaler Datei **und** hat eigene Datei | Global ersetzt die Mandanten-Datei | **Vereinigung** beider Regelsätze. Ein Deny aus einer der beiden Quellen gewinnt ohnehin. |
| Darf eine Mandanten-Datei Regeln für andere Mandanten oder `*` enthalten? | Ja (`ruleTenant = parts[2]`) | **Nein.** Feld leer oder gleich dem Mandanten, sonst `FormatException`. Sonst wirken „fremde“ Regeln nur für diesen einen Mandanten, das ist verwirrend. |
| Gelten globale `g`-Regeln auch für Mandanten mit eigener Datei? | Nein | **Ja.** Rollen aus der globalen Datei gelten überall, Mandanten-Dateien ergänzen. |
| Was bekommt ein Mandant ohne eigene Regeln? | Enforcer `*` oder leerer Fallback | **Enforcer `*`** (nur `*`-Regeln und globale `g`-Regeln). Keine `g`-Regeln anderer Mandanten. |
| `ReloadPoliciesAsync(tenant)` ohne Mandanten-Datei und ohne globale Datei | Entfernt Enforcer und Regeln des Mandanten, auch solche aus der globalen Datei | **Nur die programmatische Quelle** dieses Mandanten entfernen. |
| `AddPolicy` mit Mandant `*` | Nur Enforcer `*` | Wirkt über den Neuaufbau automatisch auf **alle** Mandanten. |

## 4. Umsetzung Schritt für Schritt

### 4.1 Quelltypen

```csharp
private sealed record GroupingRule(string User, string Role);

/// <summary>Rules of one origin for one tenant key ("*" for wildcard rules). Immutable.</summary>
private sealed record TenantPolicySource(
    ImmutableArray<CasbinRuleMetadata> Rules,
    ImmutableArray<GroupingRule> Grouping)
{
    public static readonly TenantPolicySource Empty = new([], []);
}

/// <summary>All policy input, split by origin. Never mutated; every change creates a new instance.</summary>
private sealed record PolicySources(
    ImmutableDictionary<string, ImmutableArray<CasbinRuleMetadata>> GlobalRules,   // key: tenant or "*"
    ImmutableArray<GroupingRule> GlobalGrouping,
    ImmutableDictionary<string, TenantPolicySource> TenantFiles,                    // key: tenant
    ImmutableDictionary<string, TenantPolicySource> Programmatic)                   // key: tenant or "*"
{
    public static readonly PolicySources Empty = new(
        ImmutableDictionary.Create<string, ImmutableArray<CasbinRuleMetadata>>(StringComparer.OrdinalIgnoreCase),
        [],
        ImmutableDictionary.Create<string, TenantPolicySource>(StringComparer.OrdinalIgnoreCase),
        ImmutableDictionary.Create<string, TenantPolicySource>(StringComparer.OrdinalIgnoreCase));

    public int PolicyRuleCount =>
        GlobalRules.Values.Sum(r => r.Length) +
        TenantFiles.Values.Sum(s => s.Rules.Length) +
        Programmatic.Values.Sum(s => s.Rules.Length);
}

private PolicySources _sources = PolicySources.Empty;   // only read/written under _syncLock
```

`CasbinRuleMetadata` bleibt unverändert. Die normalisierte `SubRule` (Regex `'([^']{2,})'` → `"$1"`) wird erst beim Bau des Enforcers berechnet. Die Regex wird dazu als `static readonly Regex` mit `RegexOptions.Compiled` abgelegt, statt sie an vier Stellen inline zu wiederholen.

### 4.2 Snapshot unveränderlich machen

```csharp
private sealed class PolicySnapshot
{
    public IReadOnlyDictionary<string, Enforcer> Enforcers { get; }
    public IReadOnlyDictionary<string, ImmutableArray<CasbinRuleMetadata>> Rules { get; }
    public Enforcer WildcardEnforcer { get; }      // used for tenants without own entry
    public long Epoch { get; }
    // ctor stores FrozenDictionary/ImmutableDictionary instances (OrdinalIgnoreCase); no copies needed afterwards

    public bool HasPolicies(string tenant) =>
        (Rules.TryGetValue(tenant, out var r) && r.Length > 0) ||
        (Rules.TryGetValue("*", out var w) && w.Length > 0);
}
```

- `Dictionary`/`List` durch `FrozenDictionary` (.NET 8+) bzw. `ImmutableArray` ersetzen. Damit kann niemand den Snapshot versehentlich ändern.
- `HasPolicies` stützt sich nur noch auf `Rules`. `GetPolicy()` am Enforcer ist dann nicht mehr nötig, denn jeder Enforcer wird ausschließlich aus `Rules` gebaut.
- `_emptyFallbackEnforcer` entfällt. Der Snapshot hat immer einen `WildcardEnforcer` (ohne `*`-Regeln eben leer).

### 4.3 Snapshot aus Quellen bauen

```csharp
private PolicySnapshot BuildSnapshot(PolicySources src, long epoch)
{
    var tenantKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    tenantKeys.UnionWith(src.GlobalRules.Keys);
    tenantKeys.UnionWith(src.TenantFiles.Keys);
    tenantKeys.UnionWith(src.Programmatic.Keys);
    tenantKeys.Remove("*");

    var wildcardRules = RulesFor(src, "*");
    var globalGrouping = src.GlobalGrouping
        .AddRange(src.Programmatic.TryGetValue("*", out var pw) ? pw.Grouping : []);

    var enforcers = new Dictionary<string, Enforcer>(StringComparer.OrdinalIgnoreCase);
    var rules = new Dictionary<string, ImmutableArray<CasbinRuleMetadata>>(StringComparer.OrdinalIgnoreCase);

    var wildcardEnforcer = CreateEnforcer(wildcardRules, [], globalGrouping);
    if (wildcardRules.Length > 0) rules["*"] = wildcardRules;

    foreach (var tenant in tenantKeys)
    {
        var own = RulesFor(src, tenant);
        var grouping = globalGrouping.AddRange(GroupingFor(src, tenant));
        enforcers[tenant] = CreateEnforcer(own, wildcardRules, grouping);
        if (own.Length > 0) rules[tenant] = own;
    }

    return new PolicySnapshot(enforcers.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
                              rules.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
                              wildcardEnforcer, epoch);
}

// own rules of a tenant key from all three origins (global file, tenant file, programmatic)
private static ImmutableArray<CasbinRuleMetadata> RulesFor(PolicySources src, string tenant) => ...;
// g rules of a tenant from tenant file and programmatic origin (global g is added separately)
private static IEnumerable<GroupingRule> GroupingFor(PolicySources src, string tenant) => ...;

private Enforcer CreateEnforcer(
    ImmutableArray<CasbinRuleMetadata> own,
    ImmutableArray<CasbinRuleMetadata> wildcard,
    IEnumerable<GroupingRule> grouping)
{
    var enforcer = new Enforcer(DefaultModel.CreateFromText(_modelText));
    foreach (var r in own.Concat(wildcard))
    {
        enforcer.AddPolicy(r.Sub, r.Tenant, r.Obj, r.Act, NormalizeSubRule(r.SubRule), r.Eft);
    }
    foreach (var g in grouping.Distinct())
    {
        enforcer.AddGroupingPolicy(g.User, g.Role);
    }
    return enforcer;   // never touched again after this method returns
}
```

Hinweise:
- **Kosten:** Ein globaler Reload baut so viele Enforcer, wie es Mandanten mit eigenen Regeln gibt. Bei Hot Reload (Sekundenbereich, selten) ist das unkritisch. Bei vielen `AddPolicy`-Aufrufen hintereinander (Tests, Seed) lässt sich später optimieren: nur die betroffenen Mandanten neu bauen und unveränderte Enforcer aus dem alten Snapshot **wiederverwenden**. Das ist erlaubt, weil sie nie verändert werden. Ausnahme: Ändern sich `*`-Regeln oder globale `g`-Regeln, müssen alle Enforcer neu gebaut werden.
- **Doppelte Regeln** gibt Casbin mit `AddPolicy == false` zurück. Das schadet nicht, weil jeder Enforcer neu gebaut wird.
- **Modelltext:** Die Wildcard-Prüfung (E-3) gehört in den Konstruktor. Sie bleibt Voraussetzung für `*`-Regeln.

### 4.4 Zentraler Veröffentlichungsweg

```csharp
private IReadOnlyCollection<string> Publish(
    Func<PolicySources, PolicySources> change,
    Action<PolicySources, PolicySources>? validate = null)
{
    lock (_syncLock)
    {
        var current = _currentSnapshot;
        var nextSources = change(_sources);
        validate?.Invoke(_sources, nextSources);

        var next = BuildSnapshot(nextSources, current.Epoch + 1);

        var affected = current.Enforcers.Keys
            .Union(next.Enforcers.Keys, StringComparer.OrdinalIgnoreCase)
            .Append("*")
            .ToArray();

        _sources = nextSources;
        Volatile.Write(ref _currentSnapshot, next);
        _decisionCache.Clear();
        return affected;
    }
}
```

Alle schreibenden Methoden laufen danach nur noch über `Publish`:

| Methode | `change` | `validate` |
|---|---|---|
| `AddPolicy` / `AddRlsPolicy` | `Programmatic[tenant].Rules` um die Regel ergänzen | Wildcard-Modell prüfen (wie heute) |
| `AddRoleForUser` | `Programmatic[tenant].Grouping` ergänzen | – |
| `LoadPolicyFromText(TenantId, text)` | `TenantFiles[tenant]` ersetzen | RR-L4-05: leer bei aktiven **eigenen** Regeln (aus `TenantFiles[tenant]`) ablehnen; fremde Mandanten und `*` ablehnen (Abschnitt 3) |
| `LoadPolicyFromText(text)` (global) | `GlobalRules` und `GlobalGrouping` ersetzen | **E-1:** Hat die neue Datei keine `p`-Regel, die alte aber mindestens eine, dann ablehnen (unabhängig von `g`-Zeilen) |
| `ReloadPoliciesAsync(tenant)` ohne Dateien | `Programmatic.Remove(tenant)` | – |

Das Parsen bleibt **außerhalb** des Locks. Die beiden `LoadPolicyFromText`-Varianten parsen nur in Listen aus `CasbinRuleMetadata`/`GroupingRule` und erzeugen dabei **keine Enforcer** mehr. Gemeinsamen Parse-Code in eine Methode `ParsePolicyText(string text, string defaultTenant)` ziehen, die `(rules, grouping)` liefert. Die heute doppelt vorhandene Zeilenlogik entfällt damit.

Die Events `OnPolicyReloaded` werden nach `Publish` außerhalb des Locks für die zurückgegebenen Mandanten ausgelöst. `OnPolicyReloadFailed` bleibt wie heute.

### 4.5 Lesepfad

In `EvaluatePolicyAsync` (ab `:315`):

```csharp
var snapshot = Volatile.Read(ref _currentSnapshot);
var enforcer = snapshot.Enforcers.TryGetValue(context.Tenant.Value, out var e) ? e : snapshot.WildcardEnforcer;
// tenantRulesSnapshot: snapshot.Rules["*"] + snapshot.Rules[tenant]  (wie heute, ohne Kopie, ImmutableArray)
```

- `GetOrCreateEnforcer` wird `private` (E-4) oder entfällt. Gibt es externe Nutzer, gibt die Methode nur noch eine Leseschnittstelle zurück, keinen veränderbaren `Enforcer`.
- `IsSubjectMatch` nutzt weiter `enforcer.HasRoleForUser`. Weil der Enforcer jetzt die globalen `g`-Regeln enthält, wirken Rollen auch bei Mandanten mit eigener Datei.

### 4.6 Thread-Sicherheit von `Enforce` prüfen

Auf einem nie mehr veränderten Enforcer sind `Enforce` und `HasRoleForUser` reine Lesezugriffe. Ob Casbin.NET 2.21.3 dabei intern Caches (Matcher- oder Expression-Cache, Role-Manager) ohne Synchronisation befüllt, ist nicht dokumentiert.

- **Prüfen:** mit dem Lasttest aus 5.5 und einem Blick in den Quelltext von `Enforcer.Enforce` (`ExpressionHandler`, `EnforceView`).
- **Falls nicht sicher:** je Enforcer ein Lock (`lock (enforcer) { … }`) um `Enforce` und `HasRoleForUser`. Dank Entscheidungscache sind die Kosten gering. Alternativ einen kleinen Pool von Enforcer-Kopien je Mandant.

Das Problem besteht heute schon. Es ist durch den Umbau nicht neu, wird aber erst durch ihn sauber lösbar.

### 4.7 Entscheidungscache (E-5, optional im selben Schritt)

Der Cache-Key enthält die Epoch bereits. Damit veraltete Einträge nicht liegen bleiben, beim Schreiben prüfen:

```csharp
if (Volatile.Read(ref _currentSnapshot).Epoch == snapshot.Epoch)
    _decisionCache[cacheKey] = new CachedDecision(decision, now);
```

Zusätzlich eine Obergrenze (z. B. 50.000 Einträge, darüber `Clear()`).

## 5. Tests (zuerst schreiben, müssen auf `cb16e6c` rot sein)

Datei: `tests/Autheris.Tests.Unit/CasbinHotReloadTests.cs` oder eine neue `CasbinSnapshotImmutabilityTests.cs`.

1. **Entfernter Wildcard-Allow wirkt nicht mehr (Kerntest E-2):**
   - Global laden: `p, reader, *, finance.*, read, true, allow`.
   - Mandant `t1` per Mandanten-Datei: `p, reader, t1, finance.*, read, true, allow`.
   - Global neu laden: die `*`-Regel durch `p, reader, *, finance.invoices, read, true, allow` ersetzen.
   - Erwartung: Der **Casbin-Teil** für `t1` erlaubt `finance.orders` nur noch über die eigene Regel. Prüfen über den Enforcer-Inhalt (siehe Test 3) oder über eine eigene Regel mit `sub_rule`, die nur der alte `*`-Allow erfüllt hätte.
2. **Keine Duplikate:** Global zehnmal mit derselben Datei neu laden. Die Policy-Anzahl im Enforcer von `t1` bleibt gleich (über eine `internal` Diagnosemethode `GetPolicyCount(tenant)` oder `InternalsVisibleTo`).
3. **Veröffentlichter Enforcer bleibt unverändert:** Enforcer von `t1` aus dem Snapshot holen (internal), Policy-Liste merken, `AddPolicy(t1, …)` aufrufen. Die gemerkte Instanz hat dieselbe Policy-Liste, der neue Snapshot enthält eine **andere** Instanz.
4. **Globale Rollen für Mandanten mit eigener Datei:**
   - Global `g, alice, reader` und Mandanten-Datei `t1` mit `p, reader, t1, …, allow`.
   - Erwartung: `alice` erhält in `t1` Zugriff.
   - Nach dem Entfernen der `g`-Zeile global: kein Zugriff mehr.
5. **Paralleles Lesen und Schreiben:** 8 Threads werten 10.000-mal aus, während ein Thread 200-mal abwechselnd `AddPolicy` aufruft und global neu lädt. Erwartung:
   - keine Exception;
   - jede Entscheidung entspricht entweder dem alten oder dem neuen Stand;
   - `CurrentEpoch` steigt monoton.
6. **Vereinigung global und Mandanten-Datei:** `t1` steht in beiden Dateien. Beide Regelsätze wirken, ein Deny aus einer der beiden gewinnt.
7. **Mandanten-Datei mit fremdem Mandanten oder `*`:** Erwartung `FormatException`, der alte Stand bleibt aktiv.
8. **E-1:** Globale Datei nur mit `g`-Zeilen, während `p`-Regeln aktiv sind: Erwartung `InvalidOperationException`, `HasPolicies` bleibt true.
9. **`AddPolicy` mit `*`:** wirkt danach auch für einen Mandanten mit eigener Datei.
10. **`ReloadPoliciesAsync(t1)` ohne Dateien:** Programmatische Regeln von `t1` sind weg, globale Regeln für `t1` bleiben.

Für die Tests 2 und 3 braucht es eine `internal` Lesemethode am Service, z. B. `internal int DiagnosticPolicyCount(string tenant)` und `internal object DiagnosticEnforcerIdentity(string tenant)`. `Autheris.Application` hat bereits `InternalsVisibleTo` für die Unit-Tests.

## 6. Reihenfolge und Aufwand

1. Tests 1–10 schreiben und auf `cb16e6c` rot sehen (Tests 1, 2, 3, 4, 6, 7, 8 müssen rot sein; 5, 9, 10 je nach Zufall bzw. heutigem Verhalten).
2. Quelltypen und `BuildSnapshot` einführen, Parse-Code zusammenführen (`ParsePolicyText`).
3. `Publish` einführen und alle schreibenden Methoden umstellen; Reinjektions-Schleifen löschen. Betroffen sind:
   - im Mandanten-Ladepfad „Also inject wildcard '*' rules from current snapshot“;
   - im globalen Ladepfad „Add wildcard '*' rules to specific tenant enforcers“ und „apply them to preserved per-tenant enforcers“;
   - das Hinzufügen der `g`-Regeln je Enforcer.
4. `PolicySnapshot` auf Frozen- und Immutable-Typen umstellen, `_emptyFallbackEnforcer` und die öffentliche `GetOrCreateEnforcer` entfernen bzw. `private` machen.
5. Thread-Sicherheit von `Enforce` prüfen (4.6), gegebenenfalls Lock je Enforcer.
6. Optional E-5 (4.7).
7. `PolicySimulationService` kann `ParsePolicyText` und `CreateEnforcer` wiederverwenden. Damit ist garantiert, dass Simulation und Durchsetzung gleich bauen (Rest von C-2).
8. `dotnet build`, `dotnet test`, ein Commit „fix(governance): immutable Casbin snapshots rebuilt from policy sources (E-2, E-1)“.

Aufwand: etwa ein Tag inklusive Tests. Der Umbau betrifft nur `CasbinEnforcementService` und gegebenenfalls `PolicySimulationService`. Die öffentliche Schnittstelle `IPolicyEnforcementService` bleibt unverändert, ausgenommen `GetOrCreateEnforcer`, falls öffentlich genutzt.

## 7. Abnahme

- Kein Codepfad ruft nach der Veröffentlichung eines Snapshots noch `AddPolicy`, `AddGroupingPolicy`, `RemovePolicy` oder ähnliches auf einem Enforcer aus einem Snapshot auf. Prüfbar per Code-Suche: Diese Aufrufe gibt es nur noch in `CreateEnforcer`.
- Die Tests 1–10 sind grün.
- Ein globaler Reload ändert die Policy-Anzahl eines unveränderten Mandanten nicht.
- Semantik aus Abschnitt 3 ist in `docs/configuration-guide.md` (Abschnitt Casbin) beschrieben.
