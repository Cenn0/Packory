---
name: design-effective-tests
description: "Fachlich aussagekräftige, wartbare Unit-, Integrations-, Vertrags-, Sicherheits-, E2E- und Recoverytests entwerfen und implementieren. Bei neuen Features, regressionsanfälligem Code, produktionskritischer Software und CI-Teststrategien einsetzen; nicht auf triviale Coverage-Zahlen optimieren."
---

# Tests entwickeln, die reale Fehler entdecken

Ein Test ist wertvoll, wenn eine plausible falsche Implementierung ihn verlässlich scheitern lässt und er den legitimen Auftrag absichert. Lies den [Evidenzstandard](../../policy/evidence.md) und verpflichtend die [gemeinsamen Sicherheits- und Autoritätsgrenzen](../../policy/security-boundaries.md), die [Testmuster](references/test-design-patterns.md) und die [Testfallkarte](assets/test-case-card.md).

## 1. Risiko → Verhalten → unabhängiges Oracle

1. Fixiere Komponente/Commit, Benutzer-/Fachprozess, Daten- und Vertrauensgrenzen sowie die tatsächlich geltende fachliche Regel. Gewünschte Architektur nicht als beobachtetes Verhalten deklarieren.
2. Formuliere aus jedem wesentlichen Risiko eine **beobachtbare Invariante**: Eingabezustand → Aktion → erlaubter Zustand/Fehler → verbotene Nebenwirkungen. Notiere fachliche Akzeptanz, unabhängige Berechnung oder versionierten Vertrag **vor** dem Testcode.
3. Wähle die billigste Schicht, die das Risiko tatsächlich nachweisen kann: Unit für reine Regeln, Vertrag/Komponente für Serialisierung/RBAC, echte Integration für DB/Queue/Datei, wenige E2E für Nutzerwege. Kein starres Prozentverhältnis und keine mehrfach redundanten Tests.
4. Verwende Arrange–Act–Assert beziehungsweise Given–When–Then mit klarem Namen „Szenario – erwartetes Ergebnis“. Jeder Test besitzt mindestens eine prüfbare, fachlich relevante Assertion und einen Fehlergrund.

## 2. Fälle aus den Grenzen des Systems ableiten

- Normalfall, Mindest-/Höchstwerte, exakt danebenliegende Werte, leer/null/unknown, ungültige Formate und unerwartete Versionen.
- Unberechtigt, fremder Tenant, abgelaufene Identität, Replay, doppelte Nachricht, Zeitfenster- und Ressourcengrenze; bei Fehlern auch **fehlende Änderungen** unabhängig kontrollieren.
- Concurrency, Reordering, Retry/Timeout, abgebrochene Transaktion, verlorene Antwort nach effektiver Verarbeitung, Wiederanlauf und Idempotenz.
- Für Berechnungen unabhängige Referenzwerte, Einheiten, Rundung, Grenz-/Monotonieeigenschaften; dieselbe Formel nicht zugleich als erwartetes Oracle kopieren.
- Für Produktions- und Sicherheitspfade Fault-Injection mit klarer Erlaubnis und isolierten synthetischen Daten; realen Infrastruktur-/Geräteeffekt niemals ungefragt auslösen.

## 3. Richtige Abhängigkeiten und Oracles

- Teste echte Verträge mit möglichst echten isolierten Abhängigkeiten (z. B. DB-Container mit fixierter Version); ein Mock beweist nur das Verhalten des Mocks. Bei Fremdservices Fakes für Negativfälle, zusätzlich versionierte Contract- und autorisierte Sandbox-Tests.
- Halte Testdaten, DB, Zeit, Zufall, Port, IDs, Dateipfade und externe Services deterministisch und unabhängig pro Test. Parallelisierung darf keine flakigen Shared-State-Tests erzeugen.
- UI: rollen-/namensbasierte Locators, echte Nutzerzustände, automatisch wartende Assertions; keine willkürlichen Sleeps und keine Screenshot-Gleichheit als fachliches Oracle.
- Readback separat vom geprüften Codepfad. Ein HTTP 200/exit 0, gespeicherter Mock-Receipt, synthetisches Gate oder selbst gemeldeter PASS beweist keine echte Wirkung.

## 4. Implementieren und nachweisen

1. Zuerst eine kontrollierte fehlerhafte Variante (oder eine dokumentierte Gegenprobe) festlegen, die der Test zuverlässig erkennen soll; nicht die Produktionsversion mutieren.
2. Test gezielt **rot** bekommen, sofern sicher/reproduzierbar, danach Implementierung korrigieren und gezielte wie benachbarte Tests erneut ausführen. Für vorhandenen Code zumindest negative Gegenkontrolle.
3. Kein `skip`, `xfail`, blindes Retry, `continue-on-error`, versteckte Testdoubles oder verwässerte Assertions als Reparatur. Flaky-Ursache beheben; quarantänisierte Tests bleiben explizit ohne Freigabewirkung.
4. Bericht mit getesteter Git-Revision, Abhängigkeiten, tatsächlich gestarteten Kommandos, Positiv-/Negativfällen, Fehlerbildern, nicht gelaufenen Tests und verbleibender Unsicherheit.
5. Vor Release unabhängiges Review mit einem unabhängigen Testqualitätsreview. Ein Testname/CI-Grün/Coverage-Wert ist keine Produkt- oder Sicherheitsqualifikation.

## Ergebnis

Liefere eine priorisierte Risiko-/Testmatrix und implementierte, lokal geprüfte Tests nur im freigegebenen Codebereich. Unterscheide `PASS`, `FAIL` und `NOT_RUN` sowie funktioniert grundsätzlich, offiziell unterstützt, Best Practice und sicherheitsseitig empfehlenswert. Bei fehlender Laufzeit nur nachvollziehbare Testdefinitionen und offene Test-Gates ausweisen.
