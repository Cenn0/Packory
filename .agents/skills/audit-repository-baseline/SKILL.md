---
name: audit-repository-baseline
description: "Aktuelle Repository-Baseline mit Quellstand, Roadmap, offenen PRs, CI-Gates, Architektur und Risikogrenzen nachvollziehbar erfassen. Nutzen vor jeder größeren Umsetzung, Übernahme, Release- oder Mehrrepository-Analyse; nicht aus alten Berichten auf den aktuellen Status schließen."
---

# Repository-Baseline prüfen

Lies die [gemeinsame Sicherheitsbasis](../../policy/security-boundaries.md); sie gilt auch für Quellen, Übergaben und Toolwirkungen.

**Einsatz:** Reproduzierbaren Iststand vor Änderungsentscheidungen feststellen. Lies die geltenden Projektanweisungen und den [gemeinsamen Evidenzstandard](../../policy/evidence.md). Wähle den aktuellen maßgeblichen Quellstand; übernimm historische Berichte nicht als neuen Testbefund.

## Ablauf

1. Repository-Identität, Sichtbarkeit, Default-Branch, Head-SHA, Berechtigungs- und ggf. Dirty-Status ermitteln; fehlende Rechte als ungeprüft kennzeichnen.
2. AGENTS.md, README, führende Roadmap, ADRs, Release-/Security-Regeln und relevante Implementierungs-/Teststellen am gleichen Quellstand lesen.
3. Offene PRs, Blocker, Reviews, Konflikte, Pflichtprüfungen und letzten Buildstand unterscheiden. Ein Status-Badge oder historischer Bericht belegt nicht den aktuellen Commit.
4. Pro Fachbereich Codebeleg, Vertrags-/Migrationsrisiko, Testmatrix, operativen Einfluss und ungeklärte Fremdabhängigkeiten festhalten.
5. Prioritäten nach Eintrittspfad und Ausfallwirkung ableiten; dokumentierte Ziele strikt vom tatsächlich gebauten und getesteten Stand trennen.

## Pflichtprüfungen

- Jede wesentliche Statusaussage enthält Repository, Commit/Ref, Datei oder verlinkten Run und Beobachtungsdatum.
- Ohne CI-, Laufzeit- oder Gerätezugriff nicht „erfolgreich getestet“ melden.
- Private Dateien, Kundendaten und Secrets bleiben außerhalb öffentlicher Arbeitsartefakte.

## Ergebnis

Baseline-Tabelle mit Quellen, PR/CI-Befund, offenen Risiken, ausführbarer Prioritätenfolge und ungeprüften Punkten.

## Grenzen

Eine Bestandsaufnahme autorisiert weder automatischen Merge noch Releases, produktive Tests oder Konfigurationsänderungen.

Unterscheide stets **funktioniert grundsätzlich**, **offiziell unterstützt**, **Best Practice** und **sicherheitsseitig empfehlenswert**. Ein Nachweis auf einer Stufe erlaubt keine automatische Aussage auf den anderen Stufen. Wenn Code, Testlogs oder Umgebung fehlen, dokumentiere den konkreten fehlenden Nachweis.

## Ressource für Zielintegration

Bei ausdrücklich beauftragter AiSet-Integration die [Rollout-Vorlage](assets/resource-rollout.md) verwenden. Live-Sichtbarkeit und Weitergaberechte aller Inhalte prüfen; temporäre Installationsprüfungen nicht als Produktrollout oder bestätigte Agentenaktivierung ausgeben.
