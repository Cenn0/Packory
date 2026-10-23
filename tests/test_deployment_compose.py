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


if __name__ == "__main__":
    unittest.main()
