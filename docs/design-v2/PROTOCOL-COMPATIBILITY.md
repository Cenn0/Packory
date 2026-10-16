# Packory – Protokoll- und Interoperabilitätsmatrix

**Status:** Geplante Test-/Supportmatrix, keine bereits implementierten Funktionen.  
**Stand:** 16. Oktober 2026

## 1. Aussageklassen

- **Prinzipiell möglich:** Adapter lässt sich gegen den öffentlich dokumentierten Standard implementieren.
- **Implementiert, unbestätigt:** Code existiert, aber die vollständige Abnahmematrix ist noch nicht nachgewiesen.
- **Offiziell von Packory unterstützt:** definierte Native-Client-Versionen bestehen reproduzierbare E2E-Tests mit Packory als einziger Quelle. Kein Herstellerzertifikat ist impliziert.
- **Best Practice:** möglichst native Protokollsemantik, unveränderte Artefaktbytes, ausdrückliche Herkunft und wiederholbare Tests.
- **Sicherheitsseitig empfehlenswert:** explizite Berechtigungen und Namespaces, Integritätsprüfung, egress-begrenzte Upstreams, keine Ausführung fremder Inhalte.

## 2. Formate und beabsichtigte Fähigkeiten

| Format | Read/Proxy | Hosted Publish | Besonderheiten | Gate |
|---|---|---|---|---|
| Raw | MVP | MVP | sichere Pfade, unveränderliche Versionen, MIME, Range-Reads später | A |
| NuGet V3 | MVP | MVP | Service Index, Flat Container, Registration, Publish, Suche separat | B |
| npm | MVP | MVP | vollständige/reduzierte Packuments, `dist.tarball`, Integrität, dist-tags, Scopes | C |
| Agent Skills | Git-/Upload-Import | Bundle-Publish | `SKILL.md`, Assets, Git-SHA, Freigabe, keine Ausführung | D |
| MCP Registry | Metadaten-Import | `server.json`-Publish | offizielle Generic Registry OpenAPI, versioniert, Remote/Package-Referenzen | D |
| Cargo | crates.io Source Replacement | eigene Alternate Registry | Sparse Index; keine privaten Crates in crates.io-Ersatz mischen | F |
| PyPI | Simple API | Upload separat | normalisierte Namen, HTML/JSON, multiple Wheels, yanked | F |
| OCI | Proxy/Distribution später | Push später | `/v2/`, Manifeste, Tags, Layers, Upload Sessions, Referrers | F |
| AI Models | Manifeste/Dateien später | Upload später | große Dateien, Hash, Range/Resume, Quota, mehrere Assets | F |
| Packory MCP Server | nicht zutreffend | eigene MCP Tools | Streamable HTTP, scopes, read-only zuerst | F |
| MCP Gateway | später | nicht zutreffend | Auth-Audience, Proxy-Isolation, Tool-/Host-Policies | nach F |

## 3. Verbindliche Abnahmetests für jeden nativen Registry-Adapter

1. **Nur Packory als Quelle:** Client muss ohne direkte Fallback-URL zum Origin arbeiten.
2. **Cold:** Datentransfer über Packory, Paketidentität und native Metadaten korrekt.
3. **Warm:** lokaler Client-Cache geleert; kein erneuter Origin-Download des gleichen unveränderlichen Blobs.
4. **Offline:** Origin blockiert, neuer Client-Cache; vollständiger zuvor gecachter Abhängigkeitsgraph und benötigte Indizes/Metadaten werden bedient. Nur diese definierte Menge darf als offline-fähig bezeichnet werden.
5. **Integrity:** Originalbytes/Herkunftsdigest unverändert; nativer Integrity-Mechanismus validiert.
6. **Störungen:** Origin 404, 429, 5xx, Timeouts, TLS-/DNS-Fehler, ungültige Origin-Referenzen, lokale Speicherausfälle.
7. **Concurrency:** 32 simultane Cold-Requests derselben Version, ein konsistenter lokaler Blob; Koordination und tatsächliche Origin-Request-Zahl messen.
8. **Crash:** Abbruch während Download, Staging, Blob-Publikation und DB-Commit; Recovery ohne halbe Releases oder überschriebenen Hosted-Bestand.
9. **Security:** unzulässige Uploads/Downloads, Namespacekonflikte, Token-Ablauf und Widerruf, SSRF- und Redirect-Tests.
10. **Reproduzierbarkeit:** lockfile-/manifestgebundener Clientinstallationslauf mit konsistentem Ergebnis.

Fake-Origin-Tests laufen in jedem Pull Request; öffentliche reale Upstreams in isolierter manueller bzw. periodischer CI ohne Blockierung aller PRs bei Origin-Störung.

## 4. Protokollbezogene Prüfpunkte

### 4.1 NuGet V3

