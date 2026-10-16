#!/usr/bin/env python3
"""Validate public README navigation and local links without external packages.

This is a documentation check, not a product or security certification.
"""
from pathlib import Path
import re
import sys
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1]
PRIMARY = "readme.md" if (ROOT / "readme.md").is_file() else "README.md"
INDEX = "docs/INDEX.md" if (ROOT / "docs/INDEX.md").is_file() else "docs/README.md"
FILES = [PRIMARY, "README.de.md", INDEX]
FILES += [p.relative_to(ROOT).as_posix()
          for p in sorted((ROOT / ".github/ISSUE_TEMPLATE").glob("*.md"))]
MD = re.compile(r"""!?\[[^\]\n]*\]\(([^)\s]+)(?:\s+["'][^"']*["'])?\)""")
HTML = re.compile(r"""<(?:a|img)\b[^>]*?\b(?:href|src)=["']([^"']+)["']""", re.I)
EMAIL = re.compile(r"\b[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}\b", re.I)

def main():
    errors = []
    for name in FILES:
        file = ROOT / name
        if not file.is_file():
            errors.append(f"Missing document: {name}")
            continue
        source = file.read_text(encoding="utf-8")
        for raw in MD.findall(source) + HTML.findall(source):
            address = raw.strip("<>")
            url = urlsplit(address)
            if url.scheme or url.netloc or address.startswith(("#", "//")) or not url.path:
                continue
            target = (file.parent / unquote(url.path)).resolve()
            if not target.is_relative_to(ROOT) or not target.is_file():
                errors.append(f"{name}: broken relative link {address}")
        if any(not e.lower().endswith("@users.noreply.github.com")
               for e in EMAIL.findall(source)):
            errors.append(f"{name}: review personal/contact email")
        if source.count(chr(96) * 3) % 2:
            errors.append(f"{name}: unbalanced code fence")
    for name, expected in (
        (PRIMARY, "[Deutsch](README.de.md)"),
        ("README.de.md", f"[English]({PRIMARY})"),
    ):
        file = ROOT / name
        if file.is_file():
            source = file.read_text(encoding="utf-8")
            if expected not in source:
                errors.append(f"{name}: missing language link")
            if "[![CI](" not in source or "[![License:" not in source:
                errors.append(f"{name}: missing CI/license badge")
    for error in errors:
        print("ERROR: " + error)
    print(f"Public docs: {len(FILES)} checked, {len(errors)} issues")
    return int(bool(errors))

if __name__ == "__main__":
    sys.exit(main())
