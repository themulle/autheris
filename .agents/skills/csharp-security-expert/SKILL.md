---
name: csharp-security-expert
description: >-
  Application Security (AppSec) und sichere Codierung im .NET-Ökosystem.
  Nutze diesen Skill zur Identifikation und Behebung von Schwachstellen (OWASP Top 10,
  SQL/Command Injection, SSRF, Deserialisierungsrisiken), zur Härtung von Authentifizierung
  und Autorisierung sowie für sicheres Secrets-Management und strikte Input-Validierung.
---

# C# & .NET Application Security Expert

Dieser Skill leitet die Absicherung von .NET-Lösungen nach dem Prinzip **Defense-in-Depth** und **Zero-Trust** an.

---

## 1. Identifikation & Behebung von Schwachstellen (OWASP Top 10)

### SQL & Command Injection
- **SQL-Parameterisierung**: Niemals Benutzereingaben via String-Konkatenation oder String-Interpolation (`$"SELECT * FROM t WHERE id = '{input}'"`) in Abfragen einbetten.
  ```csharp
  // Sicher mit FormattableString in EF Core:
  await context.Database.ExecuteSqlAsync($"UPDATE Invoices SET Status = {status} WHERE Id = {id}");
  
  // Sicher mit ADO.NET:
  cmd.CommandText = "SELECT * FROM Users WHERE Username = @user";
  cmd.Parameters.AddWithValue("@user", username);
  ```
- **Command Injection**: `ProcessStartInfo` niemals mit unvalidierten Argument-Strings aufrufen. Nutze `ArgumentList.Add(...)` statt `Arguments = "..."`.

### Server-Side Request Forgery (SSRF)
- Wenn Benutzer-URLs aufgerufen werden (z. B. Webhooks, Proxying):
  1. Nur explizit erlaubte Schemas zulassen (`Uri.UriSchemeHttps`).
  2. Ziel-IP auflösen und prüfen, dass keine privaten/Loopback-Bereiche adressiert werden (RFC 1918, `127.0.0.1`, `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `169.254.169.254`).
  3. DNS-Rebinding durch Validierung unmittelbar vor dem Connect verhindern.

### Deserialisierungsrisiken
- **Vermeide unsichere Serializer**: `BinaryFormatter` ist in modernem .NET obsolet und gefährlich.
- **System.Text.Json bevorzugen**: Sicherer Standard.
- Bei `Newtonsoft.Json`: `TypeNameHandling.All` oder `Auto` ist eine kritische Schwachstelle (RCE). Niemals mit unvertrauenswürdigen Daten nutzen!

### Timing-Attacks (Seitenkanalangriffe)
- Passwörter, API-Keys, HMAC-Signaturen oder Shared Secrets niemals mit `==` oder `string.Equals()` vergleichen.
- Verwende immer konstante Zeitalgorithmen:
  ```csharp
  CryptographicOperations.FixedTimeEquals(hash1, hash2);
  ```

---

## 2. Authentifizierung & Autorisierung in ASP.NET Core

### Best Practices
- **Multi-Scheme & Dynamic Auth**: Klar getrennte Schemas (Bearer, Basic, Cookies, ForwardAuth).
- **Strikte Token-Validierung**:
  ```csharp
  options.TokenValidationParameters = new TokenValidationParameters
  {
      ValidateIssuer = true,
      ValidateAudience = true,
      ValidateLifetime = true,
      ValidateIssuerSigningKey = true,
      ClockSkew = TimeSpan.FromMinutes(1) // Standard 5 Min reduzieren
  };
  ```
- **Policy-basierte Autorisierung**:
  ```csharp
  services.AddAuthorizationBuilder()
      .AddPolicy("RequireAdmin", policy => policy.RequireRole("Admin"))
      .AddPolicy("DepartmentConsent", policy => 
          policy.Requirements.Add(new DepartmentConsentRequirement("Finance")));
  ```
- **Fail-Closed Prinzip**: Standardmäßig Endpunkte mit `[Authorize]` bzw. `.RequireAuthorization()` schützen; anonyme Zugriffe müssen explizit per `[AllowAnonymous]` freigegeben werden.

---

## 3. Secrets-Management & Data Protection

- **Entwicklung**: `dotnet user-secrets` nutzen; niemals Passwörter oder Connection-Strings in `appsettings.json` committen.
- **Produktion**: Azure Key Vault, AWS Secrets Manager, HashiCorp Vault oder sichere Umgebungsvariablen.
- **ASP.NET Core Data Protection**: Schlüsselmaterial persistent und verschlüsselt speichern (z. B. Azure Blob + Key Vault oder DPAPI/Redis), um CSRF- und Session-Tokens clusterweit konsistent zu halten.

---

## 4. Input-Validierung & Edge Protection

- **Strikte Validierung**: Benutzereingaben an den Rändern des Systems typisieren und validieren (z. B. via FluentValidation). Whitelisting vor Blacklisting.
- **Security Headers**:
  - `X-Content-Type-Options: nosniff`
  - `X-Frame-Options: DENY`
  - `Content-Security-Policy`
  - `Strict-Transport-Security` (HSTS)
- **Rate Limiting**: Schutz vor DoS und Brute-Force mit der integrierten ASP.NET Core Rate-Limiting-Middleware (Fixed Window, Sliding Window, Token Bucket).
- **Anti-CSRF**: `[ValidateAntiForgeryToken]` für formularbasierte Endpunkte; API-Preflight-Header (z. B. `GraphQL-Preflight: 1` oder benutzerdefinierte Header) für zustandslose APIs.

---

## 5. Security-Audit-Checkliste

Bei Code- und Architektur-Reviews prüfen:
1. [ ] Werden Datenbankabfragen ausnahmslos parameterisiert?
2. [ ] Werden vertrauliche Vergleiche mit `CryptographicOperations.FixedTimeEquals` durchgeführt?
3. [ ] Gibt es Stellen mit `Process.Start` oder dynamischer Code-Ausführung?
4. [ ] Sind alle Endpunkte standardmäßig authentifiziert (`RequireAuthorization`)?
5. [ ] Sind Secrets aus dem Quellcode und den Commits ferngehalten worden?
6. [ ] Werden externe URLs vor dem Abruf gegen SSRF abgesichert?
