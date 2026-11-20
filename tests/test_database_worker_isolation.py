"""Enforce the actual isolated database-worker Compose security boundary."""
import copy
import json
import os
from pathlib import Path
import subprocess
import unittest

ROOT = Path(__file__).resolve().parents[1]
OVERLAY = ROOT / "compose.database-connectors.yaml"


def compose():
    env = os.environ.copy()
    env.update({
        "PACKORY_DB_CONNECTOR_ALLOWED_TARGETS": "postgresql://db.example.invalid:5432",
        "PACKORY_DB_WORKER_IPC_KEY_PATH": "/tmp/synthetic-db-worker-key",
        "PACKORY_MCP_CONNECTOR_KEYRING_PATH": "/tmp/synthetic-mcp-keyring",
    })
    completed = subprocess.run([
        "docker", "compose", "-f", str(ROOT / "compose.yaml"),
        "-f", str(OVERLAY), "config", "--format", "json"
    ], cwd=ROOT, env=env, text=True, capture_output=True, check=True)
    return json.loads(completed.stdout)


def require_isolation(conf):
    services = conf["services"]
    app = services["packory"]
    worker = services["database-connector-worker"]
    assert not worker.get("privileged", False), "Privileged worker"
    assert worker["read_only"] is True
    assert worker["user"] == "1654:1654"
    assert worker["cap_drop"] == ["ALL"]
    assert "no-new-privileges:true" in worker["security_opt"]
    assert worker["pids_limit"] <= 64
    assert worker["mem_limit"] <= 805306368
    assert worker["cpus"] <= 1.0
    assert worker.get("ports") in (None, [])
    assert worker.get("network_mode") in (None, "")
    networks = worker["networks"]
    assert set(networks) == {"database-worker-egress"}
    assert conf["networks"]["database-worker-egress"]["internal"] is True
    assert worker["build"]["target"] == "database-worker"

    destinations = {v["target"]: v for v in worker["volumes"]}
    assert set(destinations) == {
        "/run/packory-db-ipc", "/run/secrets/packory-db-worker-key"
    }
    assert destinations["/run/secrets/packory-db-worker-key"]["read_only"] is True
    assert destinations["/run/packory-db-ipc"]["type"] == "volume"
    assert "packory-data" not in str(worker["volumes"])
    assert "mcp-connector-keyring" not in str(worker["volumes"])
    for name in ("PACKORY_ADMIN_TOKEN", "PACKORY_ADMIN_TOKEN_FILE",
                 "PACKORY_S3_ACCESS_KEY", "PACKORY_S3_SECRET_KEY",
                 "PACKORY_OIDC_CLIENT_SECRET",
                 "PACKORY_MCP_CONNECTOR_KEYRING_FILE"):
        assert name not in worker["environment"], "Worker received application secrets"

    app_volumes = {v["target"]: v for v in app["volumes"]}
    assert app_volumes["/run/packory-db-ipc"]["source"] == \
        destinations["/run/packory-db-ipc"]["source"]
    assert app["environment"]["PACKORY_DB_WORKER_SOCKET"] == \
        worker["environment"]["PACKORY_DB_WORKER_SOCKET"]
    assert app["environment"]["PACKORY_DB_WORKER_IPC_KEY_FILE"] == \
        worker["environment"]["PACKORY_DB_WORKER_IPC_KEY_FILE"]


class DatabaseWorkerIsolationComposeTests(unittest.TestCase):
    def test_real_compose_profile_is_sandboxed(self):
        require_isolation(compose())

    def test_privilege_or_network_changes_break_the_contract(self):
        original = compose()
        changes = (
            ("privileged", True),
            ("read_only", False),
            ("user", "root"),
            ("cap_drop", []),
            ("security_opt", []),
            ("pids_limit", 2048),
            ("cpus", 8),
            ("ports", [{"target": 1234, "published": 1234}]),
            ("networks", {"default": {}}),
        )
        for key, value in changes:
            config = copy.deepcopy(original)
            config["services"]["database-connector-worker"][key] = value
            with self.subTest(key=key):
                with self.assertRaises(AssertionError):
                    require_isolation(config)

    def test_packory_data_or_keyring_mount_is_rejected(self):
        for path in ("/data", "/run/secrets/mcp-connector-keyring.json"):
            config = compose()
            config["services"]["database-connector-worker"]["volumes"].append({
                "type": "volume", "source": "packory-data", "target": path
            })
            with self.subTest(path=path), self.assertRaises(AssertionError):
                require_isolation(config)

    def test_noninternal_network_is_rejected(self):
        conf = compose()
        conf["networks"]["database-worker-egress"]["internal"] = False
        with self.assertRaises(AssertionError):
            require_isolation(conf)


if __name__ == "__main__":
    unittest.main()
