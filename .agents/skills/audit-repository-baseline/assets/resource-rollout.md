# Kontrollierte AiSet-Projektintegration

Zielrepository, aktuellen Commit, Sichtbarkeit und Arbeitsbaum erfassen. Diese Werte live prüfen; ein alter Profileintrag ist kein Beleg der heutigen Sichtbarkeit.

1. Den vertrauenswürdig geprüften AiSet-Commit festhalten. Aktuelle Ziel-AGENTS.md lesen. Nur passende Skills und Vorlagen laden; keine vertraulichen Profile, Markennamen, internen URLs oder Referenzen öffentlich übernehmen.
2. Lesenden Installer-Plan für das konkrete Profil prüfen. Alle transitiven Abhängigkeiten und die tatsächlich zu kopierenden Inhalte auf öffentliche Weitergaberechte prüfen. Ein bestandener Profil-Validator ist keine Inhaltsfreigabe.
3. In einem separaten Arbeitszweig explizit installieren. Bestehende Dateien, globale Konfiguration und AGENTS.md nicht überschreiben. Änderungen überprüfen und die Installationsmanifest-Datei zusammen mit den geprüften Ressourcen halten.
4. Read-only Driftcheck gegen denselben vertrauenswürdigen AiSet-Quellstand durchführen. Fehlende, unprotokollierte oder geänderte Dateien gelten als Abweichung; keine automatische Bereinigung oder Entfernung.
5. In der tatsächlich verwendeten Agentenlaufzeit Skill-Entdeckung und einen harmlosen Auslöser prüfen. Installiert, entdeckt, ausgewählt und erfolgreich ausgeführt getrennt dokumentieren.
6. Zielprojekt-Tests und Review ausführen; danach nur autorisiert mergen. Keine Produktions-, Geräte- oder Publish-Rechte aus der Installation ableiten.

| Stufe | Ergebnis / Quelle |
| --- | --- |
| Quellrevision / Rechte / aktuelle Sichtbarkeit | {{Beleg}} |
| Plan / geprüfte öffentliche Inhalte | {{Beleg}} |
| Installation / Manifest / Driftprüfung | {{Beleg}} |
| Laufzeit entdeckt Skill / korrekte Auswahl | NOT_RUN |
| Zielprojekt-CI / Review / Merge | NOT_RUN |

Eine Installation in einem temporären Testverzeichnis ist kein Rollout in ein Produktrepository. Das Fehlen eines Laufzeitnachweises muss im Abschlussbericht stehen.
