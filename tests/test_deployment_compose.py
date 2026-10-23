"""Offline negative tests for parsed Compose structural contracts."""
import copy
import importlib.util
from pathlib import Path
import unittest

spec = importlib.util.spec_from_file_location(
    "profile_check", Path(__file__).resolve().parents[1] /
    "scripts/check_deployment_compose.py")
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)


class ComposeContractTests(unittest.TestCase):
    def config(self, profile):
        env = {"PACKORY_ANONYMOUS_READ": "false"}
        secrets = {}
        for kind in ("admin", "read", "write"):
            name = "packory_" + kind + "_token"
            env["PACKORY_" + kind.upper() + "_TOKEN_FILE"] = "/run/secrets/" + name
            secrets[name] = {"file": "/synthetic/fixture/" + name}
        app = {"environment": env,
               "cap_drop": ["ALL"],
               "security_opt": ["no-new-privileges:true"]}
        worker = {"read_only": True, "user": "1654:1654",
                  "network_mode": "none", "pids_limit": 64, "mem_limit": 536870912,
                  "cap_drop": ["ALL"], "security_opt": ["no-new-privileges:true"]}
        conf = {"services": {"packory": app, "skillspector": worker},
                "secrets": secrets}
        if profile == "loopback":
            app["ports"] = [{"target": 8080, "published": "8080", "host_ip": "127.0.0.1"}]
        else:
            app["environment"]["PACKORY_PUBLIC_BASE_URL"] = "https://packory.example.invalid"
            app["environment"]["PACKORY_OCI_AUTHENTICATED_PING"] = "true"
            app["image"] = "ghcr.io/cenn0/packory@sha256:" + "a" * 64
            worker["image"] = "ghcr.io/cenn0/packory-skillspector@sha256:" + "b" * 64
            app["networks"] = {"ingress": {}}
            conf["networks"] = {"ingress": {"external": True}}
        return conf

    def test_synthetic_safe_profiles(self):
        for name in ("loopback", "private-ingress"):
            mod.check(name, self.config(name))

    def test_direct_app_credentials_rejected(self):
        config = self.config("loopback")
        config["services"]["packory"]["environment"]["PACKORY_ADMIN_TOKEN"] = "placeholder"
        with self.assertRaises(ValueError):
            mod.check("loopback", config)

    def test_worker_network_privilege_and_mount_invariants(self):
        for override in ({"network_mode": "bridge"}, {"read_only": False},
                         {"cap_drop": []}, {"privileged": True}, {"ports": [{"target": 80}]}):
            config = self.config("private-ingress")
            config["services"]["skillspector"].update(override)
            with self.subTest(override=tuple(override)):
                with self.assertRaises(ValueError):
                    mod.check("private-ingress", config)

    def test_private_ingress_must_not_leak_port(self):
        config = self.config("private-ingress")
        config["services"]["packory"]["ports"] = [
            {"host_ip": "0.0.0.0", "published": 8080, "target": 8080}
        ]
        with self.assertRaises(ValueError):
            mod.check("private-ingress", config)

    def test_http_origin_and_nonexternal_ingress_rejected(self):
        for change in ("public_base", "external"):
            config = self.config("private-ingress")
            if change == "public_base":
                config["services"]["packory"]["environment"]["PACKORY_PUBLIC_BASE_URL"] = "http://example.invalid"
            else:
                config["networks"]["ingress"]["external"] = False
            with self.assertRaises(ValueError):
                mod.check("private-ingress", config)

    def test_loopback_cannot_bind_on_all_interfaces(self):
        config = self.config("loopback")
        config["services"]["packory"]["ports"][0]["host_ip"] = "0.0.0.0"
        with self.assertRaises(ValueError):
            mod.check("loopback", config)

    def test_private_images_must_be_full_matching_sha256_references(self):
        for service, alternatives in (
            ("packory", [
                "ghcr.io/cenn0/packory:main",
                "ghcr.io/cenn0/packory@sha256:" + "a" * 63,
                "docker.io/cenn0/packory@sha256:" + "a" * 64,
                "ghcr.io/cenn0/other@sha256:" + "a" * 64,
            ]),
            ("skillspector", [
                "ghcr.io/cenn0/packory-skillspector:latest",
                "ghcr.io/cenn0/packory-skillspector@sha256:" + "Z" * 64,
                "ghcr.io/cenn0/packory@sha256:" + "b" * 64,
            ]),
        ):
            for image in alternatives:
                config = self.config("private-ingress")
                config["services"][service]["image"] = image
                with self.subTest(service=service, image=image):
                    with self.assertRaises(ValueError):
                        mod.check("private-ingress", config)

    def test_canonical_origin_rejects_credentials_paths_ports_and_fragments(self):
        for origin in (
            "https://user:password@packory.example.invalid",
            "https://packory.example.invalid/",
            "https://packory.example.invalid/path",
            "https://packory.example.invalid?debug=1",
            "https://packory.example.invalid#token",
            "https://packory.example.invalid:",
            "https://packory.example.invalid:65536",
            "https://packory..example.invalid",
            "https://localhost",
            "https://127.0.0.1",
            "https://[::1]",
            "https://packory.example.invalid\\@other.invalid",
            "HTTPS://packory.example.invalid",
            " https://packory.example.invalid",
        ):
            config = self.config("private-ingress")
            config["services"]["packory"]["environment"]["PACKORY_PUBLIC_BASE_URL"] = origin
            with self.subTest(origin=origin):
                with self.assertRaises(ValueError):
                    mod.check("private-ingress", config)

    def test_canonical_origin_accepts_explicit_https_port_and_ipv6(self):
        for origin in ("https://packory.example.invalid:8443", "https://[2001:db8::1]:8443"):
            config = self.config("private-ingress")
            config["services"]["packory"]["environment"]["PACKORY_PUBLIC_BASE_URL"] = origin
            with self.subTest(origin=origin):
                mod.check("private-ingress", config)

    def test_worker_secrets_or_root_override_rejected(self):
        for service, override in (
            ("skillspector", {"environment": {"PACKORY_ADMIN_TOKEN": "fixture"}}),
            ("skillspector", {"secrets": ["packory_admin_token"]}),
            ("packory", {"user": "root"}),
            ("packory", {"user": "0:0"}),
        ):
            config = self.config("private-ingress")
            config["services"][service].update(override)
            with self.subTest(service=service, override=override):
                with self.assertRaises(ValueError):
                    mod.check("private-ingress", config)


if __name__ == "__main__":
    unittest.main()
