---
name: csharp-architect
description: >-
  System and component design for C#/.NET 10 solutions. Guides clean layering, anti-overengineering,
  rich domain models, DI lifetime scoping, async I/O consistency, and implementation planning.
---

# C# & .NET Solution Architect

Guides pragmatic, maintainable, and high-performance system architecture in C#/.NET 10 while strictly preventing overengineering.

---

## 1. Principles & Anti-Overengineering

- **Pragmatism over Dogmatism:** Architecture serves business goals and maintainability, not theoretical purism.
- **YAGNI & KISS:** No abstraction without $\ge 2$ concrete implementations or strict testability needs.
- **No Fake Repositories over ORMs:** Do not wrap EF Core or modern data access in generic `IRepository<T>` (EF Core already implements Unit of Work/Repository).
- **Layering with Discretion:** Clean Architecture (`Domain` $\to$ `Application` $\to$ `Infrastructure` $\to$ `Api`) or Vertical Slices. Avoid 4-layer DTO re-mapping when models pass through unchanged.
- **Anti-Patterns to Avoid:**
  - Generic repositories exposing `IEnumerable<T> GetAll()` or `IQueryable<T> Query()`.
  - Excessive DTO mapping cascades (`Entity` $\to$ `DomainModel` $\to$ `AppDto` $\to$ `ApiDto`).
  - Premature microservices for low-to-medium throughput systems.
  - MediatR/CQRS cascades for trivial CRUD endpoints without pipeline behaviors.

---

## 2. Solution Structure Patterns

### Clean Architecture
```
Solution.sln
├── src/
│   ├── App.Domain/          # Pure entities, value objects, domain logic (zero external dependencies)
│   ├── App.Application/     # Use cases, interfaces, orchestrators, validators
│   ├── App.Infrastructure/  # DB access (EF Core/ADO), external APIs, caching, file storage
│   └── App.Api/             # ASP.NET Core host, Minimal APIs/Controllers, middleware, Program.cs
└── tests/
    ├── App.Tests.Unit/
    └── App.Tests.Integration/
```

### Vertical Slice Architecture (Feature-Driven Alternative)
```
src/App.Api/Features/<FeatureName>/
├── Create<Entity>.cs        # Endpoint, Request/Response DTOs, Handler in one file
├── Get<Entity>ById.cs
└── <Entity>.cs              # Feature-specific entity or domain rules
```

---

## 3. Domain & API Design

- **Value Objects via Records:**
  ```csharp
  public readonly record struct OrderId(Guid Value);
  public readonly record struct Money(decimal Amount, string Currency);
  ```
- **Rich Domain Model:** Guard invariants inside aggregates; private setters:
  ```csharp
  public class Order {
      public OrderId Id { get; private init; }
      public OrderStatus Status { get; private set; }
      public void MarkAsShipped() {
          if (Status != OrderStatus.Paid) throw new DomainException("Only paid orders can ship.");
          Status = OrderStatus.Shipped;
      }
  }
  ```
- **Result Pattern:** Use `Result<T>` / `ErrorOr<T>` for anticipated business/validation errors; reserve exceptions for unexpected failures.

---

## 4. Architectural Guardrails

### Dependency Injection Lifetimes
- **Singleton:** Stateless utilities, caches, event buses, options monitors, single-instance engines.
- **Scoped:** DbContext, Unit of Work, current user context, request services.
  - *Rule:* Never inject Scoped into Singleton (Captive Dependency). Enable `ValidateScopes = true` in host.
- **Transient:** Lightweight, stateless components created per call.

### I/O & Concurrency
- **Async All The Way:** Never call `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`.
- **CancellationToken:** Propagate `CancellationToken ct` from entry point to database/HTTP drivers.
- **HttpClient:** Register via `IHttpClientFactory` or typed clients; never use `new HttpClient()`.

---

## 5. Review Checklist

1. [ ] Unidirectional dependencies (Domain has zero references to Infrastructure/Web)?
2. [ ] No redundant abstractions (single-implementation interfaces without test need)?
3. [ ] DI lifetimes correct (no captive dependencies)?
4. [ ] CancellationTokens propagated through all async boundaries?
5. [ ] Modern C# 13 idioms used cleanly?
