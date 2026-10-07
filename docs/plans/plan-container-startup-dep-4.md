# Implementierungsplan: DEP-4 Container Startup Failure Behebung

**Status:** ✅ Abgeschlossen & verifiziert  
**Datum:** 2026-10-07  
**Befund:** DEP-4 (`security-review-2026-10-07.md`, Abschnitt Laufzeitfehler)  
**Schweregrad:** Hoch (Laufzeit-Blocker für Docker-Image)  
**Komponenten:** `Dockerfile`, `docker-compose.yml`, `README.md`, `GatewayServiceCollectionExtensions.cs`, `SqliteGovernanceRepository.cs`, `SecurityReview20261002HostingTests.cs`

---

## 1. Problemursachen (Root Cause Analysis)

### 1.1 Kestrel HTTPS-Absturz ohne Zertifikat (`ASPNETCORE_HTTPS_PORTS=8081`)
- **Ursache:** Im `Dockerfile` (Zeilen 13-14, 23) und in `docker-compose.yml` (Zeilen 18, 22) war `ASPNETCORE_HTTPS_PORTS=8081` und `EXPOSE 8080 8081` definiert.
- **Auswirkung:** In ASP.NET Core versucht Kestrel beim Start, einen TLS-Listener auf Port 8081 zu binden. In einer produktiven Container-Umgebung existiert standardmäßig kein Entwicklerzertifikat und kein konfiguriertes Produktionszertifikat. Kestrel bricht mit `InvalidOperationException: Unable to configure HTTPS endpoint. No server certificate was specified...` sofort ab.
- **Architektonischer Standard:** Container in Kubernetes / Docker Compose laufen auf Plain HTTP (8080); TLS-Terminierung erfolgt am Ingress Controller oder Reverse Proxy (Traefik, Envoy, Nginx). In-Container HTTPS erfordert explizit gemountete Zertifikate und Opt-in.

### 1.2 Berechtigungsfehler SQLite Governance-DB (`/app/governance.db`)
- **Ursache:** Das Image nutzt `USER $APP_UID` (UID 1654, gehärteter Non-Root-Container). Das Arbeitsverzeichnis `/app` gehört jedoch `root:root` mit `0755`-Berechtigungen. `GatewayOptions.GovernanceDbOptions.ConnectionString` ist standardmäßig `"Data Source=governance.db;Cache=Shared"`.
- **Auswirkung:** SQLite versucht beim Start, die Datei `/app/governance.db` sowie Journal- und Sperrdateien (`-wal`, `-shm`) im Arbeitsverzeichnis `/app` anzulegen. Dies schlägt für `$APP_UID` mit `SQLite Error 14: 'unable to open database file'` fehl und beendet den Prozess.

---

## 2. Architektur- & Lösungsentwurf

### 2.1 Dockerfile-Härtung
1. **Ports:**
   - Entfernen von `ASPNETCORE_HTTPS_PORTS=8081` aus `ENV`.
   - Ändern von `EXPOSE 8080 8081` zu `EXPOSE 8080`.
2. **Datenverzeichnis & Berechtigungen:**
   - Vor dem Wechsel auf `USER $APP_UID` Verzeichnis `/app/data` erstellen und Eigentümerschaft an `$APP_UID:$APP_UID` übergeben:
     ```dockerfile
     RUN mkdir -p /app/data && chown -R $APP_UID:$APP_UID /app/data
     VOLUME /app/data
     ```
3. **Standard-Verbindungszeichenfolge im Image:**
   - Setzen von `ENV Gateway__GovernanceDb__ConnectionString="Data Source=/app/data/governance.db;Cache=Shared"`.

### 2.2 Docker-Compose & Dokumentation
1. **docker-compose.yml:**
   - Entfernen von `ASPNETCORE_HTTPS_PORTS=8081` und Port-Mapping `8081:8081`.
   - Hinzufügen des persistenten Volumes `autheris-data:/app/data`.
2. **README.md:**
   - Anpassen der `docker run`-Beispiele auf Port 8080 und Mount von `/app/data`.

### 2.3 Resilienz & Startvalidierung im Quellcode
1. **`GatewayServiceCollectionExtensions.ValidateGatewayOptions`:**
   - Wenn `DOTNET_RUNNING_IN_CONTAINER=true` aktiv ist und SQLite konfiguriert ist, prüfen, ob die Verbindungszeichenfolge auf eine Datei direkt im `/app`-Wurzelverzeichnis zeigt (z. B. `governance.db` ohne Verzeichnis oder `/app/governance.db`).
   - In diesem Fall mit einer klaren, präzisen `ValidationException` (DEP-4) abbrechen und auf `/app/data/governance.db` hinweisen.
2. **`SqliteGovernanceRepository`:**
   - Hilfsmethode `EnsureSqliteDirectoryExists(connectionString)` aufrufen: Wenn der SQLite-Dateipfad ein Unterverzeichnis enthält (z. B. `/app/data`), wird sichergestellt, dass dieses Verzeichnis existiert (`Directory.CreateDirectory`).

---

## 3. Testgetriebene Umsetzung (TDD)

1. **Unit-Tests in `SecurityReview20261002HostingTests.cs`:**
   - `DEP04_Dockerfile_DoesNotExposeOrBindHttpsWithoutCert`: Prüft, dass `Dockerfile` und `docker-compose.yml` keine `ASPNETCORE_HTTPS_PORTS`-Einträge und keine Expose/Bindung von Port 8081 enthalten.
   - `DEP04_Dockerfile_ConfiguresDataVolumeAndPermissions`: Prüft, dass `Dockerfile` `/app/data`, `chown` auf `$APP_UID`, `VOLUME /app/data` und `Gateway__GovernanceDb__ConnectionString` auf `/app/data/governance.db` setzt.
   - `DEP04_ValidateGatewayOptions_InContainer_RelativeSqlitePath_AbortsStartup`: Prüft, dass `ValidateGatewayOptions` bei `DOTNET_RUNNING_IN_CONTAINER=true` und Pfad `governance.db` mit DEP-4-Validierungsfehler abbricht.
   - `DEP04_ValidateGatewayOptions_InContainer_AppDataSqlitePath_IsAllowed`: Prüft, dass `/app/data/governance.db` akzeptiert wird.
   - `DEP04_SqliteGovernanceRepository_CreatesDirectoryIfMissing`: Prüft, dass `SqliteGovernanceRepository` ein nicht existierendes Verzeichnis automatisch anlegt.

---

## 4. Verifikationsschritte
1. Ausführen der Unit-Tests (`dotnet test tests/Autheris.Tests.Unit`).
2. Ausführen der Architektur-Tests (`dotnet test tests/Autheris.Tests.Architecture`).
3. Gesamte Solution bauen (`dotnet build Autheris.sln`).
4. Aktualisieren von `status-und-umsetzungsplan-2026-10-07.md`.
