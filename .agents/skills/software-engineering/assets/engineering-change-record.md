# Engineering-Änderungs- und Entscheidungskarte (Vorlage)

> Vorlage, keine ausgefüllte Freigabe. Kopie nur im beauftragten Zielkontext anlegen; keine Secrets oder produktiven Nutzdaten eintragen.

## Kontext und Baseline

- Auftrag / Nutzerwirkung:
- Repository, Branch, vollständiger Commit, Datum:
- Aktueller Codepfad und originale Belegstellen:
- Fachliche Muss-Kriterien und Nichtziele:
- Vertrauens-/Daten-/Versionsgrenzen:

## Akzeptanz und Risiko

| Invariante / Risiko | Positiv- / Negativfall | Erwartete erlaubte Wirkung | Verbotene Nebenwirkung | Oracle / Evidenz |
| --- | --- | --- | --- | --- |
| Beispiel: Tenant-Trennung | Fremder Tenant liest Ressource | Zugriffsfehler | Keine fremden Daten, keine Mutation | Separater AuthZ-/DB-Test |

## Optionen und Entscheidung

| Option | Erfüllt Muss? | Nutzen | Kosten / Wartung / Betrieb | Sicherheits- und Ausfallrisiko | Nachweisstand |
| --- | --- | --- | --- | --- | --- |
| Minimale Bestandsänderung | offen | offen | offen | offen | nicht geprüft |
| Alternative | offen | offen | offen | offen | nicht geprüft |
| Zurückstellen | offen | offen | offen | offen | nicht geprüft |

- Entscheidung und fachlicher Entscheidungsgrund:
- Annahmen, Gegenargumente, verworfene Optionen und Revisionsauslöser:
- Anforderungen für Daten- und Vertragsmigration / Rückwärtskompatibilität:
- Rollout, Monitoring, Recovery, Rollback bzw. Forward-Fix:

## Verifikation und Übergabe

| Prüfung | Version / Umgebung | Tatsächliches Ergebnis | Beleg | Restrisiko |
| --- | --- | --- | --- | --- |
| Fachliche Tests | offen | NOT_RUN | — | offen |
| Sicherheits-/Negativfälle | offen | NOT_RUN | — | offen |
| Migration / Recovery | offen | NOT_RUN | — | offen |
| Build / CI / Review | offen | NOT_RUN | — | offen |

- Betroffene Dateien, PR/Commit, aktualisierte Dokumentation:
- Funktioniert grundsätzlich: belegt / offen + Grundlage
- Offiziell unterstützt: belegt / offen + Hersteller/Version
- Best Practice: begründeter Kontext / Alternativen
- Sicherheitsseitig empfehlenswert: Threat Model / Kontrollen / Restrisiko
- Blocker und nächster autorisierter Schritt:
