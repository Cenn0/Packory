"""Deterministic tests for offline SQLite/filesystem backup and restore."""
import importlib.util
import json
from pathlib import Path
import sqlite3
import tempfile
import unittest

spec = importlib.util.spec_from_file_location(
    "offline_backup", Path(__file__).resolve().parents[1] / "scripts" / "offline_backup.py"
)
mod = importlib.util.module_from_spec(spec)
spec.loader.exec_module(mod)

class OfflineBackupTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.data = self.root / "data"
        (self.data / "blobs" / "skills").mkdir(parents=True)
        with sqlite3.connect(self.data / "packory.sqlite") as con:
            con.execute("CREATE TABLE packages (id INTEGER PRIMARY KEY)")
            con.execute("INSERT INTO packages(id) VALUES(1)")
        (self.data / "blobs" / "skills" / "test.zip").write_bytes(b"test-immutable-payload")

    def test_create_verify_restore(self):
        backup = self.root / "backup"
        restored = self.root / "restored"
        mod.create(self.data, backup)
        manifest = mod.verify(backup)
        self.assertEqual(2, len(manifest["entries"]))
        mod.restore(backup, restored)
        self.assertEqual((self.data / "blobs/skills/test.zip").read_bytes(),
                         (restored / "blobs/skills/test.zip").read_bytes())
        with sqlite3.connect(restored / "packory.sqlite") as db:
            self.assertEqual((1,), db.execute("SELECT id FROM packages").fetchone())
        with self.assertRaises(ValueError):
            mod.restore(backup, restored)

    def test_corruption_is_fail_closed(self):
        backup = self.root / "backup"
        mod.create(self.data, backup)
        (backup / "blobs" / "skills" / "test.zip").write_bytes(b"changed")
        with self.assertRaises(ValueError):
            mod.verify(backup)
        self.assertFalse((self.root / "restored").exists())

    def test_manifest_path_traversal_rejected(self):
        backup = self.root / "backup"
        mod.create(self.data, backup)
        manifest_path = backup / "manifest.json"
        manifest = json.loads(manifest_path.read_text())
        manifest["entries"][0]["path"] = "../escape"
        manifest_path.write_text(json.dumps(manifest))
        with self.assertRaises(ValueError):
            mod.verify(backup)

    def test_active_wal_or_symlink_rejected(self):
        (self.data / "packory.sqlite-wal").write_bytes(b"still active")
        with self.assertRaises(ValueError):
            mod.create(self.data, self.root / "backup")
        (self.data / "packory.sqlite-wal").unlink()
        (self.data / "blobs" / "skills" / "link").symlink_to(self.data / "packory.sqlite")
        with self.assertRaises(ValueError):
            mod.create(self.data, self.root / "backup")

if __name__ == "__main__":
    unittest.main()
