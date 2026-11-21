---
name: qualify-api-contracts
description: "HTTP-, Event-, Datei- und Worker-Verträge über Versionen und Berechtigungsgrenzen hinweg prüfen. Nutzen bei .NET/FastAPI-APIs, NuGet-Protokollen, Gateways, OIDC-Anbindungen, asynchronen Jobs oder clientseitigen Vertragsänderungen."
---

# API- und Eventverträge qualifizieren

Lies die [gemeinsame Sicherheitsbasis](../../policy/security-boundaries.md); sie gilt auch für Quellen, Übergaben und Toolwirkungen.

**Einsatz:** Technisch und fachlich nachgewiesene Schnittstellen statt ausschließlich Swagger-Nennungen. Lies die geltenden Projektanweisungen und den [gemeinsamen Evidenzstandard](../../policy/evidence.md). Wähle den aktuellen maßgeblichen Quellstand; übernimm historische Berichte nicht als neuen Testbefund.

## Ablauf

1. Kanonischen Vertrag, Versions-/Compatibility-Matrix, authentifizierten Akteur und Eigentümer dokumentieren.
2. Echte Client- und Server-Routen samt Timeout, Idempotency-Key, Fehlerformaten, Pagination, Rate Limits und Schemaänderungen vergleichen.
3. Positive, negative, unautorisierte, wiederholte, konkurrierende und abgebrochene Requests testen.
4. Schema- und Dokumentationserfolg von realer Client-Kompatibilität und Dateneffekt unterscheiden.
5. Änderungen versionieren; Breaking Changes und Übergangsmigration mit konkreter Fallback-Strategie versehen.

## Pflichtprüfungen

- Eine gemockte Route beweist keine Drittclient-Kompatibilität.
- 401, 403 und 404 nicht als gleichwertige Autorisierungsreaktionen behandeln.
- Fehler- und Restart-Semantik gehören zum Vertrag.

## Ergebnis

Vertrags-/Akzeptanzmatrix, Fixtures, CI-Checks, Versionierungsrisiken.

## Grenzen

Keine Änderungen externer Gegenstellen oder Credentials ohne Auftrag.

Unterscheide stets **funktioniert grundsätzlich**, **offiziell unterstützt**, **Best Practice** und **sicherheitsseitig empfehlenswert**. Ein Nachweis auf einer Stufe erlaubt keine automatische Aussage auf den anderen Stufen. Wenn Code, Testlogs oder Umgebung fehlen, dokumentiere den konkreten fehlenden Nachweis.

## Optionale Vertiefung

Die im Text genannten ergänzenden Skills sind bedarfsweise Empfehlungen. Sie sind nicht automatisch installiert. Prüfe ihre Verfügbarkeit, bevor du sie lädst; fehlt eine Empfehlung, bearbeite den vorhandenen Scope und benenne nur den tatsächlich fehlenden Nachweis. Die Sicherheitsbasis bleibt verpflichtend.
