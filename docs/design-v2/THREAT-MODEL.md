# Packory – Sicherheitsarchitektur und Bedrohungsmodell

**Status:** Sicherheitsanforderungen für Design und spätere Implementierung; keine Produktionsfreigabe.  
**Stand:** 16. Oktober 2026

## 1. Schutzziele und Vertrauensgrenzen

| Schutzziel | Beispiel |
|---|---|
| Vertraulichkeit | Private Pakete, Manifestdaten, PATs, OIDC-Claims, Origin-Zugangsdaten nicht preisgeben |
| Integrität | Immutable Originalbytes, korrekte Metadaten, kein stiller Wechsel lokaler/öffentlicher Artefakte |
| Verfügbarkeit | Fehlertolerantes Mirroring, Limits, Wiederanlauf, Schutz vor Origin-/Client-Überlast |
| Authentizität/Herkunft | Origin, Hash, Signatur/Attestation und Publisher-Freigabe unterscheiden |
| Nachvollziehbarkeit | Audit-Trail für Writes, Entscheidungen, Quarantäne, Token-Ausgabe, Rechte |

**Grenzen:** Browser ↔ API; Paketmanager ↔ native Endpunkte; Packory ↔ externe Upstreams/Git; Packory ↔ DB/Object Storage; Packory ↔ OIDC; Packory ↔ externe KI/MCP-Clients. Aus externen Quellen übernommene README-, Skill-, Paket- und Modell-Metadaten sind **nicht vertrauenswürdig**.

## 2. Threat Model (Auszug)

| Angriff | Eintrittspfad | Erforderliche Kontrolle | Nachweis |
|---|---|---|---|
| Dependency Confusion | Private Namen im öffentlichen Origin | Namespace-Reservation, Fail-Closed, feste Quelle je Revision | Integrationstest mit kollidierender ID und Version |
| SSRF / DNS Rebinding | NuGet Service Index, npm tarball, Git-URLs, Redirects | Host-/IP-Allowlist, DNS/IP-Validierung, Revalidation jedes Hops, Blockierung interner Metadaten-Endpunkte | Fake-Origin mit lokalen Redirects, DNS-Wechsel und URL-Tricks |
| Überschreiben veröffentlichter Versionen | Publish + Proxy-Repair | Immutable Hosted-Revisionen, DB-Unique-Constraints, geprüfte CAS-Revisionsreferenzen | Fault-Injection und konkurrierende Publikationen |
| Unauthorized Read / IDOR | Management-API / Blob-Download | Default-Deny, ACL auf Katalogeintrag **und** Asset, keine direkten CAS-URLs | Test mit zwei getrennten Identitäten/Feeds |
| Token-Leak | Logs, Web-UI, Fehlerdetails, Referer | Maskierung, sichere Cookies, Token einmal anzeigen, Rotation und Revocation | Log- und Frontend-Tests, Secret-Scanning |
| Token-Weitergabe an Fremdsysteme | OIDC / MCP / Upstream-Redirect | Audience-/Issuer-Prüfung, keine Weitergabe von Browser- oder Service-Tokens an untrusted Origins | Test mit redirectender Fremddomain |
| Giftige Paketmetadaten / XSS | README, Markdown, SVG, HTML, Paketbeschreibungen | Sanitization, Content Security Policy, Content-Disposition, MIME-Validierung | XSS-Fuzzing, bösartige Dokumente |
| Archive Traversal / Zip Bomb | Skills, NuGet/nupkg, npm/tgz, generic ZIP | Pfadnormalisierung, Größen- und Entpacklimits, Symlink-/Linkregeln | Boundary- und Fuzztests |
| Prompt Injection / Skill Malware | SKILL.md, Anweisungen, Skripte | Keine Ausführung im Registry-Prozess, Quarantäne, Freigabe, Herkunft und Nutzerhinweise | Import und Katalogansicht ohne Side Effects |
| Public Registry Takedown / Origin-Ausfall | Cache Miss, Index Fetch | Klassifizierte Fehler, begrenzte Retries, nur geprüfte warme Offline-Artefakte anbieten | 404/429/503/Timeout + Cold/Warm/Offline |
| Disk Exhaustion / Zip Flood | große Blobs, Traffic | Quotas, Uploadlimits, Backpressure, Streaming, GC nur für Cache | Ressourcen- und Lasttests |
| Supply Chain / CI Secrets | GitHub Actions, Build-Dependencies | Minimalrechte, Pinning, SBOM, Secret-Scanning, Code Review, signierte Releases optional | Security-Workflows |

