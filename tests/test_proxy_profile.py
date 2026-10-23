"""Static least-privilege regression checks for the opt-in proxy example.

This does not qualify real TLS, native clients, authentication or reboots.
"""
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
COMPOSE = (ROOT / "compose.proxy.yaml").read_text(encoding="utf-8")
NGINX = (ROOT / "deploy/nginx/packory.conf.example").read_text(encoding="utf-8")
GUIDE = (ROOT / "docs/reverse-proxy.md").read_text(encoding="utf-8")


class ProxyProfileContractTests(unittest.TestCase):
    def test_only_app_attaches_to_operator_selected_ingress(self):
        app = COMPOSE.split("\n  packory:", 1)[1].split("\n  skillspector:", 1)[0]
        worker = COMPOSE.split("\n  skillspector:", 1)[1].split("\nsecrets:", 1)[0]
        self.assertIn("external: true", COMPOSE)
        self.assertIn("PACKORY_INGRESS_NETWORK:?", COMPOSE)
        self.assertIn("networks:\n      - ingress", app)
        self.assertIn('expose:\n      - "8080"', app)
        self.assertNotRegex(app, r"(?m)^\s+ports:")
        self.assertIn("network_mode: none", worker)
        self.assertIn("read_only: true", worker)
        self.assertIn('user: "1654:1654"', worker)
        self.assertIn("- ALL", app)
        self.assertIn("- ALL", worker)

    def test_mutation_and_read_access_are_private_and_secrets_are_not_env_values(self):
        app = COMPOSE.split("\n  packory:", 1)[1].split("\n  skillspector:", 1)[0]
        self.assertIn('PACKORY_ANONYMOUS_READ: "false"', app)
        self.assertIn('PACKORY_OCI_AUTHENTICATED_PING: "true"', app)
        self.assertIn("PACKORY_PUBLIC_BASE_URL: ${PACKORY_PUBLIC_BASE_URL:?", app)
        for name in ("ADMIN_TOKEN", "READ_TOKEN", "WRITE_TOKEN"):
            self.assertIn(f"PACKORY_{name}_FILE: /run/secrets/", app)
            self.assertNotRegex(app, rf"(?m)^\s+PACKORY_{name}:")
        self.assertIn("@sha256", GUIDE)
        self.assertIn("PACKORY_APP_IMAGE:?", COMPOSE)
        self.assertIn("PACKORY_SKILLSPECTOR_IMAGE:?", COMPOSE)

    def test_streaming_auth_and_untrusted_identity_headers_have_explicit_contract(self):
        self.assertIn("proxy_set_header Authorization $http_authorization;", NGINX)
        self.assertIn("proxy_buffering off;", NGINX)
        self.assertIn("proxy_request_buffering off;", NGINX)
        self.assertIn("proxy_set_header X-Forwarded-For $remote_addr;", NGINX)
        self.assertIn("proxy_set_header X-Forwarded-User \"\";", NGINX)
        self.assertIn("access_log off;", NGINX)
        for term in ("/nuget/", "/npm/", "/v2/", "/mcp", "Nginx Proxy Manager",
                     "WWW-Authenticate", "not officially"):
            if term == "not officially":
                self.assertIn("not\nofficially qualified", GUIDE)
            else:
                self.assertIn(term, GUIDE)


if __name__ == "__main__":
    unittest.main()
