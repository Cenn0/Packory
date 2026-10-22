#!/usr/bin/env python3
"""Real process + offline restore of two private feeds for every E0 package kind.

Uses only synthetic artifacts and temporary tokens. Native package-manager
interoperability has separate smoke scripts; this verifies their stored wire
artifacts, exact grants and OCI tags after an actual offline backup/restore.
"""
import base64
import hashlib
import io
import json
import os
from pathlib import Path
import secrets
import sqlite3
import struct
import subprocess
import tarfile
import tempfile
import time
import urllib.error
import urllib.request
import zipfile

import offline_backup

ROOT = Path(__file__).resolve().parents[1]
KINDS = ("nuget", "npm", "cargo", "pypi", "oci", "raw", "maven", "go")
BASE = "http://127.0.0.1:18099"


def json_bytes(value):
    return json.dumps(value, separators=(",", ":")).encode()


def archive_zip(entries):
    output = io.BytesIO()
    with zipfile.ZipFile(output, "w") as archive:
        for path, body in entries.items():
            archive.writestr(path, body)
    return output.getvalue()


def archive_tar(entries):
    output = io.BytesIO()
    with tarfile.open(fileobj=output, mode="w:gz") as archive:
        for path, body in entries.items():
            header = tarfile.TarInfo(path)
            header.size = len(body)
            archive.addfile(header, io.BytesIO(body))
    return output.getvalue()


