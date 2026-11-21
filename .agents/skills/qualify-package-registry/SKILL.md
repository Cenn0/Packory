---
name: qualify-package-registry
description: "Native Paketfeeds und Mirroring mit echten Clients, Metadaten, privater Namensraumisolation und Wiederanlauf testen. Für Packory oder BaGetter bei NuGet, npm, Cargo, PyPI, OCI, Maven, Go und Raw; implementierten Endpoint nicht mit vollständigem Protokollsupport verwechseln."
---

# Paketregistry-Protokolle testen

Lies die [gemeinsame Sicherheitsbasis](../../policy/security-boundaries.md); sie gilt auch für Quellen, Übergaben und Toolwirkungen.

**Einsatz:** Jeden Feed separat und gegen echte Clientinteroperabilität abnehmen. Lies die geltenden Projektanweisungen und den [gemeinsamen Evidenzstandard](../../policy/evidence.md). Wähle den aktuellen maßgeblichen Quellstand; übernimm historische Berichte nicht als neuen Testbefund.

## Ablauf

1. Feed-Typ, gehostet vs Cache vs Proxy, Authentisierungsweg, Version und Capability-Matrix festhalten.
2. Native Publish/Restore/Install-/Fehlerfälle mit separat benannten Clients und gesperrtem Netzwerk testen, soweit zulässig.
3. Private Scope-Isolation, Upstream-Fallback, Redirects, Content-Hashes, unveränderliche Identität und gemischte Credentials negativ prüfen.
4. Parallelität, Timeout, Rate Limits, Partial-Upload, Reconciliation und Restore über reale Provider oder klar deklarierte Emulatoren prüfen.
5. Protokollkompatibilität, Sicherheitsfreigabe und Produktionsqualifikation getrennt dokumentieren.

## Pflichtprüfungen

- NuGet Mirror ist kein vollwertiger transparenter NuGet V3 Proxy ohne passende Vertragsbelege.
- Hosted/Proxy/Group-Funktionalität nicht gleichsetzen.
- Fremdregisties nur mit freigegebenen Test-Paketen belasten.

## Ergebnis

Capability-Matrix pro Client/Protokoll, Testnachweise, offene Production Gates.

## Grenzen

Keine Veröffentlichung von privaten Packages oder Skills in öffentliche Feeds.

Unterscheide stets **funktioniert grundsätzlich**, **offiziell unterstützt**, **Best Practice** und **sicherheitsseitig empfehlenswert**. Ein Nachweis auf einer Stufe erlaubt keine automatische Aussage auf den anderen Stufen. Wenn Code, Testlogs oder Umgebung fehlen, dokumentiere den konkreten fehlenden Nachweis.

## Optionale Vertiefung

Die im Text genannten ergänzenden Skills sind bedarfsweise Empfehlungen. Sie sind nicht automatisch installiert. Prüfe ihre Verfügbarkeit, bevor du sie lädst; fehlt eine Empfehlung, bearbeite den vorhandenen Scope und benenne nur den tatsächlich fehlenden Nachweis. Die Sicherheitsbasis bleibt verpflichtend.