## 3. Identität, Authentifizierung, Autorisierung

### 3.1 Webanmeldung

- Optional OIDC mit Keycloak oder anderen Standard-Providern: Authorization Code + PKCE, verifizierter Issuer, Audience, JWKS, State und Nonce.
- BFF-Muster: serverseitige Sitzung statt Bearer-Token im lokalen Browser-Storage; Cookies `Secure`, `HttpOnly`, geeignete `SameSite`-Einstellung.
- CSRF-Schutz für browserbasierte Schreiboperationen; passende Origin-/CORS-/CSP-Konfiguration.
- Lokale Anmeldung für Standalone ohne Standardpasswort. Einmaliger Setup-Prozess, bei Headless-Deployments sicherer Setup-Token / Loopback-Only-Flow, keine Veröffentlichung von Bootstrap-Secrets im Log.
- Deaktivierung/Ablauf von Sessions; Rollenänderungen und Token-Widerruf wirksam machen.

### 3.2 Paketclients und Automatisierung

- Starke, zufällige und opaque Service-Tokens; serverseitig nur Hash/HMAC-basierter Prüfnachweis, niemals Klartext nach Ausstellung speichern.
- Token-ID zur Revocation, Ablaufzeit, optional erlaubte Feed-/Namespace-Operationen; Zugriffstokens nicht in URLs, Logs oder HTML einbetten.
- Rechte werden serverseitig gegen den konkreten Feed geprüft: `catalog.read`, `repository.read`, `artifact.publish`, `artifact.yank`, `repository.manage`, `policy.manage`, `mcp.publish`, `system.admin`.
- Keycloak-Gruppenzugehörigkeit ist lediglich ein Eingangssignal für ein konfiguriertes Mapping, keine implizite Adminberechtigung.
- Private Registry-URLs geben keine Paketinformationen an Unberechtigte preis. Keine Rückschlüsse über Timing-/Fehlermeldungsunterschiede ohne explizite Prüfung.

### 3.3 MCP-spezifisch

- Registry-API und Packory MCP Server getrennt konfigurieren und autorisieren.
- MCP-Registrierung importiert Metadaten, startet und ruft entfernte MCP-Server **nicht** automatisch auf.
- `server.json` mit Remote-URL verlangt überprüfbare Herkunft; Registrierung bedeutet keine Sicherheitsfreigabe.
- MCP-Tools zuerst `read-only`; Writes nur mit eigenständigem Scope, Audit und gegebenenfalls zusätzlicher Freigabe.
- Ein späterer MCP-Gateway benötigt eigenes Security Review: OAuth Protected Resource Metadata, Token-Audience, Tool-Berechtigungen, SSRF, Fehlerisolierung, Egress und Secret-Weitergabe.

## 4. Artefakt-Supply-Chain

1. **Integrität:** prüfe SHA-256 über empfangene Originalbytes und native Hashangaben (npm Integrity, Cargo `cksum`, NuGet/OCI-Digests etc.).
2. **Herkunft:** halte Origin-URL, Fetch-Zeit, Native-ID, Originaldigest und gegebenenfalls unveränderlichen Source-Commit fest.
3. **Vertrauen:** dokumentiere Verifikationsart, Quarantäne-/Freigabestatus; Hash allein beweist nicht, wer ein Artefakt erstellt hat.
4. **Scans:** optionale SBOM-, Malware-, Policy- und Advisory-Jobs; Scanbefund ist ein Signal, keine Garantie.
5. **Keine Ausführung:** Package-Postinstall, Skill-Skripte, Modelldateien und MCP-Konfigurationen werden durch die Registry nicht gestartet.
6. **Reviewer-Berechtigung:** Freigaben müssen getrennt von Paket-Uploads möglich sein. Freigaben sind auditierbar und auf konkrete unveränderliche Revisionen bezogen.
7. **Lizenzen:** Upstream- und Paketlizenzen getrennt von der Packory-Quellcodelizenz betrachten. Nutzungsbedingungen für Mirrors beachten.

## 5. Speicherung, Cache und Recovery