def multipart(fields, files):
    boundary = "packory-" + secrets.token_hex(16)
    output = bytearray()
    for name, value in fields.items():
        output += (f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"\r\n\r\n{value}\r\n').encode()
    for name, filename, body in files:
        output += (f'--{boundary}\r\nContent-Disposition: form-data; name="{name}"; filename="{filename}"\r\n'
                   'Content-Type: application/octet-stream\r\n\r\n').encode() + body + b"\r\n"
    output += f"--{boundary}--\r\n".encode()
    return bytes(output), "multipart/form-data; boundary=" + boundary


def main():
    admin = secrets.token_hex(32)
    process = None
    with tempfile.TemporaryDirectory(prefix="packory-feed-restore-") as directory:
        work = Path(directory)
        records = []
        grants = {}

        def request(method, path, body=None, token=None, media="application/octet-stream", expected=200):
            headers = {"Content-Type": media}
            if token:
                headers["Authorization"] = "Bearer " + token
            req = urllib.request.Request(BASE + path, data=body, headers=headers, method=method)
            try:
                with urllib.request.urlopen(req, timeout=20) as response:
                    status, payload = response.status, response.read()
            except urllib.error.HTTPError as error:
                status, payload = error.code, error.read()
            assert status == expected, f"{method} {path}: expected {expected}, got {status}: {payload[:160]!r}"
            return payload

        def post(path, data):
            return request("POST", path, json_bytes(data), admin, "application/json", 201)

        def start(data):
            nonlocal process
            environment = dict(os.environ, PACKORY_DATA_DIRECTORY=str(data),
                               PACKORY_PUBLIC_BASE_URL=BASE, ASPNETCORE_URLS=BASE,
                               PACKORY_ADMIN_TOKEN=admin, PACKORY_ANONYMOUS_READ="true",
                               PACKORY_PUBLISH_REQUESTS_PER_MINUTE="1000",
                               PACKORY_MCP_ENABLED="false")
            log = (work / "server.log").open("ab")
            process = subprocess.Popen(["dotnet", str(ROOT / "src/Packory/bin/Release/net10.0/Packory.dll")],
                                       env=environment, stdout=log, stderr=subprocess.STDOUT)
            log.close()
            for _ in range(100):
                if process.poll() is not None:
                    raise AssertionError("Private restore test server exited before readiness")
                try:
                    request("GET", "/health/ready")
                    return
                except (OSError, AssertionError):
                    time.sleep(.1)
            raise AssertionError("Private restore test server readiness timeout")

        def stop():
            nonlocal process
            if process is not None:
                process.terminate()
                try:
                    process.wait(timeout=20)
                except subprocess.TimeoutExpired:
                    process.kill()
                    process.wait(timeout=10)
                    raise AssertionError("Server failed clean shutdown before offline backup")
                process = None

        def retain(path, body, feed, token):
            assert request("GET", path, token=token) == body
            records.append((path, body, feed, token))

        try:
            start(work / "source")
            for kind in KINDS:
                for label in ("alpha", "beta"):
                    feed = kind + "-" + label
                    post("/api/v1/admin/feeds/drafts", dict(id=feed, kind=kind, mode="hosted", displayName="Synthetic restore"))
                    request("POST", "/api/v1/admin/feeds/" + feed + "/activate", b"", admin)
                    for permission in ("read", "publish"):
                        credentials = json.loads(post("/api/admin/tokens", dict(
                            role="read" if permission == "read" else "write",
                            namespace="feed:" + feed + ":" + permission, expiresInHours=1)))
                        grants[(feed, permission)] = credentials["token"]
                    writer, reader = grants[(feed, "publish")], grants[(feed, "read")]
                    prefix = "/feeds/" + feed
                    flavor = label.encode()
                    if kind == "raw":
                        path = prefix + "/raw/same-package/1.0.0"
                        request("PUT", path, flavor, writer, expected=201)
                        retain(path, flavor, feed, reader)
                    elif kind == "maven":
                        maven_root = prefix + "/maven/org/example/demo/"
                        path = maven_root + "1.0.0/demo-1.0.0.jar"
                        body = archive_zip({"fixture.txt": flavor})
                        request("PUT", path, body, writer, expected=201)
                        retain(path, body, feed, reader)
                        pom = b'<project><groupId>org.example</groupId><artifactId>demo</artifactId><version>1.0.0</version></project>'
                        request("PUT", maven_root + "1.0.0/demo-1.0.0.pom", pom, writer, "application/xml", 201)
                        retain(maven_root + "1.0.0/demo-1.0.0.pom", pom, feed, reader)
                        for build in (1, 2):
                            resolved = "2.0-20261022.000000-" + str(build)
                            for extension in ("pom", "jar"):
                                artifact_path = maven_root + "2.0-SNAPSHOT/demo-" + resolved + "." + extension
                                contents = (b'<project><groupId>org.example</groupId><artifactId>demo</artifactId>'
                                            b'<version>2.0-SNAPSHOT</version></project>') if extension == "pom" else archive_zip({"fixture.txt": flavor + str(build).encode()})
                                request("PUT", artifact_path, contents, writer, expected=201)
                                retain(artifact_path, contents, feed, reader)
                            document = ('<?xml version="1.0" encoding="UTF-8"?>\n<metadata modelVersion="1.1.0">'
                                '<groupId>org.example</groupId><artifactId>demo</artifactId><version>2.0-SNAPSHOT</version>'
                                '<versioning><snapshot><timestamp>20261022.000000</timestamp><buildNumber>' + str(build) +
                                '</buildNumber></snapshot><lastUpdated>20261022000000</lastUpdated><snapshotVersions>' +
                                ''.join('<snapshotVersion><extension>' + extension + '</extension><value>' + resolved +
                                    '</value><updated>20261022000000</updated></snapshotVersion>' for extension in ('pom', 'jar')) +
                                '</snapshotVersions></versioning></metadata>\n').encode()
                            metadata_path = maven_root + "2.0-SNAPSHOT/maven-metadata.xml"
                            request("PUT", metadata_path, document, writer, "application/xml", 201 if build == 1 else 204)
                        retain(metadata_path, document, feed, reader)
                        retain(metadata_path + ".sha256", hashlib.sha256(document).hexdigest().encode(), feed, reader)
                        versions = (b'<metadata><groupId>org.example</groupId><artifactId>demo</artifactId><versioning>'
                                    b'<latest>2.0-SNAPSHOT</latest><release>1.0.0</release><versions><version>1.0.0</version>'
                                    b'<version>2.0-SNAPSHOT</version></versions><lastUpdated>20261022000000</lastUpdated></versioning></metadata>\n')
                        request("PUT", maven_root + "maven-metadata.xml", versions, writer, "application/xml", 201)
                        retain(maven_root + "maven-metadata.xml", versions, feed, reader)
                    elif kind == "nuget":
                        body = archive_zip({"same-package.nuspec": b'<package><metadata><id>same-package</id><version>1.0.0</version><authors>synthetic</authors><description>restore fixture</description></metadata></package>',
                                            "content/fixture.txt": flavor})
                        upload, media = multipart({}, [("package", "same-package.1.0.0.nupkg", body)])
                        request("PUT", prefix + "/nuget/v3/push", upload, writer, media, 201)
                        retain(prefix + "/nuget/v3/flatcontainer/same-package/1.0.0/same-package.1.0.0.nupkg", body, feed, reader)
                    elif kind == "npm":
                        manifest = dict(name="same-package", version="1.0.0")
                        body = archive_tar({"package/package.json": json_bytes(manifest), "package/index.js": flavor})
                        manifest["dist"] = dict(shasum=hashlib.sha1(body).hexdigest(), integrity="sha512-" + base64.b64encode(hashlib.sha512(body).digest()).decode())
                        publish = dict(name="same-package", versions={"1.0.0": manifest})
                        publish["dist-tags"] = {"latest": "1.0.0"}
                        publish["_attachments"] = {"same-package-1.0.0.tgz": dict(content_type="application/octet-stream", length=len(body), data=base64.b64encode(body).decode())}
                        request("PUT", prefix + "/npm/same-package", json_bytes(publish), writer, "application/json", 201)
                        retain(prefix + "/npm/same-package/-/1.0.0.tgz", body, feed, reader)
                    elif kind == "cargo":
                        body = archive_tar({"same-package-1.0.0/Cargo.toml": b'[package]\nname="same-package"\nversion="1.0.0"\n',
                                            "same-package-1.0.0/src/lib.rs": flavor})
                        metadata = json_bytes(dict(name="same-package", vers="1.0.0", deps=[], features={}, authors=[]))
                        upload = struct.pack("<I", len(metadata)) + metadata + struct.pack("<I", len(body)) + body
                        request("PUT", prefix + "/cargo/api/v1/crates/new", upload, writer)
                        retain(prefix + "/cargo/api/v1/crates/same-package/1.0.0/download", body, feed, reader)
                    elif kind == "pypi":
                        filename = "same_package-1.0.0-py3-none-any.whl"
                        body = archive_zip({"same_package-1.0.0.dist-info/METADATA": b'Metadata-Version: 2.1\nName: same-package\nVersion: 1.0.0\n',
                                            "same_package/__init__.py": flavor})
                        upload, media = multipart({"name": "same-package", "version": "1.0.0", ":action": "file_upload", "protocol_version": "1", "sha256_digest": hashlib.sha256(body).hexdigest()}, [("content", filename, body)])
                        request("POST", prefix + "/pypi/legacy/", upload, writer, media)
                        retain(prefix + "/pypi/files/same-package/" + filename, body, feed, reader)
                    elif kind == "go":
                        module = "example.com/same-package"
                        mod = b'module example.com/same-package\n\ngo 1.23\n'
                        body = archive_zip({module + "@v1.0.0/go.mod": mod, module + "@v1.0.0/fixture.txt": flavor})
                        upload, media = multipart({"module": module, "version": "v1.0.0"}, [("mod", "go.mod", mod), ("zip", "module.zip", body)])
                        request("POST", prefix + "/api/go/modules", upload, writer, media, 201)
                        retain(prefix + "/go/" + module + "/@v/v1.0.0.zip", body, feed, reader)
                    else:
                        prefix = "/v2/feeds/" + feed + "/same-package"
                        digest = "sha256:" + hashlib.sha256(flavor).hexdigest()
                        request("POST", prefix + "/blobs/uploads/?digest=" + digest, flavor, writer, expected=201)
                        manifest = dict(schemaVersion=2, mediaType="application/vnd.oci.image.manifest.v1+json",
                                        config=dict(mediaType="application/vnd.oci.image.config.v1+json", digest=digest, size=len(flavor)), layers=[])
                        body = json_bytes(manifest)
                        request("PUT", prefix + "/manifests/stable", body, writer, manifest["mediaType"], 201)
                        retain(prefix + "/manifests/stable", body, feed, reader)
                        retain(prefix + "/blobs/" + digest, flavor, feed, reader)

            stop()
            with sqlite3.connect(work / "source/packory.sqlite") as connection:
                connection.execute("PRAGMA wal_checkpoint(TRUNCATE)")
            connection.close()
            offline_backup.create(work / "source", work / "snapshot")
            offline_backup.restore(work / "snapshot", work / "restored")
            start(work / "restored")
            for path, body, feed, token in records:
                assert request("GET", path, token=token) == body
                request("GET", path, expected=401)
                other = feed.rsplit("-", 1)[0] + ("-beta" if feed.endswith("-alpha") else "-alpha")
                request("GET", path, token=grants[(other, "read")], expected=403)
            stop()
            # A changed archived object must fail before restore can create a target.
            first_blob = next((work / "snapshot/blobs").rglob("*.bundle"))
            first_blob.write_bytes(b"synthetic archive corruption")
            try:
                offline_backup.restore(work / "snapshot", work / "must-not-exist")
            except ValueError:
                pass
            else:
                raise AssertionError("Corrupted snapshot was accepted")
            assert not (work / "must-not-exist").exists()
            print("Offline restore: 16 private feeds across 8 kinds; Maven V11 exact metadata, two SNAPSHOT generations, bytes/grants/tags and corruption rejection passed.")
        finally:
            stop()


if __name__ == "__main__":
    main()
