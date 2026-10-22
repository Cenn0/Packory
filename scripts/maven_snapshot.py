"""Bounded validation of Maven's database-only mutable metadata during restore.

This module performs no network access or database mutation. The byte ledger and
same-scope references are checked independently of outer backup-file hashes.
"""
import base64
from datetime import datetime
import hashlib
import re
import xml.etree.ElementTree as ET

MAX_BYTES = 16 * 1024 * 1024
MAX_VERSIONS = 4096
METADATA_COLUMNS = ("scope", "path", "content_base64", "sha256", "size", "updated_at")
ASSET_COLUMNS = ("path", "group_id", "artifact_id", "version", "sha256", "size",
                 "media_type", "created_at")
FEED_COLUMNS = ("id", "kind", "mode", "visibility")
MEDIA_TYPES = {"pom": "application/xml", "module": "application/json",
               "jar": "application/java-archive", "war": "application/java-archive",
               "aar": "application/java-archive", "zip": "application/zip",
               "tar.gz": "application/gzip"}
NAMESPACE = "http://maven.apache.org/METADATA/1.1.0"


def valid_segment(value):
    return (isinstance(value, str) and re.fullmatch(r"[A-Za-z0-9_][A-Za-z0-9_.-]{0,127}", value) is not None
            and ".." not in value)


def valid_scope(value):
    return value == "" or (isinstance(value, str) and re.fullmatch(r"[a-z][a-z0-9-]{1,62}[a-z0-9]", value) is not None)


def valid_date(value):
    if not isinstance(value, str):
        return False
    try:
        return datetime.fromisoformat(value.replace("Z", "+00:00")).tzinfo is not None
    except ValueError:
        return False


def valid_clock(value, dotted=False):
    pattern = r"[0-9]{8}\.[0-9]{6}" if dotted else r"[0-9]{14}"
    if not isinstance(value, str) or re.fullmatch(pattern, value) is None:
        return False
    try:
        datetime.strptime(value, "%Y%m%d.%H%M%S" if dotted else "%Y%m%d%H%M%S")
        return True
    except ValueError:
        return False


def snapshot_value(base, value):
    if (not valid_segment(base) or not base.endswith("-SNAPSHOT") or len(base) <= 9
            or not isinstance(value, str)):
        return None
    prefix = base[:-9] + "-"
    if not value.startswith(prefix):
        return None
    match = re.fullmatch(r"([0-9]{8}\.[0-9]{6})-([1-9][0-9]{0,9})", value[len(prefix):])
    if not match or not valid_clock(match[1], dotted=True) or int(match[2]) > 2147483647:
        return None
    return match[1], int(match[2])


def path_segments(path):
    if not isinstance(path, str) or len(path) > 1024:
        raise ValueError("Invalid Maven repository path")
    parts = path.split("/")
    if not 3 <= len(parts) <= 20 or any(not valid_segment(part) for part in parts):
        raise ValueError("Invalid Maven repository path")
    return parts


def parse_asset_path(path):
    parts = path_segments(path)
    if len(parts) < 4 or any("." in part for part in parts[:-3]):
        raise ValueError("Invalid Maven group path")
    group, artifact, version, filename = ".".join(parts[:-3]), parts[-3], parts[-2], parts[-1]
    if len(group) > 255:
        raise ValueError("Invalid Maven group identity")
    resolved = version
    if version.lower().endswith("-snapshot"):
        if not version.endswith("-SNAPSHOT"):
            raise ValueError("Noncanonical Maven SNAPSHOT version")
        prefix = artifact + "-" + version[:-9] + "-"
        if not filename.startswith(prefix):
            raise ValueError("Maven SNAPSHOT bytes must use a timestamped identity")
        match = re.match(r"([0-9]{8}\.[0-9]{6}-[1-9][0-9]{0,9})([.-])", filename[len(prefix):])
        if not match:
            raise ValueError("Invalid timestamped Maven file")
        resolved = version[:-9] + "-" + match[1]
        if snapshot_value(version, resolved) is None:
            raise ValueError("Invalid timestamped Maven version")
    prefix = artifact + "-" + resolved
    if not filename.startswith(prefix):
        raise ValueError("Maven file identity differs from directory")
    suffix = filename[len(prefix):]
    classifier = None
    if suffix.startswith("."):
        extension = suffix[1:]
    elif suffix.startswith("-"):
        classifier, separator, extension = suffix[1:].partition(".")
        if not separator or not valid_segment(classifier):
            raise ValueError("Invalid Maven classifier")
    else:
        raise ValueError("Invalid Maven file extension")
    if extension not in MEDIA_TYPES:
        raise ValueError("Unsupported Maven file extension")
    return {"group_id": group, "artifact_id": artifact, "version": version,
            "resolved": resolved, "classifier": classifier, "extension": extension,
            "media_type": MEDIA_TYPES[extension]}