- Blobname aus intern berechnetem SHA-256; keine untrusted Pfadangaben übernehmen.
- Nicht allein aus einem Dateipfad auf eine Berechtigung schließen: alle Reads über autorisierte Artefakt-Referenzen.
- Staging auf einem kontrollierten Filesystem, atomare Publikation des Blobs, danach DB-Transaktion zur sichtbaren Freigabe.
- Bei PostgreSQL+Object Storage gilt **keine** cross-system ACID-Garantie; Recovery durch Idempotenz und Reconciliation.
- Negative Cache-Einträge nur für bestätigte Fehler und mit begrenzter Lebensdauer. Permanente 404 nach upstream 5xx/Timeout verhindern.
- Lokale Hosted-Releases dürfen bei Origin-Reparatur oder Quota-GC nicht ersetzt/gelöscht werden.
- Für öffentliche Reverse-Proxies Uploadlimits, Forwarded-Header-Allowlist und TLS festlegen.

## 6. Öffentliches Repository und Datenschutz

Packory ist öffentlich. Diese Regeln gelten für Source, Dokumentation, Issues, Beispiele, Artefakte, Testfixtures und Commit-Metadaten:

- Keine persönlichen Kontaktadressen und keine Klartext-Tokens, Private Keys, Produktions-URLs oder IP-Adressen.
- Neutrale Domains aus reserviertem Bereich `example.invalid` und generische Namen in Beispielen.
- Für Git-Autor/Committer nur eine konfigurierte No-reply-Identität; die tatsächliche Commit-Historie vor Veröffentlichung/Übernahme gesondert prüfen.
- Keine ungeprüften Laufzeit-Dumps, Cache-Dateien oder .env-Dateien einchecken.
- Geplante GitHub-Workflows: `dotnet test`, Frontend-Lint/Test, Dependency-Review, CodeQL/entsprechende SAST, Secret-Scanning, SBOM, Container-Security, License-Audit.
- Bei Herkunftsübernahmen müssen rechtlich verpflichtende Copyright-/Lizenzhinweise bestehen bleiben; Datenschutz ist kein Grund, fremde Zuschreibungen pauschal zu löschen.

**Achtung:** Eine öffentliche, bereits abrufbare Git-Commit-Historie kann selbst nach einem Force-Push in Forks/Caches persistieren. Der Schutz zukünftiger Commits verhindert keine rückwirkende Offenlegung.

## 7. Release-Gates vor öffentlicher Produktivempfehlung

- [ ] Defaults ohne öffentliches Publish; fehlende Auth führt nicht zu `allow`.
- [ ] OIDC Authorization Code + PKCE gegen Test-Keycloak, inkl. abgelaufener/ungültiger Tokens und Rollenänderungen.
- [ ] Opaque Token: Ablauf, Widerruf, Feed-ACL, Logs frei von Secrets.
- [ ] Origin-SSRF inklusive Redirects, DNS-/IP-Besonderheiten und untrusted Metadaten-Endpunkten getestet.
- [ ] Cross-feed read/write isolation und Asset-/IDOR-Tests erfolgreich.
- [ ] Upload-, Archive- und Metadaten-Limits und Crash-Recovery getestet.
- [ ] Namespace-Confusion-Szenarien verhindert.
- [ ] Weder Skill-Import noch MCP-Metadatenregistrierung führen fremde Skripte aus.
- [ ] Backup-/Restore-Prozedur mit DB und Blob-Referenzen getestet.
- [ ] Reproduzierbare echte Client- und Offline-Tests für jede als unterstützt deklarierte Registry.
- [ ] Keine personenbezogenen oder geheimen Daten in Releases, Beispielkonfigurationen oder Commit-Metadaten.

## 8. Primärquellen

- Keycloak OIDC: https://www.keycloak.org/securing-apps/oidc-layers
- ASP.NET Core OIDC / BFF: https://learn.microsoft.com/en-us/aspnet/core/security/authentication/configure-oidc-web-authentication?view=aspnetcore-10.0
- MCP Registry Authorization: https://github.com/modelcontextprotocol/registry/blob/main/docs/reference/api/registry-authorization.md
- MCP Registry `server.json`: https://github.com/modelcontextprotocol/registry/blob/main/docs/reference/server-json/generic-server-json.md
- Cargo Source Replacement: https://doc.rust-lang.org/cargo/reference/source-replacement.html
- OCI Distribution: https://github.com/opencontainers/distribution-spec/blob/main/spec.md
