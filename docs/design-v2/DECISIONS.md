# Packory – Architecture Decision Records (ADRs)

**Status:** Vorschläge, zur Validierung im ersten technischen Spike. Diese Entscheidungen beschreiben die Produkt-Zielarchitektur; kein Repositorycode wird dadurch automatisch geändert.

## ADR-001 – Modularer Monolith als primäre Laufzeit
**Entscheidung:** Ein ASP.NET-Core-Prozess mit unabhängigen internen Modulen; .NET 10.  
**Grund:** einfaches Docker-Deployment, geringe Betriebskosten, Debuggability, gleiche Storage-/Auth-Bausteine.  
**Gegenargument:** Adapter und Worker können Ressourcen konkurrierend beanspruchen.  
**Minderung:** Module/Ports klar definieren; CPU-/IO-intensive Jobs später in Worker auslagern; keine vorgetäuschte HA.

## ADR-002 – Artefaktart von Distributionsprotokoll entkoppeln
**Entscheidung:** `ArtifactKind` ist unabhängig von `DistributionProtocol`.  
**Grund:** Ein MCP-Server kann per npm/OCI/Remote-URL distribuiert werden; ein Skill per Git und Bundle; Modelle über mehrere Dateien.  
**Gegenargument:** komplexerer Katalog und Referenzmodell.  
**Minderung:** begrenzte gemeinsame Felder, native Adaptermetadaten in separaten Strukturen.

## ADR-003 – Native Protokoll-Adapter statt erzwungenem Universalformat
**Entscheidung:** NuGet, npm, Cargo, PyPI, OCI folgen den jeweiligen veröffentlichten Clientprotokollen.  
**Grund:** Client-Interoperabilität und nachvollziehbare Supportmatrix.  
**Gegenargument:** mehr spezifischer Code.  
**Minderung:** gemeinsames Storage/Identity/Mirror-Framework, Konformitätstests pro Adapter.

## ADR-004 – CAS, unveränderliche Revisionen, DB-gesteuerte Sichtbarkeit
**Entscheidung:** SHA-256 Content-Addressed Storage mit Staging; erst nach validiertem Blob die aktive Revision in der DB freigeben.  
**Grund:** Integrity, Dedup, Recoverability, keine halben Releases.  
**Gegenargument:** Blobs und DB haben keine gemeinsame ACID-Transaktion.  
**Minderung:** idempotente Writes, Orphan-GC, Reconciliation, Fault Injection.

## ADR-005 – Zwei Datenhaltungsprofile
**Entscheidung:** Standalone = SQLite + Filesystem; Server = PostgreSQL + S3-kompatibler Store.  
**Grund:** niedrige Einstiegshürde und später skalierbare Datenspeicherung.  
**Gegenargument:** zwei Betriebsprofile erhöhen Testaufwand.  
**Minderung:** Repository-/Blob-Storage-Port und pro-Provider-Integrationsmatrix.

## ADR-006 – Keycloak optional, Autorisierung immer zentral
**Entscheidung:** OIDC als Authentifizierungsweg, nicht als zwingende Laufzeitabhängigkeit. BFF für das Dashboard; separate Scoped Tokens für Paketclients.  
**Grund:** Keycloak-Kompatibilität bei einfacher Privatinstallation; Paketmanager können nicht generell interaktive OIDC-Flows ausführen.  
**Gegenargument:** lokale Identitäten und API-Tokens erhöhen Komplexität.  
**Minderung:** gemeinsamer serverseitiger Policy-/Principal-Dienst; eindeutige AuthN-/AuthZ-Tests.

## ADR-007 – MCP Registry, Packory MCP und MCP Gateway separat
**Entscheidung:** Registry = `server.json`-Katalog; Packory MCP Server = eigener optionaler Agent-API-Endpunkt; Gateway = späterer isolierter Dienst.  
**Grund:** unterschiedliche Sicherheits- und Protokollverantwortung.  
**Gegenargument:** keine „einzige MCP-Komponente“.  
**Minderung:** gemeinsame Identitäts- und Katalogdienste, getrennte Release-Gates.

## ADR-008 – Untrusted Content niemals ausführen
**Entscheidung:** Packory interpretiert, validiert und speichert Agent Skills/Packages, startet sie aber nicht. Plugins werden zur Build-Zeit kompiliert.  
**Grund:** Untrusted-Code-/Prompt-Injection-Risiko und Multi-Tenant-Sicherheit.  
**Gegenargument:** geringere Flexibilität als Plug-in-Runtimes.  
**Minderung:** spätere externe, isolierte Prüf-Worker nur nach separatem Threat Model.

## ADR-009 – Strikter Umgang mit Hosted/Proxy-Konflikten
**Entscheidung:** Private Namespaces werden reserviert. Eine lokale immutable Paketversion wird nicht durch einen Origin-Download ersetzt. Cargo Source Replacement und private Alternate Registry sind getrennt.  
**Grund:** Schutz vor Dependency Confusion und deterministische Builds.  
**Gegenargument:** weniger automatische Aggregation.  
**Minderung:** später nachvollziehbare explizite Group-Repositories mit klarer Konfliktstrategie.

## ADR-010 – Keine Vorabbehauptung von Protokoll- und HA-Support
**Entscheidung:** Native Clients, Offline-Restores, Parallelität, Fehlerfälle und realer Upstream müssen getestet werden. Single-Instance ist der zuerst unterstützte Modus.  
**Grund:** Ein funktionierender Download oder eine erfolgreiche Beispielinstallation ist keine vollständige Protokollqualifikation.  
**Gegenargument:** langsamere Releasegeschwindigkeit.  
**Minderung:** Fake-Upstream-Tests im PR, echte Origins in periodischer CI, kleine abnahmefähige Releases.

## ADR-011 – Origin-Egress streng kontrollieren
**Entscheidung:** Alle Upstream-Zugriffe über einen gehärteten Fetcher mit Host-/IP-/Redirect-Policy; dynamisch entdeckte Registry-Zielhosts gesondert zulassen.  
**Grund:** SSRF, DNS-Rebinding und untrusted Tarball-/Service-Index-URLs.  
**Gegenargument:** Legitimen privaten Origins muss ein Freigabeweg angeboten werden.  
**Minderung:** explizite Administratorprofile mit definierten Netzbereichen und sicherem Default.

## ADR-012 – Eigenständiger Packory-Kern, keine dauerhafte Fork-Abhängigkeit
**Entscheidung:** Packory-Core unabhängig von NORA, Gitea oder BaGetter; Protokollteile können nach Lizenz-/Architekturreview adaptiert werden.  
**Grund:** geplante starke Produktabweichung (Skills, MCP, OIDC, Modelle, zentrale Distribution).  
**Gegenargument:** höhere anfängliche Implementierungskosten.  
**Minderung:** bewährte Bibliotheken, Referenzimplementierungen und Original-Protokolltests wiederverwenden.

## Review-Fragen

- Welches minimale Hosted/Proxy-Format demonstriert den gemeinsamen Core am besten? Vorschlag: Raw + NuGet.
- Welche persistente Index-/Metadatenstrategie bleibt bei großen npm-Metadaten tragfähig?
- Wie werden Git-Importe ohne fremde Codeausführung und mit nachgewiesener Commit-Identität abgesichert?
- Welche MCP Registry API-Revision wird zuerst gepinnt und vollständig qualifiziert?
- Ab wann rechtfertigt messbare Last die Auslagerung von Workern/OCI?