def metadata_path_candidates(path):
    parts = path_segments(path)
    if parts[-1] != "maven-metadata.xml":
        raise ValueError("Invalid Maven metadata file")
    candidates = []

    def add(group_parts, artifact, version):
        group = ".".join(group_parts)
        if group and len(group) <= 255 and all("." not in part for part in group_parts):
            candidates.append((group, artifact, version))

    add(parts[:-2], parts[-2], None)
    if len(parts) >= 4 and parts[-2].endswith("-SNAPSHOT") and len(parts[-2]) > 9:
        add(parts[:-3], parts[-3], parts[-2])
    if not candidates:
        raise ValueError("Invalid Maven metadata group")
    return candidates


class _BoundedXml(ET.TreeBuilder):
    def __init__(self):
        super().__init__()
        self.depth = self.nodes = 0

    def doctype(self, *args):
        raise ValueError("DTD is forbidden in Maven metadata")

    def start(self, tag, attrs):
        self.depth += 1
        self.nodes += 1
        if self.depth > 16 or self.nodes > 50000:
            raise ValueError("Maven metadata XML structure exceeds limits")
        return super().start(tag, attrs)

    def end(self, tag):
        value = super().end(tag)
        self.depth -= 1
        return value


def parse_metadata(path, raw):
    if not isinstance(raw, bytes) or not 0 < len(raw) <= MAX_BYTES:
        raise ValueError("Maven metadata length exceeds limits")
    try:
        root = ET.fromstring(raw, parser=ET.XMLParser(target=_BoundedXml()))
    except ET.ParseError as error:
        raise ValueError("Invalid Maven metadata XML") from error
    namespace = "{" + NAMESPACE + "}" if root.tag == "{" + NAMESPACE + "}metadata" else ""
    if root.tag != namespace + "metadata":
        raise ValueError("Invalid Maven metadata root")

    def children(node, allowed, repeated=()):
        result = {}
        if node.text and node.text.strip():
            raise ValueError("Unexpected Maven metadata text")
        for child in node:
            name = child.tag[len(namespace):] if child.tag.startswith(namespace) else None
            if name not in allowed or child.attrib or (name in result and name not in repeated):
                raise ValueError("Unknown, duplicate or foreign Maven metadata element")
            if child.tail and child.tail.strip():
                raise ValueError("Unexpected Maven metadata tail")
            result.setdefault(name, []).append(child)
        return result

    def leaf(items, name, required=True):
        nodes = items.get(name, [])
        if not nodes:
            if required:
                raise ValueError("Missing Maven metadata field")
            return None
        node = nodes[0]
        if len(node) or node.attrib:
            raise ValueError("Invalid Maven metadata scalar")
        return node.text or ""

    # Namespace declarations are not represented as attributes by ElementTree.
    if (any(name not in ("modelVersion", "{http://www.w3.org/2001/XMLSchema-instance}schemaLocation") for name in root.attrib) or
            root.attrib.get("modelVersion", "1.1.0") not in ("1.0.0", "1.1.0")):
        raise ValueError("Unsupported Maven metadata model")
    if any(child.tag == namespace + "plugins" for child in root):
        # The group-level layout can collide with an ordinary artifact name.
        if (not isinstance(path, str) or len(path) > 1024 or
                not path.endswith("/maven-metadata.xml")):
            raise ValueError("Invalid Maven plugin metadata path")
        parts = path[:-len("/maven-metadata.xml")].split("/")
        if (not 1 <= len(parts) <= 18 or any(not valid_segment(x) or "." in x for x in parts)):
            raise ValueError("Invalid Maven plugin group")
        group = ".".join(parts)
        if len(group) > 255:
            raise ValueError("Invalid Maven plugin group length")
        top = children(root, {"groupId", "plugins"})
        if leaf(top, "groupId", False) not in (None, group) or "plugins" not in top:
            raise ValueError("Maven plugin group identity differs from path")
        container = children(top["plugins"][0], {"plugin"}, {"plugin"})
        entries = container.get("plugin", [])
        if not 0 < len(entries) <= MAX_VERSIONS:
            raise ValueError("Invalid Maven plugin mapping count")
        prefixes, artifacts, seen = set(), set(), []
        for node in entries:
            fields = children(node, {"name", "prefix", "artifactId"})
            prefix, artifact = leaf(fields, "prefix"), leaf(fields, "artifactId")
            name = leaf(fields, "name", False) or ""
            if (not valid_segment(prefix) or not valid_segment(artifact) or
                    len(name) > 256 or any(ord(ch) < 32 for ch in name) or
                    prefix in prefixes or artifact in artifacts):
                raise ValueError("Duplicate or invalid Maven plugin mapping")
            prefixes.add(prefix)
            artifacts.add(artifact)
            seen.append({"prefix": prefix, "artifact_id": artifact})
        return {"group_id": group, "artifact_id": None, "version": None,
                "versions": [], "files": [], "plugins": seen}
    candidates = metadata_path_candidates(path)
    top = children(root, {"groupId", "artifactId", "version", "versioning"})
    identity = leaf(top, "groupId"), leaf(top, "artifactId"), leaf(top, "version", False)
    if identity not in candidates:
        raise ValueError("Maven metadata identity differs from path")
    group, artifact, base = identity
    if "versioning" not in top:
        raise ValueError("Missing Maven versioning metadata")
    versioning = top["versioning"][0]
    allowed = {"lastUpdated", "snapshot", "snapshotVersions"} if base else {"latest", "release", "versions", "lastUpdated"}
    fields = children(versioning, allowed)
    if not valid_clock(leaf(fields, "lastUpdated")):
        raise ValueError("Invalid Maven lastUpdated timestamp")
    if base is None:
        if "version" in top or "versions" not in fields:
            raise ValueError("Invalid Maven artifact-level metadata")
        listed = children(fields["versions"][0], {"version"}, {"version"})
        versions = []
        for entry in listed.get("version", []):
            value = leaf({"version": [entry]}, "version")
            if (not valid_segment(value) or
                    value.lower().endswith("-snapshot") and (not value.endswith("-SNAPSHOT") or len(value) <= 9)):
                raise ValueError("Invalid Maven version list")
            versions.append(value)
        if not 0 < len(versions) <= MAX_VERSIONS or len(set(versions)) != len(versions):
            raise ValueError("Invalid or duplicated Maven versions")
        latest, release = leaf(fields, "latest", False), leaf(fields, "release", False)
        if ((latest is not None and latest not in versions) or
                (release is not None and (release not in versions or release.lower().endswith("-snapshot")))):
            raise ValueError("Maven latest/release pointer differs from inventory")
        return {"group_id": group, "artifact_id": artifact, "version": None,
                "versions": versions, "files": []}
    if leaf(top, "version") != base or "snapshot" not in fields or "snapshotVersions" not in fields:
        raise ValueError("Incomplete Maven SNAPSHOT metadata")
    snapshot = children(fields["snapshot"][0], {"timestamp", "buildNumber", "localCopy"})
    stamp, build = leaf(snapshot, "timestamp"), leaf(snapshot, "buildNumber")
    resolved = base[:-9] + "-" + stamp + "-" + build
    identity = snapshot_value(base, resolved)
    if identity is None or leaf(snapshot, "localCopy", False) not in (None, "false", "0"):
        raise ValueError("Invalid Maven snapshot timestamp/build")
    records = children(fields["snapshotVersions"][0], {"snapshotVersion"}, {"snapshotVersion"})
    entries = records.get("snapshotVersion", [])
    if not 0 < len(entries) <= MAX_VERSIONS:
        raise ValueError("Invalid Maven SNAPSHOT file inventory")
    files, seen, current_pom = [], set(), False
    for entry in entries:
        values = children(entry, {"extension", "classifier", "value", "updated"})
        extension, classifier = leaf(values, "extension"), leaf(values, "classifier", False) or None
        value, updated = leaf(values, "value"), leaf(values, "updated")
        current = snapshot_value(base, value)
        if (extension not in MEDIA_TYPES or (classifier is not None and not valid_segment(classifier)) or
                current is None or current > identity or not valid_clock(updated) or
                (extension, classifier) in seen):
            raise ValueError("Invalid Maven snapshot sub-artifact")
        seen.add((extension, classifier))
        filename = artifact + "-" + value + ("-" + classifier if classifier else "") + "." + extension
        files.append(group.replace(".", "/") + "/" + artifact + "/" + base + "/" + filename)
        parse_asset_path(files[-1])  # Includes the runtime's whole filename bound.
        current_pom |= extension == "pom" and classifier is None and value == resolved
    if not current_pom:
        raise ValueError("Maven SNAPSHOT metadata requires its primary POM")
    return {"group_id": group, "artifact_id": artifact, "version": base,
            "versions": [], "files": files}


