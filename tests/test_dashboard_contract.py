"""Verify the static UI accurately advertises only existing experimental adapters."""
from html.parser import HTMLParser
from pathlib import Path
import re
import unittest

ROOT = Path(__file__).resolve().parents[1]
PAGE = ROOT / "src/Packory/wwwroot/index.html"
SERVER = ROOT / "src/Packory/Program.cs"


class RegistryParser(HTMLParser):
    def __init__(self):
        super().__init__()
        self.filters = []
        self.options = []
        self.active_select = None

    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "select":
            self.active_select = attrs.get("id")
        elif tag == "option" and self.active_select == "kind":
            self.options.append(attrs.get("value"))

    def handle_endtag(self, tag):
        if tag == "select":
            self.active_select = None


class StaticDashboardContractTests(unittest.TestCase):
    def test_native_filter_matches_program_supported_catalog_kinds(self):
        page = PAGE.read_text(encoding="utf-8")
        parser = RegistryParser()
        parser.feed(page)
        expected = {"nuget", "npm", "cargo", "pypi", "oci", "skill"}
        self.assertEqual(expected, set(parser.options) - {""})
        self.assertEqual(len(expected) + 1, len(parser.options))
        program = SERVER.read_text(encoding="utf-8")
        for ecosystem in expected:
            self.assertIn('"' + ecosystem + '"', program)

    def test_experimental_labels_and_real_paths(self):
        page = PAGE.read_text(encoding="utf-8")
        self.assertNotIn("Packory MVP", page)
        self.assertNotIn("hub · MVP", page)
        self.assertIn("Experimental · Single-writer reference", page)
        for url in ("/nuget/v3/index.json", "/npm/", "/cargo/index/",
                    "/pypi/simple/", "/v2/", "/api/skills"):
            self.assertIn("<code>" + url + "</code>", page)
        self.assertIn("docs/support-matrix.md", page)


if __name__ == "__main__":
    unittest.main()
