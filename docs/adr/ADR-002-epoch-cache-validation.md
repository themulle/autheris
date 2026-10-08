# ADR-002: Multi-Tier Caching mit deterministischer Epoch-Invalidierung

## Status
Akzeptiert

## Kontext
Um Latenzen < 1 ms bei der Consent-Auflösung (Q-01) zu erreichen, ist Caching unerlässlich. Ein Widerruf eines Zugriffsrechts muss jedoch innerhalb von <= 1 s clusterweit wirksam werden (Q-07). Ein reiner TTL-basierter Cache wäre zu träge; reines Pub/Sub ist unzuverlässig bei Netzwerk-Partitionen ("Fire and Forget").

## Entscheidung
Wir implementieren eine **Epoch-basierte Cache-Validierung**:
1. Jede Tabelle besitzt eine fortlaufende `POLICY_EPOCHS.epoch` Versionsnummer in der Governance-DB.
2. Jede Änderung oder jeder Widerruf erhöht die Epoch atomar in derselben Transaktion.
3. Berechnungsentscheide werden in L1 (`IMemoryCache`) und L2 (`IDistributedCache`) unter Angabe der Berechnungs-Epoch abgelegt.
4. Bei Abfrage wird die aktuelle Epoch via pipelined `MGET` abgeglichen. Weicht die gespeicherte Epoch ab, wird der Cache sofort verworfen.
5. Redis Pub/Sub (`consent:invalidations`) dient als reiner Beschleuniger für proaktive L1-Eviction. Bei Reconnect wird der L1-Cache vollständig geleert.
6. Fail-Closed: Bei Ausfall der Epoch-Validierung wird der Zugriff auf hochsensible Tabellen verweigert.

## Konsequenzen
### Positiv
- Mathematische Garantie für Korrektheit und sofortigen Widerruf (<= 1s).
- Keine Abhängigkeit der Korrektheit von verlustbehaftetem Pub/Sub.
- Hohe Trefferquote bei regulärem Lesezugriff.

### Negativ
- Zusätzlicher Roundtrip zur Epoch-Prüfung bei L1-Hits (kompensiert durch pipelined MGET und Sub-Millisekunden-Redis-Latenz).