- `GET /v3/index.json` liefert nur tatsächlich unterstützte und lokal erreichbare Ressourcen.
- `PackageBaseAddress`, `RegistrationsBaseUrl`, Search und Autocomplete werden separat geprüft.
- `dotnet restore` mit direkter/transitiver Abhängigkeit; `dotnet nuget push` für Hosted.
- NuGet-ID-Normalisierung, SemVer2, Prerelease, HEAD, Unicode-Metadaten und vorgelagerte HTTPS-Proxys.
- Warm/offline mit **geleerten Clientcaches**; lokale NuGet-/Hosted-Versionen werden nicht durch Upstream ersetzt.

### 4.2 npm

- Vollständige und abgekürzte Metadaten (`application/vnd.npm.install-v1+json`).
- `dist.tarball` auf Packory-Domäne umschreiben, Integrity-Digest beibehalten, historische Lockfile-URLs als gesonderten Fall testen.
- `npm ci`, `npm install`, `npm publish`, Scopes, URL-Encoding, dist-tags, Unpublish-Policy.
- npm-Client und ggf. pnpm/yarn **getrennt** qualifizieren; keine automatische Supportgleichsetzung.

### 4.3 Cargo

- `sparse+https://.../index/` und `config.json` (`dl`, `api`, ggf. `auth-required`).
- Indexzeilen, Cargo-Checksummen, ETag/304, yanked-Releases.
- crates.io *Source Replacement* mit **identischem Paketinhalt**; separate Private/Alternate Registry für eigene Pakete.
- `cargo build --locked`, `cargo publish --registry <name>`, Authentifizierungsfall und Offline-Materialisierung getrennt testen.

### 4.4 PyPI

- Python Simple Repository API (HTML/JSON) mit korrekter Namensnormalisierung und Link-/Hash-Metadaten.
- Plattform-Wheels, sdist, yanked, Requires-Python, pip konfigurieren und offline-gecachte Menge prüfen.
- Veröffentlichungs-API und Twine-Kompatibilität als gesondertes Feature-Gate.

### 4.5 Agent Skills

- `SKILL.md` mit gültigem YAML-Frontmatter und Dateilayout validieren.
- Dateien, Scripts, Referenzen und Assets bleiben bei Import und Bundle-Export byteidentisch; nachvollziehbarer Git-Commit.
- Kein Ausführen von Skill-Dateien; Policy-/Freigabeprüfung und Quarantäne nach Bedarf.
- Installationshilfe und gegebenenfalls eigene CLI sind **Packory-Konvention**, kein behaupteter universeller Skills-Registry-Standard.

### 4.6 MCP Registry

- Generische API gemäß gepinnter OpenAPI-Version mit `GET /v0.1/servers`, Detail-/Versionsendpunkten und Paging.
- `server.json`-Validierung gegen veröffentlichte Schema-Version, zusätzliche Packory-Policy getrennt.
- Publisher-Eigentum / Namespaces verifizieren; Imports aus offizieller Registry mit Provenienz und Status.
- Remote MCP-URL und Paketreferenz korrekt ausgeben, **nicht automatisch starten oder weiterleiten**.
- Wenn Preview-Spezifikation wechselt, Kompatibilitätsfassung veröffentlichen und Migrationspfad testen.

### 4.7 OCI Distribution

- Getrennte native `/v2/`-API einschließlich Manifests, Layerblobs, Upload, Tags und Fehlercodes.
- OCI Image / Index und ArtifactType nicht mit einem beliebigen Packory-JSON-Manifest verwechseln.
- Digest-Validierung, OCI 1.1 Referrers, mehrere Architekturvarianten und signierte Nebenartefakte separat qualifizieren.
- Empfohlen eigener Hostname für native OCI-Routen, damit `/v2/`-Kollisionen vermeidbar bleiben.

### 4.8 Packory MCP Server

- Offizielles C# MCP SDK, Streamable HTTP, Version/Handshake und serverseitige AuthZ.
- Anfangs nur Read-Tools; niemals ein allgemeines Shell-/Remote-Ausführungswerkzeug.
- Explizite Scope-Checks pro Tool, keine Rückgabe nicht autorisierter privater Inhalte.
- Legacy SSE nur wenn ausdrücklich für bestimmte Clients benötigt und separat gegen Überlast getestet.

## 5. Primärquellen

- https://learn.microsoft.com/en-us/nuget/api/implementation-guide
- https://github.com/npm/registry/blob/main/docs/responses/package-metadata.md
- https://doc.rust-lang.org/cargo/reference/registry-index.html
- https://doc.rust-lang.org/cargo/reference/source-replacement.html
- https://packaging.python.org/en/latest/specifications/simple-repository-api/
- https://github.com/opencontainers/distribution-spec/blob/main/spec.md
- https://github.com/agentskills/agentskills/blob/main/docs/specification.mdx
- https://github.com/modelcontextprotocol/registry/blob/main/docs/reference/api/openapi.yaml
- https://github.com/modelcontextprotocol/csharp-sdk/blob/main/docs/concepts/transports/transports.md
