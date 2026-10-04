# ADR-006: Stateless Kerberos-Only Windows-Authentifizierung im HA-Cluster

## Status
Akzeptiert

## Kontext
Das Gateway läuft in einem hochverfügbaren Cluster hinter einem Load Balancer. Windows-Authentifizierung (`Negotiate`) unterstützt standardmäßig Kerberos und NTLM. NTLM ist jedoch verbindungsbehaftet (Connection-Bound) und erfordert Sticky Sessions (Session-Affinity) am Load Balancer. Bei Reboots oder Ausfällen von Knoten brechen NTLM-Verbindungen ab und können nicht nahtlos von Nachbarknoten übernommen werden.

## Entscheidung
1. **Kerberos-Only Betrieb:** Der Cluster erzwingt `RequireKerberosOnly = true`. NTLM-Fallbacks werden im Clusterbetrieb unterbunden.
2. **Gemeinsamer SPN:** Alle Gateway-Instanzen teilen denselben Service Principal Name (SPN, z. B. `HTTP/gql-gateway.corp.local`) und denselben Dienst-Account bzw. dieselbe Keytab.
3. **Stateless Execution:** Authentifizierung erfolgt pro Kerberos-Ticket; gelöste transitive Gruppen-SIDs werden in Redis gecacht (`GroupCacheTtlMinutes: 5`).
4. **Pre-Auth IP-Rate-Limiter:** Ein In-Memory-Limiter vor der Negotiate-Middleware schützt den Domain Controller vor Ticket-Flooding / Kerberos-DoS.

## Konsequenzen
### Positiv
- Vollkommen zustandsloser Cluster ohne Notwendigkeit für Sticky Sessions.
- Nahtloses Load Balancing und Zero-Downtime Rolling Updates.
- Keine Belastung des Active Directory Domain Controllers bei wiederholten Requests.

### Negativ
- Striktes Kerberos-Setup (SPN-Registrierung, DNS, Zeitsynchronisation) im Active Directory erforderlich.
