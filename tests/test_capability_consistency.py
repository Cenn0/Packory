"""Prevent contradictions between released capabilities and public protocol guidance.

This is a documentation/API consistency gate, not native protocol certification.
"""
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[1]


def contents(relative):
    return (ROOT / relative).read_text(encoding="utf-8")


class CapabilityConsistencyTests(unittest.TestCase):
    def test_maven_inherited_coordinates_and_plugin_metadata(self):
        capabilities = contents("src/Packory/RegistryCapabilities.cs")
        guide = contents("docs/maven.md")
        matrix = contents("docs/support-matrix.md")
        self.assertIn("parent-inherited POM coordinates and plugin-prefix metadata", capabilities)
        self.assertNotIn("no proxy, plugin-prefix metadata", capabilities)
        self.assertIn("A POM can inherit `groupId` and `version`", guide)
        self.assertIn("Group-level plugin-prefix metadata is supported", guide)
        self.assertNotIn("Parent-inherited GAV, Maven plugin/group metadata", guide)
        self.assertIn("parent-inherited POM coordinates, plugin-prefix metadata", matrix)
        self.assertNotIn("No multi-file atomic publication, Central proxy, group/plugin-prefix", matrix)
        self.assertIn("No multi-file atomic publication, Central proxy", matrix)

    def test_npm_private_mutable_tags_are_merged_but_unpublish_is_not(self):
        capabilities = contents("src/Packory/RegistryCapabilities.cs")
        ledger = contents("docs/experimental-graduation.md")
        english = contents("README.md")
        german = contents("README.de.md")
        self.assertIn("mutable scoped dist-tags", capabilities)
        self.assertIn("audited mutable scoped dist-tags (merged #115)", ledger)
        self.assertNotIn("pending CI and merge", ledger)
        self.assertIn("durable scoped mutable dist-tag API", english)
        self.assertIn("Feed-begrenzte Dist-Tags", german)
        self.assertIn("Unpublish bleibt deaktiviert", german)

    def test_private_cargo_yank_is_scoped_without_claiming_full_graduation(self):
        capabilities = contents("src/Packory/RegistryCapabilities.cs")
        ledger = contents("docs/experimental-graduation.md")
        matrix = contents("docs/support-matrix.md")
        self.assertIn("Private hosted sparse feeds with scoped yank/unyank", capabilities)
        self.assertIn("no crates.io proxy, owner management or broad client matrix", capabilities)
        self.assertIn("V14 private feed-local mutable yank overlay", ledger)
        self.assertIn("scoped mutable private yank state (V14)", matrix)
        for filename in ("README.md", "README.de.md"):
            self.assertIn("V14", contents(filename))

    def test_public_release_recommendation_remains_explicitly_false(self):
        capabilities = contents("src/Packory/RegistryCapabilities.cs")
        roadmap = contents("docs/roadmap-v5.md")
        self.assertIn("productionRecommended = false", capabilities)
        self.assertIn('releaseQualification = "experimental"', capabilities)
        self.assertIn("**not** generally production-qualified", roadmap)
        self.assertIn("not a native package protocol", capabilities)


if __name__ == "__main__":
    unittest.main()
