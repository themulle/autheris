---
name: csharp-architect
description: >-
  System- und Komponenten-Design für moderne C#/.NET-Lösungen.
  Nutze diesen Skill bei der Projekt- und Solution-Strukturierung, dem Entwurf pragmatischer
  APIs und Domänenmodelle ohne Overengineering sowie der Definition architektonischer
  Leitplanken (Dependency Injection Scopes, I/O-Pfad-Konsistenz).
---

# C# & .NET Solution Architect

Dieser Skill leitet Architekturentscheidungen in C#/.NET-Systemen an. Er fokussiert auf pragmatisches, wartbares Systemdesign, Durchsetzen moderner Standards und das **aktive Verhindern von Overengineering**.

---

## 1. Architektur-Prinzipien & Anti-Overengineering

### Leitlinien
- **Pragmatismus vor Dogmatismus**: Architekturen dienen der Geschäftslogik und Wartbarkeit, nicht theoretischer Reinheit.
- **YAGNI & KISS**: Keine Abstraktionsebene ohne mindestens zwei konkrete Implementierungen oder zwingende Testbarkeitsanforderungen einführen.
- **Keine Fake-Repositories über ORMs**: Wenn EF Core oder moderne SQL-Data-Access-Layer im Einsatz sind, keine generischen Repositories (`IRepository<T>`) aufsetzen. EF Core ist bereits ein Unit-of-Work/Repository-Muster.
- **Pragmatische Schichten**: Clean Architecture mit Augenmaß (Domain -> Application -> Infrastructure -> Api) oder Vertical Slice Architecture. Bei kleinen/mittleren Modulen sind Vertical Slices oder flache Schichten oft überlegener als tiefe Schichten mit DTO-Mapping auf 4 Ebenen.

### Häufige Anti-Patterns (Vermeiden!)
- ❌ Generische Repositories mit Methoden wie `IEnumerable<T> GetAll()` oder `IQueryable<T> Query()`.
- ❌ Übermäßige Mapping-Schichten (Entity -> DomainModel -> ApplicationDto -> ApiResponseDto), wenn das Modell stabil und unverändert durchgereicht wird.
- ❌ Unnötige Microservices für Systeme mit niedrigem bis mittlerem Lastprofil.
- ❌ Komplexe Event-Sourcing- oder CQRS-Frameworks (z. B. MediatR für triviale CRUD-Endpunkte ohne Pipeline-Verhalten).

---

## 2. Projekt- & Solution-Strukturierung

### Clean Architecture mit Augenmaß
```
Solution.sln
├── src/
│   ├── MyApp.Domain/           # Entities, Value Objects, Enums, Interfaces für Domänenlogik (Zero External Dependencies)
│   ├── MyApp.Application/      # Use Cases, DTOs, Geschäftslogik-Services, Interfaces für Infrastruktur
│   ├── MyApp.Infrastructure/   # DB-Zugriffe (EF Core/ADO.NET), Externe APIs, Caching, Event-Bus
│   └── MyApp.Api/              # ASP.NET Core Host, Controller / Minimal APIs, Middleware, Program.cs
└── tests/
    ├── MyApp.Tests.Unit/
    └── MyApp.Tests.Integration/
```

### Vertical Slice Architecture (Alternative für Feature-Driven Services)
```
src/MyApp.Api/
├── Features/
│   ├── Invoices/
│   │   ├── CreateInvoice.cs       # Endpoint, Request/Response DTOs, Handler in einer Datei / Feature-Ordner
│   │   ├── GetInvoiceById.cs
│   │   └── Invoice.cs             # Feature-spezifische Entity oder Domänenlogik
```

---

## 3. Pragmatisches API- & Domänenmodell-Design

- **Value Objects mit C# Records**:
  ```csharp
  public readonly record struct OrderId(Guid Value);
  public readonly record struct Money(decimal Amount, string Currency);
  ```
- **Rich Domain Model vs. Anemic**: Invarianten direkt im Aggregat / in der Entität absichern; keine unkontrollierten öffentlichen Setter:
  ```csharp
  public class Order
  {
      public OrderId Id { get; private init; }
      public OrderStatus Status { get; private set; }
      
      public void MarkAsShipped()
      {
          if (Status != OrderStatus.Paid)
              throw new DomainException("Only paid orders can be shipped.");
          Status = OrderStatus.Shipped;
      }
  }
  ```
- **Result Pattern für erwartete Fehler**: Für Validierungs- und fachliche Fehler Result-Typen (`Result<T>`, `ErrorOr<T>`) bevorzugen, Exceptions für unvorhergesehene Systemfehler reservieren.

---

## 4. Architektur-Leitplanken

### Dependency Injection (DI) Lifetimes
- **Singleton**: Zustandslose Utilities, Caches, EventBusses, Options-Monitore, Single-Instance Engines.
- **Scoped**: DbContext, Unit of Work, Current-User-Context, Request-bezogene Services.
  *Vorsicht:* Niemals Scoped-Services in Singletons injizieren (Scoped Captive Dependency). Im Host `ValidateScopes = true` aktivieren!
- **Transient**: Leichte, zustandslose Komponenten, die bei jedem Aufruf frisch instanziiert werden sollen.

### I/O-Pfad-Konsistenz & Asynchronität
- **Async All the Way**: Niemals `.Result`, `.Wait()` oder `GetAwaiter().GetResult()` auf I/O-Tasks aufrufen.
- **CancellationTokens**: Durchgängig von der Controller-/Minimal-API-Ebene bis zur Datenbank-/HTTP-Abfrage durchreichen:
  ```csharp
  public async Task<InvoiceDto> GetInvoiceAsync(InvoiceId id, CancellationToken ct = default);
  ```
- **HttpClient**: Immer via `IHttpClientFactory` oder typisierte Clients registrieren, niemals manuell instanziieren (`new HttpClient()`).

---

## 5. Review- & Verifikations-Checkliste

Bei der Bewertung von Architekturentscheidungen prüfen:
1. [ ] Ist die Schichtenabhängigkeit unidirektional (Domain kennt weder Infrastructure noch Web)?
2. [ ] Gibt es künstliche Abstraktionen (z. B. Interfaces mit nur einer trivialen Implementierung ohne Testbedarf)?
3. [ ] Sind DI-Lifetimes sauber definiert (keine Scoped-Leaks in Singletons)?
4. [ ] Sind CancellationTokens durchgehend vorhanden?
5. [ ] Werden moderne C# 12/13/14-Features pragmatisch eingesetzt?
