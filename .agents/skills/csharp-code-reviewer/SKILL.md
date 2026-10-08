---
name: csharp-code-reviewer
description: >-
  Tägliche Qualitätssicherung auf Code-Ebene für Pull Requests und Refactorings in C#/.NET.
  Nutze diesen Skill zur Überprüfung moderner C#-Idiome (Pattern Matching, Nullable Reference Types,
  Records, Primary Constructors), Identifikation von Code-Smells, unsauberer Exception-Behandlung
  und zur Bewertung von Testbarkeit und Testqualität (xUnit, NUnit, Shouldly, Testcontainers).
---

# C# & .NET Code Reviewer

Dieser Skill leitet die strukturierte, konstruktive und präzise Code-Überprüfung für Pull Requests, Refactorings und neue Features in modernen C#/.NET-Projekten an.

---

## 1. Moderne C#-Idiome & Best Practices

### Nullable Reference Types & Guard Clauses
- **Keine unbedachten Null-Forgiving-Operatoren (`!`)**:
  - `!` nur verwenden, wenn die Null-Sicherheit durch das Framework garantiert ist (z. B. nach vorangegangener Assertion).
- **Prägnante Argument-Validierung**:
  ```csharp
  // Modern in .NET 8+:
  ArgumentNullException.ThrowIfNull(service);
  ArgumentException.ThrowIfNullOrWhiteSpace(tenantId);
  ```

### Pattern Matching & Switch-Expressions
- Verschachtelte `if / else if`-Kaskaden durch lesbare `switch`-Expressions und Property Patterns ersetzen:
  ```csharp
  public decimal CalculateDiscount(Order order) => order switch
  {
      { Customer.IsVip: true, Total: > 1000m } => 0.20m,
      { Total: > 500m }                        => 0.10m,
      _                                        => 0.0m
  };
  ```

### Records, Collection Expressions & Primary Constructors
- **Collection Expressions**: `int[] numbers = [1, 2, 3];` statt `new int[] { 1, 2, 3 };`.
- **Primary Constructors für Dependency Injection**:
  ```csharp
  public sealed class InvoiceService(
      IInvoiceRepository repository,
      ILogger<InvoiceService> logger) : IInvoiceService
  {
      // Direkter Zugriff auf repository und logger ohne redundante private readonly Felder
  }
  ```
- **Records für DTOs & Events**: Automatische Wertgleichheit und unveränderliche Datenstrukturen.

---

## 2. Code-Smells & Sauberkeit

### Exception-Behandlung
- ❌ **StackTrace-Verlust verhindern**:
  ```csharp
  // FALSCH: Zerstört den originalen StackTrace!
  catch (Exception ex)
  {
      logger.LogError(ex, "Error");
      throw ex;
  }

  // RICHTIG:
  catch (Exception ex)
  {
      logger.LogError(ex, "Error occurred");
      throw; // Behält den StackTrace bei
  }
  ```
- ❌ Keine leeren `catch { }` Blöcke ohne Protokollierung oder begründete Ausnahmebehandlung.
- **Exception Filters**: Spezifische Fehler gezielt abfangen:
  ```csharp
  catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
  ```

### Ressourcen-Management
- `using var` für `IDisposable` nutzen.
- `await using var` für `IAsyncDisposable` (z. B. Streams, DbContexts, Verbindungen) verwenden.

---

## 3. Testbarkeit & Test-Qualität

### Teststruktur (Arrange-Act-Assert)
- Tests müssen unabhängig, deterministisch und lesbar sein.
- **Benennungskonvention**: `MethodName_StateUnderTest_ExpectedBehavior` (z. B. `AuthenticateAsync_WhenTokenIsExpired_ReturnsFailure`).
- **Assertions**:
  - Lesbare Frameworks wie Shouldly bevorzugen:
    ```csharp
    result.Succeeded.ShouldBeTrue();
    result.Value.ShouldNotBeNull();
    ```

### Mocking mit NSubstitute
- Nur externe Schnittstellen mocken, keine reinen Datenklassen oder Value Objects.
- Sicherstellen, dass die Using-Direktive `using NSubstitute;` importiert ist, um `.Returns(...)` nutzen zu können.

### Integrationstests mit Testcontainers
- Für Datenbank-, Redis- oder RabbitMQ-Tests echte Container via `Testcontainers` anstelle instabiler Mocking-Konstrukte oder abweichender In-Memory-Datenbanken verwenden:
  ```csharp
  public sealed class PostgresFixture : IAsyncLifetime
  {
      private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().Build();
      public string ConnectionString => _container.GetConnectionString();
      public Task InitializeAsync() => _container.StartAsync();
      public Task DisposeAsync() => _container.DisposeAsync().AsTask();
  }
  ```

---

## 4. Pull-Request Review-Schablone

Bei der Abgabe von Feedback strukturieren nach:

1. **🔴 Blocker / Kritisch**: Bugs, Sicherheitslücken, Race Conditions, Memory Leaks, StackTrace-Verlust.
2. **🟡 Empfehlung / Verbesserung**: Performance-Tuning, idiomatische C#-Verbesserungen, Testabdeckung.
3. **💡 Nitpick / Optional**: Naming, kosmetische Code-Formatierung.
4. **🌟 Lob**: Gelungene Designmuster oder besonders saubere Implementierungen explizit hervorheben.