def metadata_record(record):
    if (not isinstance(record, dict) or set(record) != set(METADATA_COLUMNS) or
            not valid_scope(record.get("scope")) or type(record.get("size")) is not int or
            not 0 < record["size"] <= MAX_BYTES or not valid_date(record.get("updated_at")) or
            not isinstance(record.get("sha256"), str) or re.fullmatch(r"[0-9a-f]{64}", record["sha256"]) is None or
            not isinstance(record.get("content_base64"), str) or len(record["content_base64"]) > ((MAX_BYTES + 2) // 3) * 4):
        raise ValueError("Invalid Maven metadata ledger record")
    raw = base64.b64decode(record["content_base64"], validate=True)
    if (len(raw) != record["size"] or base64.b64encode(raw).decode("ascii") != record["content_base64"] or
            hashlib.sha256(raw).hexdigest() != record["sha256"]):
        raise ValueError("Maven metadata content differs from its digest ledger")
    return parse_metadata(record["path"], raw)


def validate_inventory(metadata_rows, global_assets, feed_assets, verify_blob, feeds):
    """Validate raw metadata, its same-scope graph and all indexed Maven bytes."""
    if any(not isinstance(rows, list) for rows in (metadata_rows, global_assets, feed_assets, feeds)):
        raise ValueError("Invalid Maven snapshot inventory")
    scopes = {}
    for feed in feeds:
        if (not isinstance(feed, dict) or set(feed) != set(FEED_COLUMNS) or
                not feed.get("id") or not valid_scope(feed["id"]) or feed["id"] in scopes or
                any(not isinstance(feed[field], str) for field in ("kind", "mode", "visibility"))):
            raise ValueError("Invalid Maven snapshot feed inventory")
        scopes[feed["id"]] = feed
    assets = {}
    for row in global_assets:
        if not isinstance(row, dict) or set(row) != set(ASSET_COLUMNS):
            raise ValueError("Invalid global Maven asset ledger")
        assets[("", row["path"])] = row
    if len(assets) != len(global_assets):
        raise ValueError("Duplicate global Maven asset identity")
    for row in feed_assets:
        if row.get("kind") != "maven":
            continue
        coordinate = parse_asset_path(row["path"])
        if row.get("name") != coordinate["group_id"] + ":" + coordinate["artifact_id"]:
            raise ValueError("Private Maven identity differs from path")
        copy = {key: row[key] for key in ("path", "version", "media_type", "sha256", "size", "created_at")}
        copy.update(group_id=coordinate["group_id"], artifact_id=coordinate["artifact_id"])
        identity = row["feed_id"], row["path"]
        if not valid_scope(row["feed_id"]) or not row["feed_id"] or identity in assets:
            raise ValueError("Invalid private Maven asset scope")
        assets[identity] = copy
    for (scope, path), row in assets.items():
        coordinate = parse_asset_path(path)
        if (any(row[field] != coordinate[field] for field in ("group_id", "artifact_id", "version", "media_type")) or
                type(row["size"]) is not int or row["size"] <= 0 or not valid_date(row["created_at"]) or
                not isinstance(row["sha256"], str) or re.fullmatch(r"[0-9a-f]{64}", row["sha256"]) is None):
            raise ValueError("Invalid immutable Maven asset ledger")
        verify_blob(scope, path, row["sha256"], row["size"])
    metadata = {}
    for row in metadata_rows:
        parsed = metadata_record(row)
        identity = row["scope"], row["path"]
        if identity in metadata:
            raise ValueError("Duplicate Maven metadata identity")
        if row["scope"]:
            feed = scopes.get(row["scope"], {})
            if any(feed.get(field) != value for field, value in
                   (("kind", "maven"), ("mode", "hosted"), ("visibility", "private"))):
                raise ValueError("Maven metadata refers to a missing or incompatible feed")
        metadata[identity] = parsed
    published_poms = {(scope, row["group_id"], row["artifact_id"]) for
                      (scope, path), row in assets.items()
                      if path.endswith(".pom") and row["media_type"] == "application/xml"
                      and not row["version"].endswith("-SNAPSHOT")}
    inventory = {(scope, row["group_id"], row["artifact_id"]) for
                 (scope, path), row in assets.items()}
    for (scope, path), parsed in metadata.items():
        if "plugins" in parsed:
            parts = path[:-len("/maven-metadata.xml")].split("/")
            if len(parts) >= 2 and (scope, ".".join(parts[:-1]), parts[-1]) in inventory:
                raise ValueError("Maven plugin and artifact metadata paths collide")
            for plugin in parsed["plugins"]:
                if (scope, parsed["group_id"], plugin["artifact_id"]) not in published_poms:
                    raise ValueError("Maven plugin mapping lacks a same-scope release POM")
            continue
        root = parsed["group_id"].replace(".", "/") + "/" + parsed["artifact_id"]
        references = set(parsed["files"])
        for path in parsed["files"]:
            if (scope, path) not in assets:
                raise ValueError("Maven metadata references a missing or different-scope artifact")
        for version in parsed["versions"]:
            if version.endswith("-SNAPSHOT"):
                target = metadata.get((scope, root + "/" + version + "/maven-metadata.xml"))
                if (target is None or target["group_id"] != parsed["group_id"] or
                        target["artifact_id"] != parsed["artifact_id"] or target["version"] != version):
                    raise ValueError("Maven version list references missing or different-coordinate snapshot metadata")
                references.update(target["files"])
            elif (scope, root + "/" + version + "/" + parsed["artifact_id"] + "-" + version + ".pom") not in assets:
                raise ValueError("Maven version list references a missing or different-scope POM")
            else:
                references.add(root + "/" + version + "/" + parsed["artifact_id"] + "-" + version + ".pom")
            if len(references) > MAX_VERSIONS:
                raise ValueError("Maven metadata aggregate references exceed limits")
