#!/usr/bin/env python3
"""Read and hash both public GHCR images without Docker or account credentials.

This verifies registry delivery, exact revision labels and the declared non-root
identity. It does not execute images, verify signatures, or qualify production.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import sys
import time
import urllib.error
import urllib.parse
import urllib.request

IMAGES = ('cenn0/packory', 'cenn0/packory-skillspector')
MANIFEST_TYPES = {'application/vnd.docker.distribution.manifest.v2+json',
                  'application/vnd.oci.image.manifest.v1+json'}
CONFIG_TYPES = {'application/vnd.docker.container.image.v1+json',
                'application/vnd.oci.image.config.v1+json'}
LAYER_TYPES = {'application/vnd.docker.image.rootfs.diff.tar.gzip',
               'application/vnd.oci.image.layer.v1.tar+gzip',
               'application/vnd.oci.image.layer.v1.tar+zstd',
               'application/vnd.oci.image.layer.v1.tar'}
MAX_JSON = 4 * 1024 * 1024
MAX_LAYER = 1024 * 1024 * 1024
MAX_IMAGE = 4 * MAX_LAYER


class Blocked(ValueError):
    pass


class RegistryRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, req, fp, code, msg, headers, newurl):
        target = urllib.parse.urlsplit(newurl)
        if (target.scheme != 'https' or target.hostname != 'pkg-containers.githubusercontent.com'
                or target.port not in {None, 443} or target.username or target.password or target.fragment
                or '/blobs/' not in urllib.parse.urlsplit(req.full_url).path):
            raise Blocked('unexpected_registry_redirect')
        # Create a new request: the anonymous registry bearer must not follow to the CDN.
        return urllib.request.Request(newurl, headers={'Accept-Encoding': 'identity'})


def parse_json(raw):
    def unique(pairs):
        result = {}
        for key, value in pairs:
            if key in result:
                raise Blocked('duplicate_json_property')
            result[key] = value
        return result
    try:
        return json.loads(raw, object_pairs_hook=unique)
    except (ValueError, UnicodeError, RecursionError) as error:
        raise Blocked('invalid_registry_json') from error


def digest(raw):
    return 'sha256:' + hashlib.sha256(raw).hexdigest()


def descriptor(value, allowed, maximum):
    if (type(value) is not dict or value.get('mediaType') not in allowed or
            type(value.get('size')) is not int or not 0 < value['size'] <= maximum or
            type(value.get('digest')) is not str or
            not re.fullmatch(r'sha256:[0-9a-f]{64}', value['digest']) or
            any(key in value for key in ('urls', 'data'))):
        raise Blocked('invalid_image_descriptor')
    return value['digest'], value['size']


def read_stream(response, maximum, expected_digest=None, expected_size=None, retain=False):
    total, sha, parts = 0, hashlib.sha256(), []
    deadline = time.monotonic() + 300
    while True:
        chunk = response.read(min(1024 * 1024, maximum - total + 1))
        if not chunk:
            break
        total += len(chunk)
        if total > maximum or time.monotonic() > deadline:
            raise Blocked('registry_content_budget')
        sha.update(chunk)
        if retain:
            parts.append(chunk)
    if ((expected_size is not None and total != expected_size) or
            (expected_digest is not None and 'sha256:' + sha.hexdigest() != expected_digest)):
        raise Blocked('registry_content_mismatch')
    return b''.join(parts) if retain else total


def anonymous_reader(image):
    if image not in IMAGES:
        raise Blocked('unexpected_image')
    opener = urllib.request.build_opener(RegistryRedirect)
    token_url = 'https://ghcr.io/token?' + urllib.parse.urlencode({
        'service': 'ghcr.io', 'scope': 'repository:' + image + ':pull'})
    with opener.open(urllib.request.Request(token_url, headers={'Accept-Encoding': 'identity'}), timeout=30) as response:
        token = parse_json(read_stream(response, MAX_JSON, retain=True)).get('token')
    if type(token) is not str or not 0 < len(token) <= 16384 or any(ord(c) < 32 for c in token):
        raise Blocked('anonymous_pull_not_authorized')

    def read(path, expected_digest=None, expected_size=None, retain=False):
        if (not re.fullmatch(r'/(?:manifests/(?:sha-[0-9a-f]{40}|sha256:[0-9a-f]{64})|blobs/sha256:[0-9a-f]{64})', path)):
            raise Blocked('unexpected_registry_path')
        request = urllib.request.Request('https://ghcr.io/v2/' + image + path, headers={
            'Authorization': 'Bearer ' + token, 'Accept': ', '.join(sorted(MANIFEST_TYPES)),
            'Accept-Encoding': 'identity'})
        with opener.open(request, timeout=30) as response:
            if response.status != 200:
                raise Blocked('unsuccessful_registry_response')
            raw = read_stream(response, MAX_JSON if retain else MAX_LAYER,
                              expected_digest, expected_size, retain)
            if path.startswith('/manifests/'):
                media_type = response.headers.get('Content-Type', '').split(';')[0]
                declared = response.headers.get('Docker-Content-Digest')
                if (media_type not in MANIFEST_TYPES or parse_json(raw).get('mediaType') != media_type or
                        (declared and declared != digest(raw))):
                    raise Blocked('manifest_header_mismatch')
            return raw
    return read


def verify(sha, reader_factory=anonymous_reader):
    if not re.fullmatch(r'[0-9a-f]{40}', sha):
        raise Blocked('exact_source_sha_required')
    result = []
    for image in IMAGES:
        read = reader_factory(image)
        manifest_bytes = read('/manifests/sha-' + sha, retain=True)
        manifest = parse_json(manifest_bytes)
        if (type(manifest) is not dict or manifest.get('schemaVersion') != 2 or
                manifest.get('mediaType') not in MANIFEST_TYPES or
                type(manifest.get('layers')) is not list or not 1 <= len(manifest['layers']) <= 128):
            raise Blocked('unsupported_image_manifest')
        config_digest, config_size = descriptor(manifest.get('config'), CONFIG_TYPES, MAX_JSON)
        config = parse_json(read('/blobs/' + config_digest, config_digest, config_size, retain=True))
        if (type(config) is not dict or config.get('os') != 'linux' or config.get('architecture') != 'amd64' or
                type(config.get('config')) is not dict):
            raise Blocked('unsupported_image_platform')
        labels, user = config['config'].get('Labels'), config['config'].get('User')
        if (type(labels) is not dict or labels.get('org.opencontainers.image.revision') != sha or
                labels.get('org.opencontainers.image.source') != 'https://github.com/Cenn0/Packory' or
                user not in ({'1654', '1654:1654'} if image == IMAGES[0] else {'1654:1654'})):
            raise Blocked('image_identity_mismatch')
        layers = [descriptor(layer, LAYER_TYPES, MAX_LAYER) for layer in manifest['layers']]
        total = sum(size for _, size in layers)
        if total > MAX_IMAGE:
            raise Blocked('image_size_budget')
        # Independently download every manifest reference, even when Docker's
        # local build/pull cache already contains the same layer.
        for layer_digest, size in layers:
            read('/blobs/' + layer_digest, layer_digest, size)
        # Read by digest too, so the recorded reference is independently retrievable.
        manifest_digest = digest(manifest_bytes)
        if read('/manifests/' + manifest_digest, retain=True) != manifest_bytes:
            raise Blocked('manifest_digest_readback_mismatch')
        result.append({'image': 'ghcr.io/' + image, 'manifestDigest': manifest_digest,
                       'sourceSha': sha, 'platform': 'linux/amd64', 'user': user,
                       'layers': len(layers), 'verifiedLayerBytes': total})
    return {'schema': 'packory.container-delivery/1', 'sourceSha': sha,
            'qualification': 'anonymous-registry-byte-delivery', 'productionRecommended': False,
            'images': result}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--sha', required=True)
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    try:
        if args.output.exists() or args.output.is_symlink():
            raise Blocked('output_must_be_new')
        report = verify(args.sha)
        with args.output.open('x', encoding='utf-8') as output:
            output.write(json.dumps(report, indent=2) + '\n')
    except (Blocked, OSError, ValueError, TypeError, KeyError, RecursionError):
        # Never retain signed redirect URLs, bearer tokens or registry error bodies.
        print('BLOCKED: anonymous container delivery verification failed', file=sys.stderr)
        return 1
    print('Both exact-revision image manifests, configs and all layer bytes verified anonymously')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
