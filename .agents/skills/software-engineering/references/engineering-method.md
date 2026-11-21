# Engineering-Methodik: Entscheidung, Änderung, Betrieb

Diese Methodik ist ein **kontextabhängiger Leitfaden**, keine universelle Architekturvorschrift. Prüfe zuerst reale Anforderungen, Quellstand und betroffene Nutzer. Der [Hauptskill](../SKILL.md) steuert Ausführung und Autorisierung.

## 1. Qualitätsanforderungen als überprüfbare Szenarien

Lege nur für relevante Eigenschaften explizite Szenarien fest:

| Eigenschaft | Frage / möglicher Nachweis |
| --- | --- |
| Fachliche Korrektheit | Welche Invariante, unabhängiger Referenzfall oder versionierte Spezifikation entscheidet? |
| Sicherheit und Datenschutz | Wer darf welche Daten/Aktionen nutzen? Was geschieht bei fehlenden oder widersprüchlichen Rechten? |
| Zuverlässigkeit | Welche Nutzeraktion muss bei Teilstörung noch funktionieren? Wie wird ein Fehler erkannt und begrenzt? |
| Performance / Kapazität | Bei welcher Last, Datengröße und Hardware gelten Latenz- oder Durchsatzziele? |
| Interoperabilität | Welche API-/Event-/Dateiversionen, Clients und Fehlermodi müssen gleichzeitig unterstützt werden? |
| Änderbarkeit / Wartung | Wie groß ist der Änderungsradius? Wer betreibt, testet und migriert den Dienst? |
| Betrieb und Kosten | Welche Infrastruktur, Lizenzen, Bereitschaft, Backups, Observability und laufenden Aufwände entstehen? |

ISO/IEC 25010:2023 kann für die systematische Auswahl von Qualitätsmerkmalen dienen; diese Tabelle ist eine **eigene operative Auswahl**, nicht der reproduzierte Normtext.

## 2. Optionen und Tradeoffs

Prüfe `A: kleinste sichere Änderung im Bestand`, `B: alternative Architektur` und bei Bedarf `C: bewusstes Zurückstellen`. Optionen nur vergleichen, wenn sie die fachlichen Muss-Kriterien erfüllen. Für jede Variante festhalten:

- Welche Anforderungen werden nachweislich erfüllt und welche scheitern?
- Neue Komponenten, Abhängigkeiten, Datenflüsse, Cloud-/Herstellerbindung und Betriebszuständigkeiten.
- Initiale und fortlaufende Kosten, Migrations-/Schulungskosten, Wartbarkeit und Exit-Strategie.
- Ausfallarten, Angriffsflächen, Datenverlust/Inkonsistenz, Erkennung, Recovery und Restrisiko.
- Belegstatus: Codebeobachtung, veröffentlichte Herstellerzusage, Test, Messung, Annahme oder unbekannt.
- Entscheidungsgrund, gezielter Gegenbeweis und Anlass zur Neubewertung.

Keine numerische Gesamtbewertung ohne transparente, von Stakeholdern legitimierte Kriterien und Gewichtung. Eine aktuelle Technologie ist nicht automatisch zweckmäßig; `microservice`, `event-driven`, `Kubernetes` und `LLM` sind Mittel, keine Ziele.

## 3. Grenzen statt Happy Path

| Grenze | Fragen für Entwurf und Negativtests |
| --- | --- |
| Trust / Identität | Authentifiziert, aber nicht autorisiert? Fremder Tenant? Abgelaufene oder entzogene Berechtigung? |
| Daten und Schema | Alte und neue Version parallel? Reihenfolge/Schema unbekannt? Teilmigration, Audit und Readback? |
| Netzwerk / Queue | Timeout nach ausgeführtem Seiteneffekt? Idempotenz/Deduplizierung? Doppelte Nachricht? Out-of-order? Retry-Sturm? |
| Nebenläufigkeit | Gleichzeitiger Schreibzugriff, verlorenes Update, Lock/Deadlock, Racing auf Statuswechsel? |
| Ressourcen | Lastspitze, Speicher-/Storage-Grenze, Backpressure, Abbruch und überlange Eingaben? |
| Betrieb | Kaltstart, Neustart, Konfigurationsfehler, Secret-Rotation, Backup- und Restore-Probe? |
| Release | Feature-Flag, Rollout-Stufen, Monitoring, kompensierbare und nicht kompensierbare Schritte? |

Ein sicherer Rückweg ist nicht stets `rollback`: bei Daten-/Schemaänderungen kann nur ein geprüfter Forward-Fix oder Wiederherstellung aus einem geprüften Backup möglich sein. Gefährliche Fehlerbilder zuerst in isolierten, autorisierten Umgebungen mit synthetischen Daten nachstellen.

## 4. Test- und Evidenzpyramide nach Risiko

1. Fachvertrag mit beobachtbaren Nachbedingungen definieren.
2. Den günstigsten Test wählen, der den realen Fehlerpfad aufdecken kann: Unit, Contract, Integration mit echter isolierter Abhängigkeit, wenige E2E und gezielte Recovery-/Lastproben.
3. Mindestens eine gezielte negative Gegenprobe je kritischer Invariante. Unabhängige Oracles statt erwarteter Werte aus dem zu prüfenden Code.
4. Belege an Commit, Befehle, Testdatenklasse und Zielumgebung binden. `NOT_RUN` ist kein `PASS`.
5. Die Eigenprüfung eines Autors/Agenten nicht als unabhängigen Code-Review ausgeben; zuständige Reviewer und erforderliche Checks bleiben maßgeblich.

## 5. Betrieb und Messgrößen

Google SRE beschreibt SLIs, SLOs und Fehlerbudgets als Mittel zur Priorisierung **unter vorhandener Serviceverantwortung**. Definiere SLOs nicht willkürlich. Relevante Messungen können Endnutzererfolg, Latenz, Datenfrische, Korrektheit, Kapazität und Wiederherstellbarkeit sein.

DORA unterscheidet in seinen fünf Software-Delivery-Metriken **Durchsatz** (Change Lead Time, Deployment Frequency, Failed Deployment Recovery Time) von **Instabilität** (Change Fail Rate, Deployment Rework Rate). Kennzahlen für Teams/Services nur mit Definition und Zeitfenster vergleichen; sie beweisen allein weder Softwarequalität noch Sicherheit.

## 6. Abbruch- und Eskalationskriterien

Stoppe nur den konkret gefährlichen oder nicht autorisierten Eingriff, wenn: Datenhoheit ungeklärt, irreversibler Schritt nicht abgesichert, Security- oder Rechtegrenze unklar, notwendiges Gate rot/ausgesetzt oder Zielsystem nicht autorisiert. Lege Blocker, Alternativen und sichere nächste Schritte offen; sonst arbeite die übrigen beauftragten Teile weiter.

## 7. Nichtziele

Kein automatisches Produkt-Release, kein Architektur-Rewrite ohne belegten Nutzen, keine Zertifizierung von ISO-/OWASP-/NIST-Konformität, kein Ersatz für Normtexte, Fachfreigaben oder Betreiberentscheidungen. Ein Dokument, Mock oder KI-Bericht ist kein Ersatz für Messung und echten Betrieb.
