# Testdesign: Auswahl und Oracles

| Risiko | Kleinster sinnvoller Test | Fehler, der erkannt werden muss | Unabhängiges Oracle |
| --- | --- | --- | --- |
| Rechen-/Fachregel | parametrisierter Unit-/Property-Test | Grenzwert um eins falsch, falsche Rundung | unabhängig berechneter Fall / Normausgabe |
| JSON/API-Vertrag | Consumer-Provider-Contract | duplizierter Schlüssel, Breaking Field, falscher Fehlercode | versioniertes Schema + echte API-Reaktion |
| Persistenz | isolierter DB-Integrationstest | Datenverlust, doppeltes Schreiben, falscher Tenant | zweite Verbindung / DB-Abfrage |
| Queue/Worker | Integration + kontrollierter Crash | Duplicate Delivery, fehlendes ACK | unabhängiger Queue-/Datenzustand |
| Sicherheit | Negativtest mit Fremd-Tenant | Privilegieneskalation, Datenabfluss | getrennte Autorisierungsentscheidung und Readback |
| UI | Playwright/Komponententest | Rolle/Status falsch, Tastatur/Fokus kaputt | sichtbares Rollen-/Statusverhalten |
| Backup/Upgrade | isolierter Restore-Drill | nur Schema migriert, Daten/Keys fehlen | unabhängiger Fingerprint und funktionaler Readback |
| KI-Vorschläge | synthetische unabhängige Eval | sichere Blockierung verdeckt falsche Absicht | separater Oracle/Custodian und alle Trials |

## Konkrete Testqualität

- Frage vor jeder Implementierung: **Welcher realistische Defekt lässt genau diesen Test scheitern?** Wenn keiner benannt werden kann, ist er vermutlich nicht nützlich.
- Gute Negativfälle prüfen sowohl die **Ablehnung** als auch das **Ausbleiben unerlaubter Effekte**. Ein bloßer HTTP-403-Test prüft nicht automatisch unveränderte Persistenz.
- Tests sollen von selbst und in beliebiger Reihenfolge funktionieren. `sleep(5000)` ist kein Synchronisationsvertrag. Feste Seeds, gefrorene Zeit und eigens provisionierte Daten sind besser.
- Bei Integrationen muss die Abhängigkeit wirklich im Test laufen oder explizit als Fake gekennzeichnet sein. Ein Mock-Pfad ist kein PostgreSQL-, Keycloak-, OPNsense- oder Hyper-V-Lauf.
- Keine selbstreferenziellen Oracles (gleiche Funktion für Erwartung und Ergebnis), keine Testdaten, die den Fehlerfall unbemerkt ausschließen.
- Mutation/Gegenprobe auf isoliertem Branch oder lokaler Fixture: falschen Grenzwert / entfernte AuthZ / kaputte Rundung / doppelten Dispatch gezielt simulieren; die Testklasse muss aus **richtiger Ursache** rot werden.

## Originaldokumentation (28.10.2026 nachgesehen)

- [Microsoft: .NET Unit Testing Best Practices](https://learn.microsoft.com/en-us/dotnet/core/testing/unit-testing-best-practices) – schnell, isoliert, wiederholbar, selbstprüfend, AAA.
- [pytest: Good Integration Practices](https://docs.pytest.org/en/stable/explanation/goodpractices.html) – venv, Testlayout, Importmodus.
- [Playwright: Best Practices](https://playwright.dev/docs/best-practices) – sichtbares Nutzerverhalten, test isolation, web-first Assertions.
- [Fowler: Practical Test Pyramid](https://martinfowler.com/articles/practical-test-pyramid.html) – Granularität und aussagekräftige Integrationsgrenzen (ergänzende Sekundärquelle).

Methodik ist eine eigene Synthese; keine Herstellerzertifizierung, keine Kopie fremder Skills. Version/Framework im Zielprojekt stets erneut prüfen.
