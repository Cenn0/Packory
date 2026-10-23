"""Container byte-delivery contracts; synthetic responses are not real registry evidence."""
import copy
import importlib.util
import io
import json
from pathlib import Path
import unittest
import urllib.request

spec = importlib.util.spec_from_file_location('container_delivery', Path(__file__).resolve().parents[1] / 'scripts/verify_container_delivery.py')
m = importlib.util.module_from_spec(spec)
spec.loader.exec_module(m)
SHA = 'a' * 40


def encode(value):
    return json.dumps(value).encode()


def fixture(config_change=None, manifest_change=None):
    responses = {}
    layers = [b'first-compressed-layer', b'second-compressed-layer']
    for image in m.IMAGES:
        config = {'os': 'linux', 'architecture': 'amd64', 'config': {
            'User': '1654:1654', 'Labels': {'org.opencontainers.image.revision': SHA,
            'org.opencontainers.image.source': 'https://github.com/Cenn0/Packory'}}}
        if config_change:
            config_change(config)
        raw = encode(config)
        manifest = {'schemaVersion': 2, 'mediaType': sorted(m.MANIFEST_TYPES)[0],
                    'config': {'mediaType': sorted(m.CONFIG_TYPES)[0], 'size': len(raw), 'digest': m.digest(raw)},
                    'layers': [{'mediaType': sorted(m.LAYER_TYPES)[0], 'size': len(layer), 'digest': m.digest(layer)} for layer in layers]}
        if manifest_change:
            manifest_change(manifest)
        doc = encode(manifest)
        responses[image] = {'/manifests/sha-' + SHA: doc, '/manifests/' + m.digest(doc): doc,
                            '/blobs/' + m.digest(raw): raw,
                            **{'/blobs/' + m.digest(layer): layer for layer in layers}}
    return responses


def reader(responses, calls):
    def factory(image):
        def read(path, expected_digest=None, expected_size=None, retain=False):
            calls.append((image, path))
            return m.read_stream(io.BytesIO(responses[image][path]), m.MAX_JSON if retain else m.MAX_LAYER,
                                 expected_digest, expected_size, retain)
        return read
    return factory


class DeliveryTests(unittest.TestCase):
    def test_downloads_every_layer_and_both_manifests_by_digest(self):
        calls = []
        result = m.verify(SHA, reader(fixture(), calls))
        self.assertFalse(result['productionRecommended'])
        self.assertEqual(result['qualification'], 'anonymous-registry-byte-delivery')
        self.assertEqual(2, len(result['images']))
        for image in result['images']:
            self.assertEqual(2, image['layers'])
            self.assertEqual(45, image['verifiedLayerBytes'])
            self.assertIn((image['image'][8:], '/manifests/' + image['manifestDigest']), calls)
        self.assertEqual(10, len(calls))

    def test_wrong_source_root_and_wrong_platform_are_rejected(self):
        for change in (lambda x: x['config'].update(User='0'),
                       lambda x: x['config'].update(User='root'),
                       lambda x: x.update(architecture='arm64'),
                       lambda x: x['config']['Labels'].update({'org.opencontainers.image.revision': 'b' * 40}),
                       lambda x: x['config']['Labels'].update({'org.opencontainers.image.source': 'https://example.invalid'})):
            with self.subTest(change=change), self.assertRaises(m.Blocked):
                m.verify(SHA, reader(fixture(config_change=change), []))

    def test_descriptor_origin_size_digest_and_manifest_limits(self):
        for change in (lambda x: x['layers'][0].update(urls=['https://example.invalid/layer']),
                       lambda x: x['layers'][0].update(size=True),
                       lambda x: x['layers'][0].update(size=m.MAX_LAYER + 1),
                       lambda x: x['layers'][0].update(digest='sha256:invalid'),
                       lambda x: x.update(layers=[]),
                       lambda x: x.update(mediaType='application/vnd.oci.image.index.v1+json')):
            with self.subTest(change=change), self.assertRaises(m.Blocked):
                m.verify(SHA, reader(fixture(manifest_change=change), []))

    def test_corrupt_missing_and_truncated_content_cannot_pass(self):
        for mode in ('config', 'layer', 'truncated', 'missing', 'digest-manifest'):
            responses = fixture()
            paths = responses[m.IMAGES[0]]
            if mode == 'digest-manifest':
                key = next(p for p in paths if p.startswith('/manifests/sha256:'))
                paths[key] = b'{}'
            else:
                blobs = [p for p in paths if p.startswith('/blobs/')]
                key = blobs[0 if mode == 'config' else 1]
                if mode == 'missing':
                    del paths[key]
                else:
                    paths[key] = paths[key][:-1] if mode == 'truncated' else b'tampered'
            with self.subTest(mode=mode), self.assertRaises((m.Blocked, KeyError)):
                m.verify(SHA, reader(responses, []))

    def test_streams_are_bounded_and_json_has_no_duplicate_properties(self):
        with self.assertRaises(m.Blocked):
            m.read_stream(io.BytesIO(b'12345'), 4)
        with self.assertRaises(m.Blocked):
            m.parse_json(b'{"size":1,"size":2}')
        with self.assertRaises(m.Blocked):
            m.verify('main', reader(fixture(), []))
        with self.assertRaises(m.Blocked):
            m.anonymous_reader('unexpected/repository')

    def test_cdn_redirect_strips_registry_bearer_and_rejects_other_origins(self):
        handler = m.RegistryRedirect()
        req = urllib.request.Request('https://ghcr.io/v2/cenn0/packory/blobs/sha256:' + 'b' * 64,
                                     headers={'Authorization': 'Bearer synthetic-token'})
        result = handler.redirect_request(req, None, 307, '', {}, 'https://pkg-containers.githubusercontent.com/blob?synthetic=1')
        self.assertFalse(result.has_header('Authorization'))
        for url in ('http://pkg-containers.githubusercontent.com/blob', 'https://example.invalid/blob',
                    'https://pkg-containers.githubusercontent.com:444/blob',
                    'https://synthetic@pkg-containers.githubusercontent.com/blob'):
            with self.subTest(url=url), self.assertRaises(m.Blocked):
                handler.redirect_request(req, None, 307, '', {}, url)


if __name__ == '__main__':
    unittest.main()
