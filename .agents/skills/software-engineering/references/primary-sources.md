# Software-Engineering-Primärquellen

**Recherche-/Prüfstand: 21.11.2026.** Diese Links sind **Quellenhinweise und keine ausführbaren Installationsbefehle**. Gültigkeit, Version, Lizenz und konkrete Eignung bei Nutzung erneut prüfen. Der Skill ist eine eigenständige AiSet-Ausarbeitung und übernimmt keine fremden Community-Skills, Prompts oder längeren Originaltexte.

| Quelle | Verifizierter Stand / Einsatz | Grenze |
| --- | --- | --- |
| [NIST SP 800-218](https://csrc.nist.gov/pubs/sp/800/218/final) | SSDF 1.1, final (Februar 2022), sichere Entwicklungs- und Vulnerability-Prozesse | [SSDF 1.2 / SP 800-218 Rev. 1](https://csrc.nist.gov/pubs/sp/800/218/r1/ipd) ist zum Prüfstand **Initial Public Draft**, nicht finale Nachfolgenorm |
| [OWASP ASVS](https://asvs.owasp.org/) | ASVS 5.0.0, stabile Ausgabe vom 30.05.2025, überprüfbare Web-/API-Sicherheitsanforderungen | Geeignete Anforderungen für konkrete Architektur und Verifizierungsniveau wählen; kein pauschales Produktzertifikat |
| [Google Engineering Practices](https://google.github.io/eng-practices/) | Herstellerdokumentation zu Code-Reviews, kleinen Änderungen, Tests, Design und Komplexität | Google-interne Richtlinien sind nicht automatisch Vorgaben für jedes Team |
| [Google SRE Workbook: Implementing SLOs](https://sre.google/workbook/implementing-slos/) | SLI/SLO, Fehlerbudget und betriebsbezogene Priorisierung | SLOs und Eskalationspolitik benötigen Servicekontext und Zuständigkeit |
| [DORA: Metrics History](https://dora.dev/insights/dora-metrics-history/) | Fünf Software-Delivery-Metriken, getrennte Dimensionen Durchsatz und Instabilität | Kein generischer Sollwert und kein Ersatz für Produkt-/Sicherheitsmetriken |
| [ISO/IEC 25010:2023](https://www.iso.org/standard/78176.html) | Offizieller Standard für ein Produktqualitätsmodell mit neun Eigenschaften | Der eigentliche Normtext ist ggf. kostenpflichtig; ohne lizenzierten Zugriff nicht ausgedacht oder als vollständig wiedergegeben behandeln |
| [Agent Skills Spezifikation](https://agentskills.io/specification) | SKILL.md-Metadaten, modulare Referenzen und bedarfsorientiertes Laden | Formatkonformität ist weder inhaltliche Freigabe noch technische Sandbox |

**Einordnung:** Die genannten Quellen stützen ausgewählte Praktiken; daraus ergibt sich keine allgemeingültige `Best Practice` für jede Architektur. Für Plattform-/Versionsfragen immer die zuständige Herstellerdokumentation zum tatsächlich betriebenen Stand prüfen; bei widersprüchlichen Quellen beide Befunde ausweisen.
