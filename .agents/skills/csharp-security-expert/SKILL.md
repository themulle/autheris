---
name: csharp-security-expert
description: >-
  Application security (AppSec) in .NET 10. Guides OWASP Top 10 mitigations, parameterization,
  SSRF prevention, timing-attack defense, TokenValidationParameters, Fail-Closed RBAC/ABAC, and secrets management.
---

# C# & .NET Application Security Expert

Guides defense-in-depth and Zero-Trust security hardening across .NET 10 solutions.

---

## 1. OWASP Mitigations & Secure Coding

- **SQL & Command Injection:**
  - Never concatenate or interpolate raw strings into queries.
  - EF Core: `await context.Database.ExecuteSqlAsync($"UPDATE T SET S = {status} WHERE Id = {id}");` (safe via `FormattableString`).
  - ADO.NET: Always use `cmd.Parameters.AddWithValue("@user", username);`.
  - Process execution: Use `ProcessStartInfo.ArgumentList.Add(...)` instead of raw `Arguments = "..."`.
- **SSRF Prevention (Webhooks / Proxies):**
  - Restrict schemes: `Uri.UriSchemeHttps` only.
  - Resolve IP before connect; reject private/loopback CIDRs (RFC 1918, `127.0.0.0/8`, `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `169.254.169.254`).
  - Guard against DNS rebinding right before socket connect.
- **Deserialization Risks:**
  - `BinaryFormatter` is permanently banned.
  - Standardize on `System.Text.Json`. If using `Newtonsoft.Json`, never set `TypeNameHandling.All` or `Auto` on untrusted payloads.
- **Constant-Time Cryptographic Comparison:**
  - Never compare secrets/HMACs with `==` or `string.Equals()` (vulnerable to timing attacks).
  - Use `CryptographicOperations.FixedTimeEquals(hash1, hash2);`.

---

## 2. Authentication & Authorization

- **Strict Token Validation:**
  ```csharp
  options.TokenValidationParameters = new TokenValidationParameters {
      ValidateIssuer = true,
      ValidateAudience = true,
      ValidateLifetime = true,
      ValidateIssuerSigningKey = true,
      ClockSkew = TimeSpan.FromMinutes(1) // Reduced from default 5m
  };
  ```
- **Policy Authorization:**
  ```csharp
  services.AddAuthorizationBuilder()
      .AddPolicy("RequireAdmin", p => p.RequireRole("Admin"))
      .AddPolicy("DepartmentConsent", p => p.Requirements.Add(new DepartmentConsentRequirement("Finance")));
  ```
- **Fail-Closed Principle:** Require authorization by default (`.RequireAuthorization()`); public access must be explicitly declared with `[AllowAnonymous]`.

---

## 3. Secrets & Protection

- **Dev:** Use `dotnet user-secrets`; never commit credentials to `appsettings.json`.
- **Prod:** Azure Key Vault, AWS Secrets Manager, HashiCorp Vault, or container environment secrets.
- **ASP.NET Core Data Protection:** Persist and encrypt keyring (Azure Blob + Key Vault, Redis + DPAPI) for cluster-consistent tokens.

---

## 4. Edge & Input Hardening

- **Validation:** Whitelist schema input validation at boundaries (FluentValidation).
- **Security Headers:** `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Content-Security-Policy`, `Strict-Transport-Security`.
- **Rate Limiting:** Built-in middleware (Fixed/Sliding Window, Token Bucket) to guard against brute-force and DoS.
- **CSRF Defense:** `[ValidateAntiForgeryToken]` for forms; custom preflight headers (e.g. `GraphQL-Preflight: 1`) for stateless APIs.

---

## 5. Security Audit Checklist

1. [ ] All SQL queries strictly parameterized?
2. [ ] Secret comparisons use `CryptographicOperations.FixedTimeEquals`?
3. [ ] No dynamic command execution via raw string `Process.Start`?
4. [ ] Fail-Closed authorization active by default?
5. [ ] Secrets omitted from git repositories?
6. [ ] External URLs protected against SSRF?
